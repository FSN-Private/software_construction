# Feature 011 validation evidence

Status: local implementation and verification in progress. No production recovery qualification has been issued. The owner approved local implementation on 2026-10-05; deployment, persistent production migration, live restore, purge and cloud drills require separate authorization.

## Recorded local checks

| Date | Command | Result and scope |
|---|---|---|
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
