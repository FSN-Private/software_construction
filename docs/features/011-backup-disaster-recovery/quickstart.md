# Feature 011 validation quickstart

This is the planned validation procedure. It records no passing result. Local tests can validate contracts, integrity handling, and failure behavior; only a separately authorized AWS drill can demonstrate cross-account recovery, alert delivery, and the binding RPO/RTO.

## Local validation

Local validation requires Node.js 24.20.0 with npm 11.19.0, the .NET SDK pinned in `global.json` (10.0.400 with latest-patch roll-forward), and Docker Compose for database/object-store integration tests. No AWS credentials are required or should be supplied to these test commands.

After implementation adds the feature-specific commands, run from the repository root:

```powershell
npm ci --ignore-scripts
npm --prefix packages/infrastructure ci --ignore-scripts
npm run test:backup
npm run test:backup-integration
npm run test:ingest
npm run test:version-control
npm run validate:infrastructure
npm run test:reliability
node tests/infrastructure/recovery/recovery-contract.mjs
```

`test:backup`, `test:backup-integration`, and `tests/infrastructure/recovery/recovery-contract.mjs` are planned and do not exist in the current repository yet. The implementation tasks must add the commands around `tests/recovery/Platform.Recovery.Tests.csproj`, `tests/integration/recovery/run.mjs`, and synthesized recovery-template/workflow checks, then put deterministic local checks in CI. The tests must cover data-class coverage and immutable identity checks; a read-only exported PostgreSQL snapshot/LSN paired with the transactional acknowledged-change watermark; complete enumeration of exact S3 key/version/hash/length dependencies from that same snapshot; partial-dump/copy/signature failure withholding the verified watermark; new-version streamed hash/length checks; reuse of prior destination receipts only when exact identity, retention, and access policy still match; periodic full rehash; stale, missing, corrupt, and inaccessible points; rejection of promotion from an isolated restore before owner approval; protection of the last verified copy; owner and authorization validation; audited purge of eligible primary and recovery copies with later recovery denied; and alert fault timing. Alert tests should verify a warning no later than 45 minutes of recovery-point age and delivery within 15 minutes. A local object-store emulator or mocks can exercise application behavior, but cannot prove AWS replication, KMS policy separation, archive policy, or notification delivery.

The existing reconstruction-manifest verifier is also useful against two independently obtained manifests and recovered object bytes:

```powershell
dotnet run --project apps/ingest-worker/Platform.Ingest.Worker.csproj -- --verify-manifest <trusted.json> <recovered.json> <object-directory>
```

The recovered object directory must contain only the authoritative objects under their `sha256/<digest>` relative keys. Retain the command version, manifest IDs and digests, snapshot ID, operator, timestamp, and result. A successful manifest comparison is necessary integrity evidence, but it does not establish independent AWS access or a completed regional-recovery drill. See [the reconstruction-manifest runbook](../../runbooks/reconstruction-manifest.md).

The existing `npm run test:ingest` and `npm run test:version-control` commands protect the source data and commit-graph behavior that recovery depends on. Their success, infrastructure synthesis, and the committed `tests/infrastructure/protection-evidence.json` do not constitute backup or disaster-recovery evidence; that JSON describes GitHub repository protection only.

## Recovery scale and cold storage

The parent program's capacity baseline is at least 100,000 images and 1,000,000 XMP/content records. Record the exact object count and byte total from the frozen recovery manifest for every measured restore. The specifications do not set a maximum library byte size, so these counts alone cannot prove the 24-hour RTO for an arbitrary library. The owner must confirm the workload and byte envelope the release claim covers; do not invent a cap or claim that a smaller fixture represents a larger library.

The current planning proposal transitions old content object versions to S3 Glacier Instant Retrieval after 90 days, with recent database dumps, catalogs/manifests, configuration, release artifacts, signatures, and owner-access evidence kept warm. Instant Retrieval provides millisecond object access, but has a 90-day minimum storage duration and a 128-KB minimum billable object size; the cost estimate must account for small XMP objects, lifecycle timing, and retrieval charges ([AWS storage classes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/glacier-storage-classes.html)). The recovery copy still needs an independently usable recent point and a measured full restore path. This is a proposal pending owner approval, not a binding decision.

## AWS-only release evidence

The cloud drill requires a separately provisioned recovery AWS account and secondary region, independently governed recovery operator access, encrypted/versioned recovery storage, recovery-owned KMS permissions, replication policies, alert delivery, and a safe representative library. The current v1 proposal opens one read-only exported PostgreSQL MVCC snapshot, records its snapshot/LSN and latest transactional acknowledged-change watermark, and enumerates all authoritative exact `content_objects` key/version/hash/length references plus other class counts from that same view. A pinned PostgreSQL 18 `pg_dump` uses that exported snapshot. Newly copied object versions receive streamed hash/length verification; a prior receipt may be reused only for the exact immutable destination key/version/hash/length when retention, access, freshness, and signature checks still pass. Periodic and full-drill rehashes verify bytes again. The recovery-account verifier closes every dependency before it signs and advances the verified watermark. A capture starts every 15 minutes, but an incomplete dump, S3 closure, signature, or verification does not advance that watermark; the evidence must show the end-to-end age of the last fully verified point stays within one hour. The repository names `ap-southeast-1` as primary and `ap-southeast-2` as recovery in bootstrap evidence, but that file is a required-input checklist, not proof either account or its recovery resources exist. The drill operator must use the approved recovery identity and demonstrate recovery without primary-region credentials or reads. After implementation, the recovery runner is invoked with `dotnet run --project apps/recovery-worker/Platform.Recovery.Worker.csproj -- <approved-command>`; the exact command and flags must be documented by its implementation task before any cloud run.

Before and during an authorized drill, preserve a redacted, tamper-evident report containing:

- account aliases and regions, drill/run ID, operator, approval, and declared loss time;
- frozen source and recovered manifest digests, recovery-point timestamp, per-class object counts and bytes, and every validation result;
- the exported PostgreSQL snapshot ID/LSN and its timestamp, transactional acknowledged-change watermark, full exact-S3 closure manifest digest/counts/bytes, signature and verification result, copy completion time, latest acknowledged source mutation, and verified recovery-point age (at most one hour); plus evidence that the simulated outage prevented use of the primary region;
- restore start/end times, recovered owner-access check, measured RTO (at most 24 hours), and evidence that the restore remained isolated until explicit promotion;
- fail-closed cases for corrupt data, wrong owner/policy, and blocked final-copy deletion; and
- injected overdue/incomplete/integrity failures with alert detection and owner/operator delivery timestamps (within 15 minutes), plus corrective actions and follow-up owner.

For the permanent-purge acceptance case, record that the fixture passed its 90-day retention eligibility and had no remaining hold, identify the exact authorized object/version scope removed in both accounts, prove that normal and recovery workflows deny retrieval afterward, and preserve the durable purge-completion audit event. Keep media, XMP payloads, geolocation, tokens, and credentials out of reports and logs.

No AWS account creation, deployment, data migration, restore, purge, or fault injection is authorized by this feature specification. This section is an evidence contract for a future owner-authorized non-production drill; local validation must not be reported as satisfying it. AWS account identifiers, approved IAM Identity Center/role access, recovery-owned key policy, primary/recovery destinations, and alert subscription confirmation are external prerequisites; never place credentials in this repository.
