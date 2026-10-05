# Feature 011 validation evidence

Status: local implementation and verification in progress. No production recovery qualification has been issued. The owner approved local implementation on 2026-10-05; deployment, persistent production migration, live restore, purge and cloud drills require separate authorization.

## Recorded local checks

| Date | Command | Result and scope |
|---|---|---|
| 2026-10-05 | `dotnet list apps/recovery-worker/Platform.Recovery.Worker.csproj package --vulnerable --include-transitive` | PASS: restored the pinned worker and referenced projects against `https://api.nuget.org/v3/index.json`; no known vulnerable packages reported. This advisory check does not establish worker functional readiness. |
| 2026-10-05 | `dotnet nuget verify <five pinned recovery-worker AWS SDK .nupkg files> --all` | PASS: NuGet.org repository signatures and package content hashes for DynamoDBv2 4.0.107, KeyManagementService 4.0.100.15, SecretsManager 4.0.100.15, SecurityToken 4.0.101.1 and SQS 4.0.100.15. Local package metadata identifies Amazon Web Services and Apache-2.0 licensing for all five. |
| 2026-10-05 | `npm --prefix apps/web run typecheck` | PASS: backup-health component and navigation type checking. |
| 2026-10-05 | `dotnet run --project tests/recovery/Platform.Recovery.Tests.csproj --no-restore` | PASS, independently repeated: canonical manifest identity, unsupported/malformed/noncanonical input, owner/timestamp/hash/class validation, exact-version byte verification, replay, corruption, interrupted reads, bad destination bytes and missing retention/public-access checks. Fixed-clock checks enforce 90 days from write admission and allow verified reads after day 91. These storage tests use an SDK fixture and do not certify AWS policy. |
| 2026-10-05 | `npm --prefix apps/web run build` | PASS: production web build. Existing Vite notice about the runtime configuration script remains. |
| 2026-10-05 | `npm --prefix apps/web run test` | PASS: existing web source baseline checks. |
| 2026-10-05 | `npm --prefix apps/web run test:accessibility` | PASS: existing source and rendered accessibility checks. |
| 2026-10-05 | From `apps/web`: `node node_modules/prettier/bin/prettier.cjs --check . --end-of-line auto` | PASS: all web files. The standard format command reported 35 files because this Windows checkout converts baseline LF to CRLF; the committed baseline `Gallery.tsx` passes the standard configuration. No unrelated formatting rewrite was performed. |
| 2026-10-05 | `npm --prefix apps/web run test:browser -- recovery.spec.ts` | PASS: nine checks, three scenarios each in Chrome, Edge and Firefox. Covers authenticated health, semantic table and keyboard refresh, gallery-context preservation, missing/overdue classes and removal of stale success after refresh failure. Provider details are excluded from rendered output. |
| 2026-10-05 | Selected Chrome tests from `frontend.spec.ts`, `gallery.spec.ts` and `exports.spec.ts` using `--grep 'secondary import\|session expiration\|unsaved drafts\|branch switch blocks\|gallery additions across\|submission retry retains'` | PASS: six existing navigation/session regressions, including dirty-state guards, preserved browsing context and download-session revocation. |
| 2026-10-05 | `docker compose -f tests/integration/ingest/compose.yml up -d`, then `INGEST_INTEGRATION=1 dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj --no-restore` | Delegated M1 rehearsal PASS: additive migration 022 applied twice, transactional failure rollback, expansion, old-reader compatibility and stable identity; existing PostgreSQL/S3 ingest checks passed. Environment variable notation is descriptive; in PowerShell set and subsequently remove `$env:INGEST_INTEGRATION`. |

These checks use local fixtures and services. They do not establish AWS account separation, production permissions, notification delivery, workload capacity, cold retrieval, or achieved RPO/RTO. Requirement-level results will be added after the corresponding implementation is verified.

The orchestrator repeated the M1 migration rehearsal successfully: transactional migration rollback, apply-twice/old-reader compatibility, association correction, exact-byte retrieval/provenance, lifecycle day-90/holds/shared-byte cases, full reconstruction relationships, owner-isolated HTTP behavior and the existing six-object ingest retry/quarantine/atomic/fault matrix all passed.

M1 follow-up: the source adapter now checks the provider's returned version ID before copying. A same-byte/wrong-version fixture is rejected before any destination write. SDK access-denial fixtures reject both copying and recovery reads. The recovery executable checks passed again after this change; these denial fixtures do not certify actual KMS policy.

Signed-contract checkpoint: the orchestrator independently ran the recovery executable checks and `dotnet format packages/recovery/Platform.Recovery.csproj --verify-no-changes --no-restore --include packages/recovery/RecoveryEvidenceContracts.cs packages/recovery/RecoveryCatalogContracts.cs packages/recovery/RecoveryQualificationContracts.cs`; both passed. Checks cover pinned RSA-PSS verification, canonical bounded catalogs, failure summaries without a successful point, signer/digest rejection, and release qualification expiry/artifact binding. Qualification now derives conservative RPO/RTO from signed loss/cutoff/owner-ready timestamps, rejects contradictory reported durations and chronology, and requires at least 100,000 images and 1,000,000 content records. Qualification fixtures are synthetic unit inputs, not cloud evidence.

## External completion and release gates

The following evidence is unavailable in this local implementation session and remains BLOCKED, not passed:

- Independently administered recovery-account identity, recovery region, approved operator/MFA and externally provisioned least-privilege capture database credential.
- Actual cross-account KMS, IAM, exact-version retention and independent signed trust-anchor validation under primary-account denial.
- Full library and owner-access restoration, including an actual measured RPO of at most one hour and RTO of at most 24 hours at the approved workload.
- Actual missing/overdue/integrity and worker-loss notification delivery within the specified interval.
- Actual cold-media retrieval, independent purge reconciliation, and supported screen-reader acceptance.
- Approved measured byte/growth/budget envelope and health-query p95 at the approved workload.
- Attributable signed successful cloud qualification no older than three calendar months, bound to the deployed recovery-relevant image, template and migration digests.

Until these gates have evidence, feature acceptance and production recovery enablement remain incomplete. Synthetic qualification must not replace them.

## Automatic approval review disposition

Automatic approval review rejected creating a database reader with SELECT grants on all current and future tables, citing a persistent broad read-access expansion. That action was not applied. Implementation instead accepts a separately provisioned read-only credential and documents explicit current protected-table grants; unclassified future tables and live privilege provisioning remain deployment prerequisites. Unaffected local implementation continues.

Automatic approval review also rejected routing two buckets through the shared normal storage adapter, citing its broader access/service-disruption impact. That patch was not applied. The recovery-only materializer instead verifies retained exact versions, copies them into a separate isolated target bucket using the existing `sha256/{digest}` layout, and rebinds mutable physical locators only in the database clone. Immutable graph, owner IDs and content digests remain preserved, with attributable source-to-target mapping required for validation. The normal API retains its single-bucket adapter and cannot mutate protected recovery copies.

Automatic approval review rejected a caller-visible recovery identity claim as a verification bypass. That approach was not applied. The replacement uses server-only principal state populated after the stored mapping and independently pinned RSA-PSS approval receipt are verified; caller claims cannot approve a mapping. Functional verification of this replacement remains pending.
