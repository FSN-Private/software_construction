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

## Pre-push review — 2026-09-07 (single additional round)

**Status**: blocked | **Reviewer model**: gpt-6-astra | **Base**: `3195561c0c1f4ba37bc475ff5552adc2952a3cba` | **Reviewed range**: `3195561c0c1f4ba37bc475ff5552adc2952a3cba..d88f9ab3d159ca7ccaaad23e6654bead7b1cfd3a` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

### Round 6 — independent follow-up assessment

**Prompt scope**: The alternate reviewer received the approved Feature 005 artifacts, the complete pending range and relevant implementation, tests, migrations, configuration, all prior dispositions, and repository policy. It was asked to disprove correctness and completeness. It made no file, commit, container, or remote changes.

| Finding | Severity | Affected IDs | Evidence, impact, remediation | Implementer disposition |
|---|---|---|---|---|
| Stale idempotency owners can overwrite or delete a newer reservation. | Medium | T004; FR-015; NFR-015 | `IdempotencyStore` replaces a stale owner/key reservation, while later completion and cleanup use only that owner/key. A late request A can overwrite or remove replacement request B. Add a reservation generation/token and condition completion/release on it; test A expiry, B takeover, then late A completion/failure. | **Valid.** Unresolved; review-only round, no implementation authorized. |
| Replayed idempotency responses lose their original content type. | Medium | T004; FR-015; versioned error contract | `IdempotencyMiddleware` persists/replays status and body but not content type, so a replayed problem response can lose `application/problem+json`. Persist/replay required response metadata and test duplicate error responses. | **Valid.** Unresolved; review-only round, no implementation authorized. |
| Readiness and ingest observability remain incomplete. | Medium | T009; NFR-002/007/012 | API readiness only checks a nonempty connection string while transfer handling requires bucket/KMS configuration; unavailable PostgreSQL can still appear ready. Worker signals remain unconnected and no ingest export/15-minute alert evidence exists. Check required configuration and bounded dependencies, wire signals/alerts, and test an injected outage. | **Valid.** Unresolved; review-only round, no implementation authorized. |
| Critical publication/storage boundaries lack executable behavior and fault evidence. | Medium | T004/T006/T008/T009; AC-002/007; NFR-009/015 | The focused executable does not exercise S3 storage, transfer grants, idempotency persistence, outbox, or worker; the integration ingest directory provides Compose only. Add targeted S3/PostgreSQL/worker tests for source-version, metadata binding, reuse, stale reservation, and infrastructure faults. | **Valid.** Unresolved; review-only round, no implementation authorized. |
| Required backend formatter fails. | Medium | T004/T009; NFR-009; CI compatibility | `dotnet format apps/api/Platform.Api.csproj --verify-no-changes --no-restore --verbosity quiet` exits 1 for `apps/api/Program.cs` lines 50–55, the command run by CI. Format the file and rerun the command. | **Valid.** Unresolved; review-only round, no implementation authorized. |

**Prior disposition assessment**: source-version binding, destination version evidence, publication binding checks/single-item enforcement, authoritative reuse, worker infrastructure isolation, persisted transfer authorization, Windows path validation, digest-pinned images, exact exception assertions, and fixture licensing are materially addressed in their stated scopes. The remaining idempotency and observability work is only partially remediated. Historical XMP quarantine/reconstruction work remains consistent. Later planned user-story, reconstruction, and capacity tasks were not counted as new findings.

**Validation reproduced**: the focused unit executable, `npm run test:contract`, the API correlation integration test, and worker build passed; binary fixture SHA-256 values match provenance. The formatter verification failed. `git diff --check origin/feature/005-image-ingest-storage..HEAD` also fails on the newly introduced `Program.cs` trailing whitespace plus inherited Markdown whitespace. Docker was available, but no test containers or PostgreSQL/MinIO behavior tests existed; the reviewer did not create infrastructure in this read-only round.

**No additional issue found**: the inspected owner-subject authorization, fixture integrity/provenance, dependency lock presence, API container project-copy wiring, destructive migration behavior, accidental secrets/artifacts, and remediated storage primitives had no additional defect identified.

**Final gate result: BLOCKED.** Valid unresolved findings remain. This was the user-requested maximum of one additional review round; no push is authorized.

## Current gate status — 2026-09-07

The historical PASS immediately above applies only to the earlier documented range. Round 5 independently reviewed `93ff52a..27fca41` and is **BLOCKED** with valid unresolved high- and medium-severity findings. Commits `4efe5c4` and this review record were created after that independent review, so the current pending range `93ff52a..HEAD` also requires a fresh complete review after the blocking findings are resolved. No push is authorized.

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

### Round 5 — independent follow-up assessment

**Reviewer model**: gpt-6-astra | **Review date**: 2026-09-07 | **Base**: `93ff52a` | **Reviewed range**: `93ff52a..27fca41` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

**Prompt scope**: The reviewer received the approved feature artifacts, complete range and diff, relevant source/tests/configuration/migration, all earlier rounds and dispositions, repository constraints, and latest validation. It was asked to disprove completeness and correctness, assess every earlier disposition, and report severity, evidence, affected IDs, impact, remediation, and no-issue areas. It made no file or remote changes.

**Reproduced validation**: `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj --no-restore` and `git diff --check 93ff52a..27fca41` passed. The reviewer also verified all four binary fixture SHA-256 values against provenance. The implementation agent additionally passed `dotnet build packages/storage/Platform.Storage.csproj --no-restore` and `dotnet build apps/ingest-worker/Platform.Ingest.Worker.csproj --no-restore` before commit.

| Finding | Severity | Affected IDs | Evidence, impact, remediation | Implementer disposition |
|---|---|---|---|---|
| Staging HEAD and Copy are not bound to the same immutable source version. | High | T006/T008; FR-002/003; AC-001/007; NFR-015 | `S3ObjectStore` validates the mutable staging key then copies it without `SourceVersionId` or a source ETag condition; an intervening replacement can publish different bytes at the verified key. Bind copy to the validated version, retain destination evidence, and test replacement between HEAD/copy. | **Valid.** Unresolved; no source-version binding or test exists. |
| Promotion payload is not transactionally bound to intent item/content/asset metadata. | High | T005/T006/T008; FR-002/004/009; AC-007; NFR-015 | `PublicationPromoter` uses JSON payload while `MarkPublishedAsync` activates separately stored item content and assets without checksum/session/asset comparison. Payload A, item B, and asset C can differ. Derive promotion from bound records or enforce exact bindings and test deliberate mismatches. | **Valid.** Unresolved. |
| Deduplicated authoritative content cannot complete subsequent metadata publication. | High | T006/T008; FR-007; AC-002/007; NFR-013/015 | SQL requires `integrity_state = 'pending-publication'`; after first publication it is `authoritative`, so later asset reuse fails despite verified S3 reuse. Permit verified authoritative content without state regression and test sequential/concurrent reuse. | **Valid.** Unresolved. |
| Database failures can still terminate the hosted worker. | High | T008; AC-007; NFR-002/015 | Reconcile/claim execute outside the per-intent try; `MarkFailedAsync` can also throw inside catch. A metadata outage exits `ExecuteAsync`. Protect polling, claim, and failure persistence; distinguish shutdown and retry infrastructure failures with bounded delay; inject failures. | **Valid.** Unresolved. |
| Persisted, owner-scoped transfer authorization is absent. | High | T006; FR-002/008; NFR-003 | `TransferGrant` is caller supplied, with no session lookup/issuance endpoint/owner-state-target binding; signed headers omit length. Issue from authenticated persisted session and test cross-owner, stale/cancelled, size and target abuse. | **Valid.** Unresolved. |
| Durable idempotency and lifecycle concurrency are unimplemented. | Medium | T004; FR-015; NFR-003/015 | Middleware checks only header presence and lifecycle-version constant is unused. Persist request fingerprint/result replay and version comparisons with duplicate/concurrent/stale tests. | **Valid.** Unresolved. |
| Observability/audit/health/alerts and safe failure diagnostics are incomplete. | Medium | T004/T009; FR-014/015; NFR-004/007/012 | Instruments have no emissions; audit writer, health/alert path absent; worker logs full exception. Add safe metrics/audit/health/alert verification and sensitive-sentinel log test. | **Valid.** Unresolved. |
| Integration images use mutable tags. | Medium | T002; NFR-003/014 | `tests/integration/ingest/compose.yml` uses PostgreSQL/MinIO tags. Verify provenance and pin reviewed digests. | **Valid.** Unresolved. |
| Publication/storage boundary has no behavior or fault coverage. | Medium | T006/T008/T009; AC-002/007; NFR-009/015 | Focused executable never invokes S3/outbox/worker and catches any exception; disconnected `PublicationIntent.Repair()` differs from SQL. Add exact-exception S3/PostgreSQL/worker fault tests for these findings. | **Valid.** Unresolved. |
| Completed Windows-path validation is not platform independent. | Medium | T007; FR-011; AC-005; NFR-010 | `CON.photo.jpg` bypasses `GetFileNameWithoutExtension` check and Linux host invalid-char rules omit Windows controls. Validate pre-first-dot device names and explicit Windows character policy with tests. | **Valid.** Unresolved; T007 must be reopened before closure. |

**Prior disposition assessment**: the reviewer agreed that the specification-only XMP quarantine and reconstruction-traceability repairs, earlier range wording, fixture-license correction, migration field additions, worker registration/handler/stale requeue, and unbounded-memory removal are resolved in their stated scopes. The immediate one-payload/multiple-item activation defect is resolved by `count(*) = 1`; atomic multi-item publication remains unfinished. Conditional S3 reuse is resolved at the primitive level but not end-to-end because of the duplicate-publication finding. Per-intent exceptions are partly isolated, but infrastructure failures remain unresolved. No waiver exists.

**No additional issue found**: no unrelated change, committed secret, destructive migration, remote/deployment mutation, fixture checksum mismatch, or basic owner-subject authorization defect. Locked dependency versions are present. Later user-story/reconstruction/capacity tasks remain planned and were not counted as new findings.

**Final gate result: BLOCKED.** The alternate reviewer explicitly cannot agree that no valid unresolved finding remains. No push is authorized.

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

## Current pre-push review — 2026-09-09

**Status**: pass, pending final review-record commit | **Reviewer model**: GPT-5.5 | **Destination**: `origin/master` | **Base**: `5f47b1f723d1f1116a3a0e29297ff47c7244e934` | **Final reviewed head**: `394c1a1eb88db9d9dd28c1a830f24426f0459be3` | **Final reviewed range**: `5f47b1f723d1f1116a3a0e29297ff47c7244e934..394c1a1eb88db9d9dd28c1a830f24426f0459be3` | **Pending push scope**: prospective `origin/master...HEAD`, 0 behind / 41 ahead; no open PR for this head (earlier PR #10 is merged).

### Round 1 — independent assessment

The reviewer received the complete prospective range, approved Feature 005 artifacts, governing repository policy, relevant implementation/tests/configuration/migrations, historical review dispositions, and latest validation evidence. It was asked to disprove merge safety under the skill's significant-risk threshold and made no repository, container, or remote changes.

| Finding | Classification | Severity | Affected IDs | Reviewer evidence and remediation | Implementer assessment |
|---|---|---:|---|---|---|
| Review policy contradicts the governing constitution | Blocking | Medium | Constitution V/Governance; repository push invariants | The range changes `AGENTS.md` and four project skills to waive review for ordinary pushes while the unchanged constitution mandates alternate-model review before every push and says the stricter rule governs. Restore pre-push wording or obtain a formal constitution amendment. | **Valid; T028 appended.** The constitution was not amended or explicitly approved for amendment. The smallest fix is to restore consistent pre-push semantics while retaining the execution-persistence improvements. |
| Worker packaging is not first-class | Non-blocking | Low | NFR-011 | The worker builds through project references but has no dedicated package script/container/CI artifact. Consider adding it for the first deployment. | **Non-blocking note.** External deployment is explicitly outside Feature 005 implementation authority, and Release publication of the worker executable passes. Track with release preparation rather than delay this gate. |

The reviewer found no other blocking issue in its runtime review of ingest integrity/publication, idempotency, transfer authorization, owner scoping, privacy/logging, lifecycle, exact retrieval, reconstruction, migrations, CI/test wiring, dependencies, secrets, and accessibility. It assessed the historical runtime findings as materially resolved at the reviewed head and returned **BLOCKED** solely for the policy/constitution conflict.

### Implementer supplemental integrity finding

Independent reproduction after Round 1 truncated the 29,195,592-byte licensed DNG fixture to 4,096 bytes. `ContentInspector.Inspect` returned `dng` because it checked TIFF directory structure but did not follow sub-IFDs or validate strip/tile payload offset and byte-count pairs. This is a blocking data-integrity defect under FR-007, AC-003, and NFR-015: an obviously incomplete RAW could enter active authoritative storage. T029 was appended for structural payload-range validation and regression coverage. The follow-up reviewer must assess both remediation dispositions and the complete revised range.

**Round 1 gate result: BLOCKED.** T028 and T029 require implementation, validation, and independent follow-up review. No push is authorized.

### Round 1 remediation disposition

- **Policy/constitution conflict — valid and fixed (T028).** `AGENTS.md`, `adversarial-pre-push-review`, `github-work-traceability`, `plan-driven-implementation`, and `release-operations` again require a passing alternate-model review before any push. The later execution-persistence rules remain, and no constitution amendment or history rewrite was needed.
- **Corrupt DNG acceptance — valid and fixed (T029).** `ContentInspector` now walks nested TIFF/DNG IFD pointers and validates matching strip, tile, and embedded-JPEG offset/length arrays against the complete stream. The executable corpus check rejects the 29,195,592-byte DNG truncated to 4,096 bytes while accepting the complete DNG and TIFF fixtures.
- **Worker packaging — non-blocking note retained.** A release may add a dedicated worker package/container when deployment is authorized; current implementation validation publishes the worker executable successfully.

Remediation validation passed: `dotnet format ... --verify-no-changes`; offline foundation/corpus executable; Release API and worker builds; Release worker publish; `npm test`; web typecheck/test/accessibility/build; contract and CI-contract checks; and the full `npm run test:ingest` PostgreSQL 18.1/versioned SSE-KMS MinIO suite. The first integration attempt after prior cleanup failed before feature execution because Docker was stopped; after starting Docker Desktop and healthy isolated services, the exact command passed. `git diff --check` remains required immediately before the remediation commit.

### Round 2 — independent follow-up assessment

**Reviewer model**: GPT-5.5 | **Reviewed head**: `73c17f01f3c9621f0be2457aefa4f7eefe4a8ac8` | **Reviewed range**: `5f47b1f723d1f1116a3a0e29297ff47c7244e934..73c17f01f3c9621f0be2457aefa4f7eefe4a8ac8` | **Pending push scope**: prospective `origin/master...HEAD`, 0 behind / 40 ahead.

The reviewer explicitly agreed that the Round 1 governance blocker is valid and fixed and that the supplemental DNG/TIFF truncation blocker is valid and fixed for the reproduced failure mode. It retained worker packaging as a non-blocking release note and recorded three stale generic uses of “merge” in the review skill as non-blocking wording polish.

The reviewer found one new **blocking Medium** defect affecting T012/T013/T014/T029, FR-007/008/015, AC-003, and NFR-015: PNG chunk and JPEG segment header reads passed potentially short arrays directly to `BinaryPrimitives`. A 9-byte PNG or four-byte JPEG can therefore throw `ArgumentOutOfRangeException`, bypass the worker's deterministic content-failure handler, and remain in infrastructure retry instead of quarantine. The reviewer required exact reads plus offline and PostgreSQL/MinIO quarantine regressions.

**Implementer assessment: valid; T030 appended.** The exception path is reproducible from the source and materially violates deterministic corrupt-input handling. All PNG/JPEG structural reads will use a common exact-read helper. Both sub-header truncations will be covered before and through the worker. The non-blocking wording note will be cleaned while preserving the operative pre-push gate.

**Round 2 gate result: BLOCKED.** T030 requires implementation, validation, and independent follow-up review. No push is authorized.

### Round 2 remediation disposition

- **Short PNG/JPEG parser exception — valid and fixed (T030).** PNG signature/chunk/CRC and JPEG segment-length reads now use a shared exact-read helper whose short read raises `EndOfStreamException`; the existing inspector boundary converts that to `InvalidDataException("unsupported_format")`, which the worker quarantines. Offline regressions reject a 9-byte PNG and four-byte JPEG. The PostgreSQL/MinIO fault batch proves both items reach quarantine with stable `unsupported_format` outcomes.
- **Stale merge wording — non-blocking note fixed.** The three generic threshold/loop references now say push, consistent with the skill's operative gate and the constitution.

Validation passed sequentially after one harmless shared-output file-lock collision from initially launching the test and worker build in parallel: `npm run format`; `npm test`; contract and CI-contract checks; Release API/worker builds; Release worker publish; offline foundation/corpus checks; the complete `npm run test:ingest` suite against healthy PostgreSQL 18.1 and versioned SSE-KMS MinIO; web accessibility checks included by `npm test`; and `git diff --check`.

### Round 3 — independent final implementation assessment

**Reviewer model**: GPT-5.5 | **Reviewed head**: `394c1a1eb88db9d9dd28c1a830f24426f0459be3` | **Reviewed range**: `5f47b1f723d1f1116a3a0e29297ff47c7244e934..394c1a1eb88db9d9dd28c1a830f24426f0459be3` | **Pending push scope**: prospective `origin/master...HEAD`, verified 0 behind / 41 ahead.

The reviewer inspected the complete revised range and explicitly agreed:

- T028 materially resolves the Round 1 governance contradiction; the constitution, `AGENTS.md`, and affected project skills consistently require the alternate-model gate before every push.
- T029 materially resolves the reproduced TIFF/DNG truncation defect by validating nested IFD and image-payload ranges while retaining acceptance of the complete corpus.
- T030 materially resolves the short PNG/JPEG exception path: exact reads become `InvalidDataException("unsupported_format")`, and offline plus PostgreSQL/MinIO coverage proves deterministic quarantine.
- Historical storage-version binding, immutable reuse, publication/retry, transfer authorization, idempotency replay, readiness/observability, path validation, dependency pinning, and integration-coverage findings remain materially resolved.
- Worker packaging remains a non-blocking Low release-preparation note. No other blocking issue was found across governance/constitution compliance, traceability, corrupt-input handling, storage integrity, atomicity/retry, authorization/privacy, lifecycle concurrency, retrieval, reconstruction, migrations/rollback, CI/supply chain, secrets/artifacts, accessibility, capacity, or runbooks.

The reviewer ran read-only range/status/diff/source checks including `git diff --check` and made no repository, container, or remote changes.

## Final agreement for the current pending push

The independent GPT-5.5 reviewer explicitly states that no valid unresolved blocking finding remains in `5f47b1f723d1f1116a3a0e29297ff47c7244e934..394c1a1eb88db9d9dd28c1a830f24426f0459be3` and returns **PASS**. The worker packaging note does not delay the gate.

**Final gate result: PASS.** No push or merge is authorized by this review.

## Release follow-up pre-push review — 2026-09-10

**Status**: blocked after Round 2; T038/T039 remediation awaiting final follow-up review

**Reviewer model**: gpt-5.6-sol

**Destination**: `origin/master`

**Base**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1`

**Reviewed range**: `origin/master..f53b2dc` (`a189b1f`, `ba7f2ee`, `f53b2dc`)

**Pending push scope**: the three local Feature 005 release-preparation commits; no deployment or other remote mutation

### Round 1

**Prompt scope**: The independent reviewer received repository policy, the approved Feature 005 artifacts, release plan, existing review history, complete committed range, worker/container/migration implementation, release/deploy/rollback workflows, evidence and smoke scripts, tests, and exact local validation results. It was asked to disprove push safety across requirements, data integrity, migration, security, supply chain, failure handling, compatibility, tests, and documentation. It made no file, commit, container, or remote changes.

| Finding | Classification / severity | Affected IDs | Evidence and impact | Implementer disposition |
|---|---|---|---|---|
| Production promotion and rollback handle only the API despite the new worker artifact. | Blocking / High | T032–T035; NFR-002/011/015 | `release.yml` published only `api.tar`; `rollback.yml` restored only one task definition; promotion evidence validated only the API digest. A permitted tag could leave imports unprocessed or the API/worker pair inconsistent. | **Valid and fixed in remediation.** T036 adds the shared migration/two-service/smoke deployment path to production, binds promotion evidence to both services, and makes rollback restore the exact retained API/worker pair. |
| Non-production failure paths can leave a partial API/worker rollout. | Blocking / High | T033/T034; NFR-002/011/012/015 | Rollback began in a later workflow step; ambiguous CLI failure or the 20-minute job timeout could bypass it after one service changed. | **Valid and fixed in remediation.** T037 centralizes both environments on one fail-fast script, installs `ERR`/`TERM` paired rollback before the first service mutation, caps migration/service waits at 10 minutes and smoke at 2 minutes, and reserves a 45-minute job window for rollback. |
| Exact-range review record was absent. | Blocking / Medium | Pre-push policy | The prior PASS covered the earlier feature range, not `0749a52..f53b2dc`. | **Valid and fixed by this preserved round.** The complete revised range and this record require independent follow-up before PASS. |
| Smoke did not prove deleted access denial, restored bytes, or manifest membership. | Non-blocking note | T034; AC-001/006/008 | The first script called the lifecycle endpoints without checking those outcomes. | **Non-blocking note fixed voluntarily.** The smoke now requires 404 after deletion, rehashes restored bytes, verifies manifest membership, and avoids the first-page asset-list assumption. |

**No blocking issue found**: the reviewer found the exact initial commit scope clean; the Windows lock target stable; worker base/action pins and non-root runtime sound; clean-database migration transaction, advisory lock, and checksum behavior sound; workflow source binding and token permissions sound; legacy evidence verification compatible; and smoke output private. Docker/PostgreSQL runtime proof remains assigned to required remote CI and did not itself block the proposed push.

**Round 1 gate result: BLOCKED.** The revised exact range, validation, dispositions, and pending record require independent follow-up.

### Round 2 — independent follow-up assessment

**Reviewer model**: gpt-5.6-sol | **Reviewed base**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1` | **Reviewed head**: `6eeb1e115fa91b97fa83a13c60872a4ac790bf5f` | **Reviewed range**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1..6eeb1e115fa91b97fa83a13c60872a4ac790bf5f` (`a189b1f`, `ba7f2ee`, `f53b2dc`, `6eeb1e1`)

The reviewer accepted the Round 1 remediation for paired production promotion/rollback, rollback activation before the first service update, and the stronger lifecycle/manifest smoke. It found no blocking issue in Windows lock stability, worker provenance and non-root packaging, transactional/checksum-pinned migration behavior, workflow identity and permissions, evidence compatibility, secret handling, or the unavailable local Docker/PostgreSQL checks assigned to remote CI.

| Finding | Classification / severity | Affected IDs | Evidence and impact | Implementer disposition |
|---|---|---|---|---|
| Deployment evidence was not bound to the task definitions actually running after ECS stability. | Blocking / High | T036/T037; NFR-002/011/012/015 | ECS circuit-breaker rollback could stabilize both services on a prior healthy release, pass smoke there, and produce evidence claiming the candidate pair ran. | **Valid; fixed by T038.** Both services are re-read after the stability waiter and must exactly match the candidate API/worker task-definition ARNs before health, smoke, or evidence completion. A mocked prior-task response fails closed. |
| Standalone rollback had the same false-success path. | Blocking / High | T036; NFR-002/011/012/015 | A failed target rollback could revert to the current release, pass health there, and be recorded as successful. | **Valid; fixed by T038.** The standalone path now uses a dedicated script and verifies both requested task-definition ARNs after stability before smoke or evidence generation. |
| The workflow timeout did not guarantee enough time for recovery. | Blocking / Medium | T037; NFR-002/012/015 | Pre-mutation image and AWS operations could consume the job window; a later runner timeout could terminate the paired restore waiter. | **Valid; fixed by T039.** Deployment is capped at 35 minutes inside a 60-minute job; standalone rollback is capped at 20 minutes inside a 40-minute job. `TERM` reaches the active script trap, leaving at least 20 raw runner minutes beyond each inner deadline. Contract checks enforce the budgets. |
| The review record described only the prior three-commit range. | Blocking / Medium | Pre-push policy | Round 1 ended at `f53b2dc`, while the revised range contained `6eeb1e1`. | **Valid; fixed by this preserved round.** Round 1 remains intact and this section records the exact four-commit Round 2 range and all dispositions. |

**Round 2 gate result: BLOCKED.** T038/T039 remediation and this pending record require validation and independent review of the complete revised range before PASS.

### Round 3 — pending final follow-up

**Proposed reviewer model**: gpt-5.6-sol | **Base**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1` | **Head**: `e03c3c4eafe6c35d2e14de4cc442eadb48a84d34` | **Complete revised range**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1..e03c3c4eafe6c35d2e14de4cc442eadb48a84d34` | **Pending push scope**: six local commits, 0 behind / 6 ahead of `origin/master`; no push, deployment, migration, or other remote mutation

The final reviewer is asked to inspect this pending record and the complete revised range, explicitly reassess every Round 1 and Round 2 blocking disposition, and identify any new significant blocking issue. In particular, it must verify that T038 detects ECS circuit-breaker reversion in deployment and standalone rollback before smoke/evidence success, and that T039's inner deadlines activate recovery with adequate job time remaining.

Latest remediation validation passed: `npm run test:release-evidence`, `npm run test:reliability`, `npm run test:package-definitions`, `npm run test:ingest-smoke-script`, Bash syntax checks for all three release scripts, YAML parsing for all four release workflows, and `git diff --check`. The release-preparation API/worker builds, worker publish, backend suite, Windows packaging/lock stability, and security validation remain passing from the preceding commits; T038/T039 do not change application or package source. Docker/PostgreSQL runtime validation remains assigned to remote CI because those local services are unavailable.

**Round 3 gate result: pending independent agreement.**

### Round 3 — independent final assessment

**Reviewer model**: gpt-5.6-sol | **Review date**: 2026-09-10 | **Destination**: `origin/master` | **Base**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1` | **Reviewed head**: `e03c3c4eafe6c35d2e14de4cc442eadb48a84d34` | **Complete reviewed range**: `0749a52c575d7571473f159f089c0ce1bdcbb6b1..e03c3c4eafe6c35d2e14de4cc442eadb48a84d34` | **Pending push scope**: six local commits, 0 behind / 6 ahead of `origin/master`

The independent reviewer inspected the complete revised range and the pending Round 3 review record without changing files, commits, containers, or remotes.

The reviewer explicitly accepts every prior disposition:

- The Round 1 API-only production finding is fixed. Non-production and production require worker evidence and use the same migration, paired API/worker deployment, health, smoke, and rollback path.
- The Round 1 partial-rollout finding is fixed. Paired rollback is active before the first service mutation, ambiguous update failures fail closed, and T039 enforces deployment and standalone-rollback deadlines inside larger job windows with at least 20 minutes reserved for recovery. Signal handling converts termination to failure and invokes the active recovery trap.
- The Round 1 exact-range record finding is fixed. All rounds are preserved and this final assessment covers the exact six-commit range through `e03c3c4`.
- The non-blocking smoke note is fixed. The smoke proves deletion denies download, restored bytes retain their checksum, and the reconstruction manifest contains the smoke asset without relying on a paginated asset listing.
- Both Round 2 false-success findings are fixed. After ECS reports stability, deployment requires the API and worker services to name the exact candidate task-definition ARNs before health, smoke, or evidence completion. Standalone rollback similarly requires both exact requested task definitions before it can report success. The mocked circuit-breaker regression proves a prior-task response fails and activates the parent rollback trap.
- The Round 2 recovery-reserve finding is fixed. Deployment is capped at 2,100 seconds inside a 60-minute job; standalone rollback is capped at 1,200 seconds inside a 40-minute job. Contract checks enforce both budgets.

No valid unresolved blocking finding was identified. No evidence path can report successful deployment or rollback after the inspected ECS task-definition mismatch, health failure, smoke failure, or inner deadline failure. No blocking issue was found in Windows lock stability, worker container provenance and non-root execution, transactional/checksum-pinned migration behavior, workflow syntax/source binding/permissions, paired artifact and promotion evidence, secret handling, strengthened smoke behavior, documentation, or test wiring. Docker/PostgreSQL runtime execution remains a required remote CI check and does not itself block this proposed push.

The reviewer explicitly agrees that no valid unresolved blocking finding remains in `0749a52c575d7571473f159f089c0ce1bdcbb6b1..e03c3c4eafe6c35d2e14de4cc442eadb48a84d34`.

**Final gate result: PASS.** This review authorizes no push, merge, deployment, migration, or other remote mutation.

## Release evidence plan-compatibility review — 2026-09-10

**Status**: blocked after Round 1; T040 remediation awaiting final follow-up review

**Reviewer model**: gpt-5.6-sol | **Destination**: `origin/master` | **Base**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64` | **Initial reviewed head**: `c8116c9807f0bbe30c5231c7b91df003acef3400` | **Initial range**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64..c8116c9807f0bbe30c5231c7b91df003acef3400`

### Round 1 — independent assessment

The reviewer received failed GitHub Actions run `34477947247`, the exact one-commit fix, relevant Feature 004/005 requirements and tasks, workflow/scripts/tests/documentation, the private-plan constraint, and local validation. It made no repository or remote changes.

| Finding | Classification / severity | Affected IDs | Evidence and impact | Implementer disposition |
|---|---|---|---|---|
| The local provenance statement claimed GitHub's published workflow build type without implementing that schema, and repository identity was only self-consistent. | Blocking / Medium | Feature 004 FR-005, AC-003/008, NFR-016, T017; Feature 005 T040, NFR-011/014/016 | The generator used `https://actions.github.io/buildtypes/workflow/v1` with project-specific external parameters; the verifier did not require the build type or receive a trusted expected repository. A recomputed manifest could substitute a consistent repository identity and still pass local verification. | **Valid and fixed in `5d05173`.** The statement now uses the documented project-owned build type in `docs/contracts/release-provenance-v1.md`. Current-evidence verification requires `GITHUB_REPOSITORY`, checks the exact build-type URI, and compares external parameters, dependency URI, builder ID, and invocation ID against that trusted value. Recomputed-checksum tests reject build-type and consistent repository substitution. |

The reviewer found no blocking issue in removal of hosted attestation and write/OIDC permissions, five-artifact/worker enforcement, exact source/release-run checks, artifact-subject matching, read-only evidence permissions, genuine four-artifact rollback compatibility, or the requirement for a fresh protected-default remote run. It recorded the weaker assurance of an unsigned local statement as non-blocking under the explicit plan constraint because documentation does not claim GitHub/Sigstore certification.

**Round 1 gate result: BLOCKED.** The valid build-type and trusted-repository finding required remediation and follow-up.

### Round 2 — pending final follow-up

**Proposed reviewer model**: gpt-5.6-sol | **Base**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64` | **Head**: `5d051739ba6d42dc2516ed6ec591b39176b81870` | **Complete revised range**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64..5d051739ba6d42dc2516ed6ec591b39176b81870` | **Pending push scope**: two local commits, 0 behind / 2 ahead of `origin/master`

Latest validation passed: `npm run test:release-evidence`, `npm run test:reliability`, `npm run test:ci-contract`, `npm run test:package-definitions`, workflow YAML parsing, `npm run test:backend`, `npm run validate:security`, and `git diff --check`. The initial sandboxed backend attempt failed only because NuGet advisory lookup had no network; the normal-network retry passed.

**Round 2 gate result: pending independent agreement.**

### Round 2 — independent final assessment

**Reviewer model**: gpt-5.6-sol | **Review date**: 2026-09-10 | **Destination**: `origin/master` | **Base**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64` | **Reviewed head**: `5d051739ba6d42dc2516ed6ec591b39176b81870` | **Complete reviewed range**: `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64..5d051739ba6d42dc2516ed6ec591b39176b81870` | **Pending push scope**: two local commits, 0 behind / 2 ahead of `origin/master`

The independent reviewer inspected the complete revised range and the pending Round 2 record without changing files, commits, containers, or remotes.

The reviewer accepts the Round 1 blocking disposition as valid and fixed. The generated statement now identifies the documented project-owned build type at `docs/contracts/release-provenance-v1.md` rather than claiming GitHub's incompatible workflow build type. Current-evidence verification requires the repository supplied by trusted GitHub workflow context and checks the exact build-type URI, external repository, dependency URI, builder identity, invocation identity, source revision, triggering CI run, release run when supplied, validation statement, and every artifact subject. Non-production and production pass the trusted repository and selected release run; rollback passes the trusted repository. Recomputed-checksum regressions reject both build-type substitution and a consistent repository/builder/dependency/invocation substitution.

No valid unresolved blocking finding was identified. No blocking issue was found in removal of the unavailable hosted-attestation action and its write/OIDC permissions, five-artifact worker enforcement, genuine four-artifact legacy verification compatibility, workflow source/run binding, read-only evidence permissions, documentation claims, or test wiring. The locally generated statement remains weaker than a GitHub-hosted signed Sigstore attestation, but this remains non-blocking under the explicit private-plan constraint and is accurately documented without a certification claim. A fresh protected-default workflow run remains the required remote confirmation and does not block this proposed push.

The reviewer explicitly agrees that no valid unresolved blocking finding remains in `6b4d4c3a4a4493efa4a6e618cb1e2077d4298e64..5d051739ba6d42dc2516ed6ec591b39176b81870`.

**Final gate result: PASS.** This review authorizes no push, merge, deployment, migration, or other remote mutation.
## T035 non-production runtime review — 2026-09-13

**Status**: blocked after Round 1; T041–T044 remediation awaiting independent follow-up

**Reviewer model**: gpt-5.6-sol | **Destination**: `origin/feature/005-image-ingest-storage-release` | **Base**: `99ceba30f5550117634b2df0b7b9327d4044c0b7` | **Initial reviewed head**: `54c0d21f091509d3c9673872e981598edaa1fb9b` | **Initial range**: `99ceba30f5550117634b2df0b7b9327d4044c0b7..54c0d21f091509d3c9673872e981598edaa1fb9b`

The independent reviewer inspected the complete pending runtime/bootstrap range, Feature 004/005 release requirements, infrastructure, deployment scripts, workflow contracts, tests, and documentation without changing files, commits, containers, or remotes.

| Finding | Classification / severity | Affected IDs | Evidence and impact | Implementer disposition |
|---|---|---|---|---|
| The CloudWatch Logs KMS encryption-context condition synthesized `log-group/*`, which cannot match CloudWatch Logs ARNs. | Blocking / High | T035/T041; NFR-007/014 | The telemetry log group could fail to use its KMS key at deployment or runtime. | **Valid and fixed by T041.** CDK now uses `ArnFormat.COLON_RESOURCE_NAME`; the synthesized-template contract requires `:log-group:*` and rejects the slash form. |
| API and worker received `s3:DeleteObject*` across the complete ingest bucket, including authoritative content versions. | Blocking / High | T035/T042; FR-005/009; NFR-008/014 | A runtime defect or compromise could remove retained canonical `sha256/*` versions. | **Valid and fixed by T042.** Runtime roles may delete staging/quarantine versions only and explicitly deny `DeleteObject`/`DeleteObjectVersion` for `sha256/*`; synthesis checks both roles. |
| API and worker received the RDS `platform_admin` master credentials. | Blocking / High | T035/T043; NFR-002/008/014 | Either runtime process could modify schema or bypass database-level least privilege. | **Valid and fixed by T043.** A distinct migration task alone receives administrator credentials and transactionally provisions/rotates a non-administrative runtime principal; API and worker receive only the runtime secret. Integration coverage verifies migrated-table access and absence of administrative role flags. |
| A migration waiter timeout or signal could leave the remote ECS task running, and the deployment role lacked `ecs:StopTask`. | Blocking / Medium | T035/T044; NFR-002/012/015 | A migration could continue after the workflow reported failure, racing a retry or later release. | **Valid and fixed by T044.** The dedicated helper traps errors/signals immediately after `RunTask`, stops and awaits the active task, and a mocked waiter timeout proves the cleanup path. The deployment role receives cluster-conditioned `ecs:StopTask`. |

The reviewer found no blocker in the release-evidence/source binding, configuration preflight, immutable ECR retry, exact service task checks, zero-count rollback, Cloud Map/VPC-link isolation, Cognito handling, S3 versioning/KMS, or external bootstrap documentation. It retained the documented $20 budget as a non-blocking alert threshold rather than a hard spending cap.

**Round 1 gate result: BLOCKED.** The complete remediated range and this record require independent follow-up before push.

### Round 2 — independent follow-up assessment

**Reviewer model**: gpt-5.6-sol | **Reviewed base**: `99ceba30f5550117634b2df0b7b9327d4044c0b7` | **Reviewed head**: `f1849d5` | **Complete reviewed range**: `99ceba30f5550117634b2df0b7b9327d4044c0b7..f1849d5`

The reviewer accepted all four Round 1 remediations: the synthesized CloudWatch Logs KMS condition uses `:log-group:*`; S3 deletion is limited to staging/quarantine with authoritative denies; only the dedicated migration task receives administrator credentials while runtime roles are non-administrative; and migration timeout/signal handling stops and awaits the ECS task with scoped IAM and executable timeout coverage.

| Finding | Classification / severity | Affected IDs | Evidence and impact | Implementer disposition |
|---|---|---|---|---|
| API and worker still shared `s3:PutObject` over the entire ingest bucket. | Blocking / High | T035/T045; FR-003; NFR-003/015 | The public API only requires staging writes but could create an arbitrary current version beneath canonical `sha256/*`, polluting the authoritative namespace and causing integrity or availability failures. | **Valid and fixed by T045.** API writes and multipart actions are limited to `staging/*`; worker writes are limited to `sha256/*` and `quarantine/*`. Exact synthesized assertions reject broad and crossed grants. |

The reviewer recorded runtime `DELETE` access to the migration ledger as a non-blocking excess privilege. T045 voluntarily removes all runtime privileges from `platform_schema_migrations`, with integration coverage for denied ledger writes and denied schema DDL. No other blocker was found in migration provisioning/cleanup, IAM pass-role/task-family scopes, workflow preflight, ECR immutability, zero-count rollback, exact task checks, Cloud Map routing, release evidence, secrets, persistence retention, cost-gate documentation, or tests.

**Round 2 gate result: BLOCKED.** T045 and this complete revised range require another independent follow-up before push.

### Round 3 — independent final follow-up

**Reviewer model**: gpt-5.6-sol | **Review date**: 2026-09-13 | **Destination**: `origin/feature/005-image-ingest-storage-release` | **Base**: `99ceba30f5550117634b2df0b7b9327d4044c0b7` | **Reviewed head**: `ddebb6413445ba0e7b7d48c91381bfc388a61d3b` | **Complete reviewed range**: `99ceba30f5550117634b2df0b7b9327d4044c0b7..ddebb6413445ba0e7b7d48c91381bfc388a61d3b`

The independent reviewer inspected the complete revised range, the preserved Round 1 and Round 2 records, Feature 004/005 requirements and release documentation, current infrastructure/application/deployment source, the exact synthesized CloudFormation task/IAM/KMS resources, and updated tests without changing files, commits, infrastructure, or remotes. The working tree is clean and the branch is 15 commits ahead of the stated destination.

The reviewer explicitly accepts every prior blocking disposition:

- **T041 / CloudWatch Logs KMS** remains fixed. CDK uses `ArnFormat.COLON_RESOURCE_NAME`; the exact key policy emits the account/region-scoped `:log-group:*` encryption-context ARN and rejects the slash form.
- **T042 / authoritative deletion** remains fixed. API and worker both have explicit identity-policy denies for `DeleteObject` and `DeleteObjectVersion` on `sha256/*`; only the worker has deletion allows, limited to `staging/*` and `quarantine/*`.
- **T043 / database principal separation** remains fixed. API and worker execution roles can read only the generated runtime secret. The separate migration execution role alone can read both runtime and RDS administrator secrets; its transaction provisions a login without superuser, role-creation, database-creation, or schema-creation authority and grants required application-table DML/sequence access.
- **T044 / migration cleanup** remains fixed. The helper installs its error/interrupt/termination cleanup trap immediately after capturing the task ARN, stops and awaits an active remote task, and preserves failure. The deployment role has cluster-conditioned `ecs:StopTask`, and the executable timeout contract proves cleanup invocation.
- **T045 / write namespace isolation** fixes the Round 2 blocker. Exact synthesized IAM shows the API has `PutObject`, multipart-abort, and multipart-list only for `staging/*`; it has no bucket-wide, `sha256/*`, or `quarantine/*` write. Worker `PutObject` is limited to `sha256/*` and `quarantine/*`; its staging permission contains only multipart cleanup actions. Required staging/authoritative reads and authoritative deletion denies remain intact. The synthesized assertions bind these statements to the API and worker task-role policies and reject broad or crossed API writes.

The voluntary migration-ledger hardening is also sound. Provisioning revokes every runtime privilege on `platform_schema_migrations` after granting application-table privileges, while default privileges do not retroactively restore access. PostgreSQL integration coverage verifies application-table DML, denied migration-ledger mutation, denied schema DDL, and non-administrative role flags.

No new significant blocking issue was found in deployability, IAM and secret boundaries, S3/KMS persistence, database migration/provisioning, migration timeout recovery, immutable ECR publication, paired service rollback, exact post-stability task checks, workflow configuration contracts, release-evidence binding, Cloud Map/VPC-link routing, cost-gate documentation, or the updated tests.

The reviewer explicitly agrees that no valid unresolved blocking finding remains in `99ceba30f5550117634b2df0b7b9327d4044c0b7..ddebb6413445ba0e7b7d48c91381bfc388a61d3b`.

**Final gate result: PASS.** This review authorizes no merge, deployment, migration, or other remote mutation; push authority remains governed by the user's separate authorization.

## T046 MinIO registry CI remediation review — 2026-09-13

**Reviewer model**: gpt-5.6-sol | **Destination**: `origin/feature/005-image-ingest-storage-release` | **Base**: `d2309de01b0a7d62a0b34ed10cce1d0677985bff` | **Reviewed head**: `1b88802760acc2d269b647b340d5690a49809cc5` | **Reviewed range**: `d2309de01b0a7d62a0b34ed10cce1d0677985bff..1b88802760acc2d269b647b340d5690a49809cc5`

The independent reviewer inspected the exact four-file commit, current clean status, integration Compose contract and backend workflow wiring, dependency inventory, T046 traceability, validation record, and the reported registry/platform/runtime evidence without changing files, containers, commits, or remotes.

The registry substitution is materially sound. `tests/integration/ingest/compose.yml` changes only the MinIO registry host from Docker Hub to the upstream MinIO Quay repository. It retains release `RELEASE.2025-09-07T16-13-09Z` and exact OCI index digest `sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e`; the digest, rather than the readable tag, controls the pulled bytes. The supplied verification confirms the Quay index digest and platform manifests, successful public Compose pull, healthy service startup, and the complete PostgreSQL/MinIO ingest suite. No mutable image reference, credential dependency, unsupported Compose syntax, platform loss, application behavior change, or broadened CI permission is introduced.

Documentation and traceability are accurate: the dependency inventory identifies the named registry and immutable digest, T046 records the Docker Hub denial and Quay remediation, and validation distinguishes registry availability from feature behavior while recording the successful full integration path. Existing Compose and CI-contract validation continues to enforce digest pinning and backend integration execution.

No significant blocking issue was found in supply-chain provenance, immutable identity, registry compatibility, workflow behavior, test coverage, documentation, or scope. The working tree is clean and exactly one commit is pending to the stated destination.

The reviewer explicitly agrees that no valid unresolved blocking finding remains in `d2309de01b0a7d62a0b34ed10cce1d0677985bff..1b88802760acc2d269b647b340d5690a49809cc5`.

**Final gate result: PASS.** This review authorizes no merge, deployment, migration, or other remote mutation; the separately recorded user authorization governs the push.
