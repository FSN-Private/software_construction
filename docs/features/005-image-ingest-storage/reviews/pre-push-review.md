# Adversarial Specification Review

**Status**: Round 1 findings fixed; awaiting independent Round 2
**Reviewer model**: gpt-5.5
**Review date**: 2026-09-03
**Base**: `feature/003-darktable-version-control`
**Scope**: Feature 005 specification; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| Accepted unassociated XMP could be consumed as active data. | Medium | Parent FR-002; 005 FR-005, AC-009 | **Valid and fixed.** It is quarantine-only and non-authoritative until one deterministic association exists. |
| Recovery manifests omitted key lifecycle and provenance evidence. | Medium | Parent recovery requirements; 005 FR-016, AC-010 | **Valid and fixed.** Manifest and recovery validation now include path, holds, audit, provenance, and handoff identifiers. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

## Final agreement

The alternate reviewer confirmed quarantine-only unassociated XMP and complete recovery-manifest traceability. No valid unresolved finding remains.

**Final gate result: PASS**

## Pre-push review — 2026-09-07

**Status**: blocked | **Reviewer model**: gpt-5.6-terra | **Base**: `93ff52a` | **Reviewed range**: `93ff52a..16c7f46` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

### Round 1

The independent reviewer found no unrelated changes, secrets, deployment mutation, fixture checksum mismatch, or defect in the basic owner/path primitives. The reviewer reproduced the focused unit executable and whitespace check. It found the following unresolved findings:

| Finding | Severity | Affected IDs | Disposition |
|---|---|---|---|
| Authoritative writes do not verify the supplied stream or prevent overwrite; canonical digest casing also differs from the database constraint. | High | T006; FR-002/003/008; AC-001/002; NFR-015 | **Valid.** No fix yet; T006 returned to in progress. |
| The worker does not register or execute the outbox; stale publication cannot retry and activation is not gated on verified storage. | High | T008; FR-007/009; AC-002/007; NFR-002/015 | **Valid.** No fix yet; T008 returned to in progress. |
| Presigned URLs lack persisted owner/session, expiry, length, and checksum constraints. | High | T006; FR-002/008; NFR-003 | **Valid.** No fix yet; T006 returned to in progress. |
| Idempotency/lifecycle headers are only presence checks; replay, key validation, and optimistic concurrency are absent. | Medium | T004; FR-015; NFR-003/015 | **Valid.** No fix yet; T004 returned to in progress. |
| Metrics are declared but not emitted; audit writer, health, and alert path are absent. | Medium | T004/T009; FR-014/015; NFR-004/007/012 | **Valid.** No fix yet; T009 returned to in progress. |
| Migration lacks received-part/target binding, content version, and publication object/repair evidence fields. | Medium | T005; FR-004/016; NFR-008/011/016 | **Valid.** Add before migration rehearsal. |
| Integration container tags are mutable. | Medium | T002; NFR-003/014 | **Valid.** Pin reviewed digests before running integration environments. |
| The focused tests do not cover S3, PostgreSQL, worker registration, or fault paths; one helper accepted any exception. | Medium | T006/T008/T009; AC-002/007; NFR-009/015 | **Valid.** Add exact unit and container-backed tests with the implementations. |
| Fixture README called all materials public-domain despite MIT corpus fixtures. | Low | T003; NFR-014 | **Valid and fixed** in the follow-up review-record commit. |

**Validation evidence**: locked restores and all three project builds passed; the focused unit executable and `npm run test:contract` passed; `git diff --check` passed. The initial locked restore required normal network access only to retrieve NuGet vulnerability metadata. The reviewer confirmed those commands were consistent with the range.

**Final gate result: BLOCKED.** High findings remain unresolved. No push is authorized.

### Round 2 — implementation disposition before follow-up review

| Prior finding | Disposition | Evidence / remaining work |
|---|---|---|
| Authoritative write integrity | **Valid and fixed** for the storage primitive. | `ContentAddress.IsValid` now permits canonical lowercase hexadecimal only; `S3ObjectStore.PutAuthoritativeAsync` verifies the seekable stream against SHA-256/length before upload, sends the base64 checksum, and uses `If-None-Match: *` to prevent overwrite. Focused storage build and unit executable pass. |
| Publication worker/repair protocol | **Valid.** | Still unresolved; no claim is made that T008 is complete. |
| Persisted scoped transfer authorization | **Valid.** | `TransferGrant` now rejects missing owner/session, invalid content, expired grants, and grants over 15 minutes; signed upload headers bind checksum, KMS, and owner metadata. Persisted session-state authorization remains for T006. |
| Idempotency/lifecycle enforcement | **Valid.** | Still unresolved in T004. |
| Telemetry/audit/health/alert path | **Valid.** | Still unresolved in T009. |
| Migration data-model gaps | **Valid.** | Still unresolved in T005. |
| Mutable integration images | **Valid.** | Still unresolved in T002. |
| Test depth | **Valid.** | The original broad-exception helper remains; exact storage and fault tests remain required. |

**Round 2 validation before follow-up**: `dotnet build packages/storage/Platform.Storage.csproj --no-restore` and `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj --no-restore` passed.

### Round 2 — independent follow-up assessment

**Reviewer model**: gpt-5.6-terra | **Reviewed range**: `93ff52a..da3124f` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

The reviewer reproduced `git diff --check`, the storage build, and the focused unit executable. It agreed that the fixture-license correction is resolved and that the earlier specification-only findings remain resolved as specification work. It found no new distinct issue, but explicitly rejected closure of the storage high finding because the new primitive has no registered API/worker call path and lacks S3 behavior tests.

| Finding | Follow-up assessment |
|---|---|
| Authoritative write integrity | **Valid and unresolved.** The primitive now checks canonical digest, verifies local bytes, sends checksum, and uses conditional creation; verified promotion is not wired or proven against S3/MinIO. |
| Publication worker/repair protocol | **Valid and unresolved.** No data source/hosted worker registration; stale `publishing` intents become unclaimable `reconciled`; activation is not gated on object verification. |
| Persisted scoped transfer authorization | **Valid and unresolved.** `TransferGrant` validates only caller-provided data; there is no persisted owner/session/target authorization, content-length constraint, issuance authorization, or call site. |
| Idempotency/lifecycle, observability/audit/health/alerts, migration gaps, mutable images, and test depth | **Valid and unresolved.** The reviewer confirmed the Round 1 evidence remains applicable. |

**Final gate result: BLOCKED.** The reviewer explicitly found valid unresolved high findings. No push is authorized.

### Round 3 — implementation disposition before follow-up review

| Prior finding | Disposition | Evidence / remaining work |
|---|---|---|
| Publication worker/repair protocol | **Valid.** | The host now registers `NpgsqlDataSource`, `PublicationOutbox`, and `PublicationRepairWorker`; stale publishing intents return to `pending-publication` with repair evidence, and metadata activation requires `object_state = promoted` plus pending content, which transitions atomically to authoritative. Verified object promotion still needs a worker handler and integration fault tests. |
| Migration gaps | **Valid.** | Sessions now retain owner, target key, and received parts; content retains storage version; intents retain object state and repair evidence. Migration rehearsal remains T024. |

**Round 3 validation before follow-up**: worker/API builds and the focused unit executable passed; `git diff --check` passed.

### Round 3 — independent follow-up assessment

**Reviewer model**: gpt-5.6-terra | **Reviewed range**: `93ff52a..5bcd438` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

The reviewer reproduced the worker/API builds, focused unit executable, and whitespace check. It found no regression in the specification-only or fixture-provenance dispositions and no new distinct issue. It confirmed the migration fields cited in Round 1 are now present and that worker registration, stale-intent requeue, and metadata gating are meaningful partial remediation. It nevertheless found these valid unresolved findings:

- **High T006:** no API/worker call path or S3/MinIO fault evidence for verified immutable publication.
- **High T008:** the hosted worker only reconciles; no handler calls `ClaimAsync`, verifies/promotes an object, or calls atomic activation.
- **High T006:** transfer grants remain caller supplied and lack persisted owner/session/target, issuance authorization, and length enforcement.
- **Medium T004/T009:** durable idempotency/version enforcement, audit, outcome telemetry, health, and alert path remain absent.
- **Medium T002:** integration images remain tag-pinned rather than digest-pinned.
- **Medium T006/T008/T009:** no PostgreSQL/MinIO/worker fault coverage; the focused test does not exercise the database repair behavior.

**Final gate result: BLOCKED.** No push is authorized.

### Round 4 — independent follow-up assessment

**Reviewer model**: gpt-5.6-terra | **Reviewed range**: `93ff52a..51b09ac`

The reviewer reproduced the worker build, focused unit executable, and whitespace check. It confirmed the previous no-handler finding is superseded because the worker now invokes claim, promotion, and metadata activation. It found the following valid unresolved high findings:

- A single payload promotion can activate every item attached to an intent; promotion must be per item or single-item intents must be enforced (T008, FR-009, AC-007).
- Conditional create treats legitimate duplicate authoritative content as a worker failure; reuse must verify existing checksum/length (T006/T008, AC-002/007).
- A malformed payload, S3 failure, checksum failure, or metadata conflict escapes the hosted loop and stops all publication; failures must be classified/persisted per intent (T008, AC-007, NFR-002/015).
- Staged content is copied into an unbounded `MemoryStream`, which is unsafe for RAW content; promotion requires bounded streaming/disk/server-side copy (T006, NFR-001/002/015/016).

The reviewer reconfirmed unresolved persisted transfer authorization, durable idempotency/lifecycle, telemetry/audit/health/alerts, digest-pinned integration images, and PostgreSQL/MinIO fault coverage. It confirmed that the migration-field omission cited in earlier rounds is materially addressed.

**Final gate result: BLOCKED.** No push is authorized.

## Pre-push review — 2026-09-03

**Status**: Follow-up required | **Reviewer**: gpt-5.4 | **Destination**: `origin/feature/005-image-ingest-storage` | **Range**: `origin/feature/005-image-ingest-storage..HEAD`

Initial independent review found stale/missing exact-range review evidence and incorrect master-divergence wording. Both are **valid and fixed**: this record defines the pending range and [plan.md](../plan.md) reports divergence without a reversed count. `git diff --check origin/feature/005-image-ingest-storage..HEAD` passed. Awaiting final alternate-model agreement.

## Final agreement for current pending range

The alternate reviewer rechecked the pending push range `origin/feature/005-image-ingest-storage..HEAD` and confirmed the previously identified missing/stale exact-range evidence and reversed divergence wording are fixed. The review record now documents the exact pending range, the plan uses stable non-directional divergence language, and the added ADR/contract/data-model/plan/tasks artifacts remain consistent with the approved review dispositions. No valid unresolved finding remains on the provided evidence.

**Final gate result: PASS**
