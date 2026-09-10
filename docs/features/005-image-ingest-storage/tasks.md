# Tasks: Image Ingest and Durable Storage

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), and [contracts/](contracts/)
**Status**: Repository release follow-up complete — live non-production execution remains gated on AWS/GitHub environment bootstrap.

## Phase 1: Setup and shared foundations

- [x] T001 Create ingest API, worker, storage, corpus, contract, and integration-test layout in `apps/api/`, `apps/ingest-worker/`, `packages/storage/`, and `tests/` (FR-001, NFR-008/009).
- [x] T002 [P] Add locked, reviewed S3, PostgreSQL, multipart-transfer, XML, and format-detection dependencies to repository manifests and `docs/dependency-inventory.md` (NFR-003/006/014). — .NET provides SHA-256 and XML; AWS SDK provides multipart transfer; API/worker use locked AWS/Npgsql packages; PostgreSQL and MinIO integration images are reviewed digest pins.
- [x] T003 [P] Build legally usable RAW/JPEG/PNG/TIFF/XMP and corruption/ambiguity corpus fixtures in `tests/fixtures/ingest/` (NFR-006/014/016).
- [x] T004 Implement shared owner authorization, operation ID, idempotency, versioned errors, audit, and redaction adapters in `packages/contracts/` and `apps/api/` (FR-014/015, NFR-003/004). — Durable fenced reservations release retryable failures; owner/version enforcement and payload-free PostgreSQL audit events are covered by HTTP/integration checks.

## Phase 2: Foundational data and publication controls

- [x] T005 Create PostgreSQL migrations for batches, upload sessions, content objects, assets, edit identities, paths, holds, publication intents, and reconstruction manifests in `apps/api/Database/Migrations/` (FR-004/016, NFR-008/011/016). — Includes session/content/asset publication binding and durable idempotency records; backward-compatible migration rehearsal remains T024.
- [x] T006 [P] Implement immutable SHA-256/length object addressing, private staging/authoritative/quarantine locations, SSE-KMS, and scoped transfer authorization in `packages/storage/` (FR-002/003/008, NFR-003/013/015). — Whole-stream SHA-256, SSE-KMS, versioning, conditional reuse, 15-minute persisted grants, and expiry cleanup pass PostgreSQL/MinIO fault tests.
- [x] T007 [P] Implement normalized display/collision paths and Windows-invalid/case-collision validation in `packages/storage/Paths/` (FR-004/011, AC-005, NFR-010). — Explicit Windows control-character and device-name-before-extension cases are validated on every host.
- [x] T008 Implement publication-intent outbox, atomic metadata transition, and idempotent orphan reconciliation in `apps/ingest-worker/` (FR-007/009, AC-002/007, NFR-002/015). — Durable batch outbox, per-item savepoints, atomic rollback, dependency retry, and immutable orphan reuse pass injected-fault tests.
- [x] T009 Implement metrics, redacted audit events, alerts, and health checks for ingest, quarantine, repair, storage failure, and integrity drift in `apps/ingest-worker/Observability/` (FR-014/015, NFR-004/007/012). — Payload-free metrics/audits, PostgreSQL/S3 readiness, 15-minute storage alarm, immediate drift alarm, dashboard filters, and outage checks pass.

**Checkpoint**: verified content can be safely published or repaired before user-story work begins.

## Phase 3: User Story 1 — Import a verified photographic batch (P1)

**Independent test**: import the representative mixed batch; every item is deterministic, supported objects are checksummed/active, and unsafe inputs are private quarantine.

- [x] T010 [P] [US1] Implement import-batch and resumable upload-session endpoints from `contracts/ingest-storage-v1.md` in `apps/api/Features/Ingest/` (FR-001/009/015, AC-002).
- [x] T011 [P] [US1] Implement streaming size/SHA-256 verification, content reuse, and immutable source/XMP persistence in `apps/ingest-worker/Verification/` (FR-002/003/005, AC-001/002, NFR-015).
- [x] T012 [P] [US1] Implement corpus-backed format detection, XMP well-formedness, darktable base/numbered naming, and conservative association in `apps/ingest-worker/Verification/` (FR-005/006/007, AC-003/004/009, NFR-006/008). — Standard-library bounded XML and structural image parsers require no new dependency.
- [x] T013 [US1] Implement quarantine, item state/progress, safe corrective errors, atomic-batch preparation, and durable outcomes in `apps/api/Features/Ingest/` (FR-007/008/009/015, AC-003/007, NFR-001/005).
- [x] T014 [US1] Add resumable/retry/corruption/duplicate/atomic-publication/association contract and fault tests in `tests/integration/ingest/` and the executable .NET harness (FR-001–009, AC-001–004/007/009, NFR-001/009/015). — Real PostgreSQL/MinIO and HTTP checks run through `npm run test:ingest`.

## Phase 4: User Story 2 — Retrieve and organize stored assets (P2)

**Independent test**: download exact bytes, move/case-rename 10,000 assets, and associate numbered sidecars without changing identity.

- [x] T015 [P] [US2] Implement authorized exact-byte original/XMP retrieval with checksum/provenance response in `apps/api/Features/Lifecycle/` and `packages/storage/AssetService.cs` (FR-010, AC-001, NFR-003/004). — Every response rehashes version-pinned bytes and returns digest/handoff provenance without storage keys.
- [x] T016 [P] [US2] Implement move/rename with optimistic lifecycle version, path history, collision checks, and audit in `apps/api/Features/Lifecycle/` and `packages/storage/AssetService.cs` (FR-004/011/014, AC-005, NFR-010/015).
- [x] T017 [US2] Add retrieval, duplicate-sidecar, path history, Windows-name, and 10,000-asset collision/load checks in the executable .NET integration/capacity harness (FR-006/010/011, AC-004/005, NFR-009/016). — Real PostgreSQL/MinIO validation covers 100k assets, 1m edit records, and 10k moves.

## Phase 5: User Story 3 — Delete and restore safely (P3)

**Independent test**: soft-delete, verify active exclusion and deadline, restore at day 90, and prove a hold/destination conflict protects bytes and identity.

- [x] T018 [P] [US3] Implement lifecycle state transitions, 90-day deadline, active-list exclusion, and hold/reference evaluation in `apps/api/Features/Lifecycle/` (FR-012/013/015, AC-006, NFR-014/015).
- [x] T019 [US3] Implement restore with identity/association/path-history preservation and explicit alternate-path resolution in `apps/api/Features/Lifecycle/` (FR-011/012/014, AC-006, NFR-005/010).
- [x] T020 [US3] Add day-90, protected-reference, concurrent-operation, and audit/redaction checks in the executable .NET integration harness plus accessible owner controls in `apps/web/` (FR-012–015, AC-006, NFR-004/005/009/015). — Tests cover the inclusive stored deadline, expiry, stale versions, holds, shared content, destination conflicts, and edit gating.

## Phase 6: Recovery and operational completion

- [x] T021 Implement versioned canonical reconstruction-manifest export and verifier in `packages/storage/Reconstruction/` (FR-016, AC-008/010, NFR-008/009).
- [x] T022 Add sampled missing/altered/extra/mis-associated/path/hold/audit/handoff manifest fault checks in the executable .NET reconstruction harness (FR-016, AC-008/010, NFR-009/015).
- [x] T023 Publish ingest, quarantine, orphan-repair, checksum-drift, quota, outage, and manifest-verification runbooks in `docs/runbooks/` (NFR-007/012).
- [x] T024 Rehearse backward-compatible schema/storage rollout and repairable rollback in `docs/features/005-image-ingest-storage/validation.md` (NFR-002/011/015). — Transaction rollback and additive reapply preserve old-record identity/readability; no external migration or deployment performed.
- [x] T025 Measure batch, storage inventory/cost, and 100k/1m capacity targets in the executable .NET performance harness and record evidence in `docs/features/005-image-ingest-storage/validation.md` (NFR-001/013/016). — 1k progress 170.7 ms, verification p95 34.43 s, 100k assets/1m edits, 10k moves, and deduplicated inventory passed locally.
- [x] T026 Obtain owner approval for the feature specification and proposed ADR, then update `spec.md` and `docs/architecture/decisions/0005-immutable-content-and-repairable-publication.md`.

## Dependencies and traceability

T001–T009 are required before stories. Deliver US1 (T010–T014), then US2 (T015–T017), then US3 (T018–T020); recovery closure follows. FR-001–009: T004–T014; FR-010–011: T007/T015–T017; FR-012: T018–T020; FR-013: T018/T020; FR-014–015: T004/T009/T018–T020; FR-016: T005/T021–T022. AC-001–004: T010–T014; AC-005: T007/T016–T017; AC-006: T018–T020; AC-007: T008/T013–T014; AC-008: T021–T022; AC-009: T012–T014; AC-010: T021–T022. NFR-001–010: T002–T025; NFR-011: T005/T024; NFR-012: T009/T023; NFR-013: T006/T025; NFR-014: T002/T003/T018/T023; NFR-015: T006/T008/T014/T020/T022/T024; NFR-016: T003/T005/T017/T025. Milestone commits: integrity foundation; import MVP; retrieval/path; lifecycle; reconstruction readiness.

## Phase 7: Convergence

- [x] T027 Measure and record an executable association/path correction workflow that completes within two minutes per SC-004 (partial). — PostgreSQL/MinIO exercises completed association correction in 133.1 ms and path-collision correction in 21.2 ms.

## Phase 8: Adversarial review remediation

- [x] T028 Align repository review policy with Constitution Principle V's mandatory pre-push gate across `AGENTS.md` and affected project skills (contradicts). — All affected instructions again require a passing independent review before any push while retaining persistent remediation behavior.
- [x] T029 Reject TIFF/DNG files whose referenced image payload ranges are absent or truncated, with executable regression coverage per FR-007, AC-003, and NFR-015 (partial). — Nested IFD and strip/tile/JPEG range checks reject the 29 MB DNG fixture truncated to 4 KB in offline and PostgreSQL/MinIO suites.

## Phase 9: Adversarial review follow-up

- [x] T030 Convert every PNG/JPEG structural read to an exact read and prove sub-header truncations receive deterministic quarantine outcomes per FR-007/008/015, AC-003, and NFR-015 (partial). — Offline and PostgreSQL/MinIO worker checks quarantine a 9-byte PNG and four-byte JPEG as `unsupported_format`.

## Phase 10: Non-production release readiness

- [x] T031 Restore locked `win-x64` packaging for the referenced storage project and reproduce the Windows release ZIP per NFR-011/014 (partial). — The storage lock now contains the required runtime target; no package identity, version, or source changed.
- [x] T032 Package the ingest worker as a locked, non-root immutable container and include it in checksummed release evidence per NFR-011/014/016 (missing). — Worker publish and artifact contracts pass locally; the Docker daemon is unavailable, so the exact container build remains a required remote CI check.
- [x] T033 Add a transactional, checksum-pinned schema migration command and coordinate non-production API/worker ECS deployment with rollback per NFR-002/011/015 (missing). — Embedded migrations are serialized and atomic; workflow YAML/Bash, build, rollback, and migration integration contracts pass locally, with PostgreSQL execution delegated to the required remote ingest suite.
- [x] T034 Implement an authenticated one-item import/download/delete/restore/manifest smoke test in the deployment workflow and publish the exact AWS prerequisites and release procedure per AC-001/006/008 and NFR-003/011/012 (missing). — The bounded smoke script, deployment contracts, runbook, and release plan pass local checks.
- [ ] T035 Bootstrap the approved AWS non-production resources and protected GitHub configuration, execute the deployment, complete the 15-minute observation window, and record retained evidence per NFR-002/003/007/011/012 (missing). — External gate: only `AWS_NONPROD_ROLE_ARN` currently exists; no deployment mutation is authorized by repository preparation.

## Phase 11: Release review remediation

- [x] T036 Promote and roll back the API, worker, migration, and smoke evidence as one production release unit per NFR-002/011/015 (partial). — Production verifies the paired non-production evidence, uses the shared deployment path, and binds later rollback to both exact retained task definitions and artifact digests.
- [x] T037 Install paired rollback handling before the first ECS service mutation and bound delivery operations with rollback margin per NFR-002/011/012/015 (partial). — The shared script traps errors and termination before either service update, restores both captured tasks, and bounds migration, stability, smoke, deployment, and standalone rollback windows.

## Phase 12: Release review follow-up

- [x] T038 Bind deployment and rollback success to the exact API and worker task-definition ARNs running after ECS stabilization per NFR-002/011/012/015 (partial). — Both paths fail closed on circuit-breaker reversion before health, smoke, or evidence completion; the failure path is covered with a mocked ECS response.
- [x] T039 Enforce inner deployment and rollback execution budgets that reserve enough runner time for paired recovery per NFR-002/012/015 (partial). — Deployment has a 35-minute inner budget inside a 60-minute job, and standalone rollback has a 20-minute inner budget inside a 40-minute job.
