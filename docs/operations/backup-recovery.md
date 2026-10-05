# Backup and recovery operations

**Status:** implementation in progress; AWS qualification is not yet evidenced. This guide records the intended boundaries and commands. It does not authorize account creation, deployment, restore, cold transition, promotion, or purge.

## Trust and data flow

| Identity | Runs in | Allowed work | Explicitly denied |
|---|---|---|---|
| `platform-<env>-recovery-capture` ECS task role | Primary VPC, ap-southeast-1 | Read the externally provisioned `platform_recovery_reader` database secret; read the primary database; write new capture artifacts under `captures/` in the primary capture bucket; publish safe capture status | Any recovery-account ARN; primary media deletion; source credential administration |
| `platform-<env>-recovery-source-read` assumed role | Primary account, ap-southeast-1 | Read capture artifacts under `captures/` and exact `sha256/*` source object versions; decrypt only with the primary storage KMS key | Writes or deletes in either account; database credentials; IAM/KMS administration |
| `platform-<env>-recovery-copy` ECS task role | Recovery VPC, ap-southeast-2 | Assume the one primary source-read role while running `copy`; consult recovery tombstones/intents; copy and verify into recovery-owned storage; emit signed safe observations | Recovery object deletion, governance bypass, key administration, owner approval, source database login |
| `platform-<env>-recovery-verify` ECS task role | Recovery VPC, ap-southeast-2 | Read recovery copies, validate isolated database/object state, write recovery evidence, run a drill using recovery-only material | `sts:AssumeRole`, primary-account reads, promotion, purge, or retention bypass |
| `RecoveryDrill` GitHub OIDC role | GitHub Actions | Start only the recovery drill task in the protected `recovery-drill` environment and observe its result | Primary credentials, source reads, arbitrary task definitions, promotion, purge, or application deployment |

The capture database secret is a deployment prerequisite, not the primary runtime database secret. Provision a dedicated read-only LOGIN with explicit SELECT grants for the current protected tables and sequences. Do not use default privileges, a schema-wide grant, or a mutable application login. Store JSON fields `username` and `password` in Secrets Manager, encrypted with the AWS-managed Secrets Manager key; grant only the capture task role `GetSecretValue`. The database credential bootstrap is a separately authorized operation. Never point the capture task at `platform_admin` or `platform_runtime` credentials.

The recovery account and its KMS key are independent from the primary account. Recovery S3 is versioned, private, encrypted by the recovery KMS key, Object Lock governance-protected for 90 days, and has no automatic expiration of the final copy. Only the payload prefix transitions to S3 Glacier Instant Retrieval after 90 days. Database dumps, manifests, catalogs, configuration, release images and signed evidence stay in Standard. The copy and verification roles have no `DeleteObjectVersion` or Object Lock bypass permission. A separate human MFA-approved purge path rechecks holds and durable writer settlement before exact-version deletion.

The recovery copy task is the only process that writes protected recovery copies. Before every external write and before sealing a point, it checks the recovery-owned tombstone/write-intent table. It assumes primary read credentials only while copying. A regional drill, restore, validation, or owner-access check runs without a primary role or primary network dependency. The primary SQS mirror contains signed, redacted observations only; the API verifies the signature and never receives recovery-object access.

## Capture and health contract

The primary capture task runs every 15 minutes in the source VPC. It uses one PostgreSQL exported MVCC snapshot for the recovery inventory and PostgreSQL 18 `pg_dump`; the same transaction records cutoff, LSN, migration digest, and the scoped committed-change watermark visible in that snapshot. Protected mutations append their ledger entry and serialize the owner/library watermark in their own transaction, so an allocated sequence from an uncommitted transaction cannot advance recovery coverage. Capture uploads a hashed database dump and canonical dependency manifest to the primary capture bucket. It does not copy media into that bucket.

Migration 022 backfills `recovery_change_watermarks` for owner/library scopes visible in existing protected tables. A completely empty canonical library has no database row from which that migration can infer its identity. Before enabling capture for such a scope, the separately authorized database provisioning administrator must register the exact deployment-configured owner and library once:

```sql
INSERT INTO recovery_change_watermarks(owner_id, library_id, last_sequence, latest_changed_at_utc)
VALUES ('<canonical-owner-id>', '<configured-library-id>', 0, NULL)
ON CONFLICT (owner_id, library_id) DO NOTHING;

SELECT owner_id, library_id, last_sequence, latest_changed_at_utc
FROM recovery_change_watermarks
WHERE owner_id = '<canonical-owner-id>' AND library_id = '<configured-library-id>';
```

Replace both placeholders with the exact provisioned values and execute through the authorized provisioning session, never through the capture login. If the configured scope has no durable watermark row, capture fails closed with `change_watermark_missing`; do not synthesize sequence zero or create the row from a read-only capture process. Confirm the row exists before scheduling the first capture.

The recovery copy task polls on the same 15-minute interval. It follows the manifest's exact S3 version IDs, computes SHA-256 from the actual source and destination bytes, records destination version IDs and fresh access/retention checks, and commits the recovery point only after full closure verification and recovery-key signing. A receipt can be reused only for an identical immutable source key/version/digest/length mapped to the same destination version after fresh existence, access and policy checks. A first seed and every full drill rehash all bytes. The worker must reject an unresolved/unknown write outcome or pending purge fence.

The 15-minute interval is not an RPO guarantee. Measure the declared-loss instant minus the snapshot cutoff of the latest fully verified point; confirm the point's transactional acknowledged-change sequence has no missing entry through the cutoff. Alert when verified-point age reaches 45 minutes and record actual notification delivery within the following 15 minutes. A regional release qualification still requires a real measured RPO of 3,600 seconds or less and RTO of 86,400 seconds or less at the owner-approved library-byte envelope. Missing telemetry, incomplete closure, an unverifiable signer, an alert-delivery failure, or any lost write intent makes health unhealthy.

The primary's former 26-hour `BackupAgeHours` placeholder alarm is not recovery evidence. The recovery account emits `Platform/Recovery/VerifiedPointAgeSeconds`; its 45-minute alarm treats missing data as a breach. `CopyFailures` and `VerificationFailures` are separate alarms. Preserve CloudWatch delivery records and operator acknowledgement with the drill package.

## Deployment and qualification prerequisites

The default foundation deployment is unchanged. Recovery is opt-in and needs a separate target account/profile. Before enabling schedules, an owner must provide and verify:

- Distinct primary and recovery account IDs/aliases, recovery-account administrative MFA custody, and ap-southeast-1 / ap-southeast-2 access.
- The deployed primary bucket names, primary KMS key ARN, observation-queue ARN/URL, and dedicated read-only database secret ARN.
- The recovery release repository, recovery-worker bootstrap image, protected GitHub `recovery-drill` environment, and recovery-account GitHub OIDC role.
- A verified recovery-owner mailbox, independent alert recipients, offline audit-key fingerprint/checkpoint custody, recovery Cognito owner provisioning and new-issuer identity-mapping approval path.
- The measured seed size/object histogram and change rate for 100,000 images and 1,000,000 content/version records, the capture scratch requirement, monthly budget, and owner-selected recovery database class.
- A full initial seed, full byte verification, successful isolated restore, signed audit-anchor verification, and actual alert-delivery test.

Deploy and enable the workflow in stages only after explicit deployment authorization:

1. Synthesize both stacks in offline mode and review them. For a real deployment, provide every non-placeholder context input and the actual measured database/capture sizing.
2. Deploy the recovery-account stack with schedules disabled. Publish immutable API, ingest-worker, renderer, and recovery-worker images into its ECR repositories by digest; provision the independent MFA operator and `recovery-drill` GitHub environment. Keep the recovery Cognito pool private and self-signup disabled.
3. Deploy the primary recovery integration. Bootstrap the dedicated read-only DB login in a separately reviewed session. Validate that its password is in the configured secret and it cannot write or administer the source database.
4. Perform the full recovery seed, byte rehash, isolated restore, independent owner/foreign-subject checks, and alert test. Verify the first signed recovery checkpoint against the owner's offline pin.
5. Enable both 15-minute schedules. Confirm the first verified point and SQS observation import. Keep release/promotion/purge disabled until a full AWS drill qualifies the selected byte envelope.

Example local, offline-only synthesis:

```powershell
cd packages/infrastructure
npm run build
npx cdk synth --no-lookups --context offline=true --context recovery=true --context environment=nonprod --context region=ap-southeast-1 --quiet
```

The real two-account `cdk deploy`, image push, secret bootstrap, migration, and schedule enablement require independently supplied role/account values and are not run by local validation. Recovery code never receives those credentials through the primary runtime.

## Restore and identity rules

For accidental loss, first select the latest verified point before the reported event and create an isolated restore session. Do not replace or overwrite the live library. The owner reviews validation results for original/XMP byte hashes, metadata, graph manifests, audit continuity, lifecycle state, holds, purge fences, and export cleanup. Only the recovery MFA identity can approve a plan-bound promotion after validation and a fresh live-generation check.

For regional loss, declare the outage time, deny primary account credentials and endpoints, and use the recovery-only runner, database, bucket, ECR image digests, configuration, KMS key, and Cognito pool. Restore the point into the isolated recovery database and namespace; verify the primary endpoint is unreachable. Independently reauthenticate the owner in the recovery Cognito pool, then bind the verified `(issuer, audience, subject)` tuple to the preserved canonical `owner_id` in recovery configuration. Never accept an `owner_id` from a request and never rewrite historical graph/database rows. Verify owner access and foreign-subject denial before any reviewed cutover.

Do not configure automatic reverse replication when the primary returns. Keep the recovered environment isolated until a separate reconciliation and cutover review.

## Retention, cold retrieval, and purge

Payload copies remain Standard for 90 days, then become Glacier Instant Retrieval; they are online, but retrieval and small-object minimum-size charges apply. Glacier Instant Retrieval has a 90-day minimum storage duration and 128 KiB minimum billable object size. Bulk restore is still bounded by measured object count, concurrent GET rate, KMS requests, network bandwidth, and hash verification. Database/configuration/evidence stays warm. Never configure lifecycle expiration for recovery payload or the last verified copy.

Purge currently means service-controlled media payload only: originals, XMP, derivatives, exports, temporary copies and multipart remnants. Historical hashes, paths, graph manifests, audit evidence and descriptive metadata remain in database snapshots. Tell the owner this before collecting purge approval. The independent recovery MFA identity approves a signature over exact scope and plan digest; primary API or database claims cannot authorize deletion. Keep status pending until source and recovery versions, in-flight write intents, archive retrieval, and Feature 012 export cleanup are all fully enumerated and acknowledged. Never bypass governance retention or purge the last verified copy. Full metadata erasure requires a separately approved graph-redaction design.

## Cost and abort model

Do not quote a dollar total while the byte envelope and AWS budget are missing. For a measured inventory, record `B_standard` (recovery payload GiB younger than 90 days), `B_GIR` (older payload GiB), object count `N` and the `<128 KiB` histogram, monthly new bytes `G`, quarterly bytes retrieved `D`, number of copy/read/KMS requests `Q`, capture dump size and duration, worker vCPU/memory hours `H`, recovery database hours `R`, and NAT processed bytes `X`.

Estimate monthly recovery spend as:

`B_standard × Sydney Standard rate + B_GIR × Sydney Glacier Instant Retrieval rate + (small-object billable-size adjustment) + G × inter-region transfer rate + Q × S3/KMS/request rates + D × GIR retrieval rate + H × Fargate rates + R × RDS rates + NAT gateway hours/processing for X + ECR GB-month + retained CloudWatch/SNS/DynamoDB usage.`

Use the live AWS Pricing Calculator for ap-southeast-2 and retain the dated inputs/estimate with the approval. The owner must set a monthly budget and a measured byte-growth envelope before enabling continuous schedules. Stop schedule enablement or growth when actual estimate exceeds that budget, the last verified point age exceeds 45 minutes without delivered alert, seed/copy verification is incomplete, or measured full restore cannot reach 24 hours. Increasing a threshold never converts a failed result into qualification.

## Drill commands and evidence

The container command interface is `dotnet run --project apps/recovery-worker/Platform.Recovery.Worker.csproj -- capture`, `-- copy`, `-- verify --point-id <uuid>`, and `-- drill --run-id <uuid>`. Isolated restore uses `-- restore-isolated --point-id <uuid> --session-id <uuid>` followed by `-- validate --session-id <uuid>`. The manual/quarterly drill has recovery-only credentials and cannot promote or purge. Store the resulting canonical, recovery-key-signed qualification in the encrypted recovery evidence prefix; validate it against a public verification key/fingerprint provisioned outside the artifact and outside both accounts.

Retain run ID, source revision, recovery-relevant template and image digests, primary/recovery aliases and regions, declared loss and restore timestamps, cutoff/LSN/change watermark, point/checkpoint digest, class counts/bytes, source-denial proof, owner/foreign access results, hash/graph/XMP/metadata/audit checks, alert detection and delivery timestamps, retention/purge fixture outcomes, RPO/RTO seconds and corrective actions. A failed or absent observation is a failure, not a fixture that can be upgraded to success.

Every quarter, a named recovery operator records corrective actions with an owner and due date. Close each action with independent verification. Production promotion requires a signed full qualification no older than three calendar months, matching the exact recovery-relevant release/infrastructure digests, RPO <= 3,600 seconds, RTO <= 86,400 seconds, and no unresolved blocking action.
