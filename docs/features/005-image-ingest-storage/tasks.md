# Tasks: Image Ingest and Durable Storage

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), and [contracts/](contracts/)
**Status**: In progress — specification/ADR approval recorded by T026; foundational review remediation continues.

## Phase 1: Setup and shared foundations

- [x] T001 Create ingest API, worker, storage, corpus, contract, and integration-test layout in `apps/api/`, `apps/ingest-worker/`, `packages/storage/`, and `tests/` (FR-001, NFR-008/009).
- [x] T002 [P] Add locked, reviewed S3, PostgreSQL, multipart-transfer, XML, and format-detection dependencies to repository manifests and `docs/dependency-inventory.md` (NFR-003/006/014). — .NET provides SHA-256 and XML; AWS SDK provides multipart transfer; API/worker use locked AWS/Npgsql packages; PostgreSQL and MinIO integration images are reviewed digest pins.
- [x] T003 [P] Build legally usable RAW/JPEG/PNG/TIFF/XMP and corruption/ambiguity corpus fixtures in `tests/fixtures/ingest/` (NFR-006/014/016).
- [ ] T004 Implement shared owner authorization, operation ID, idempotency, versioned errors, audit, and redaction adapters in `packages/contracts/` and `apps/api/` (FR-014/015, NFR-003/004). — IN PROGRESS: durable request fingerprint/reservation/replay and lifecycle-version parsing are implemented; endpoint-wide version enforcement and durable audit emission remain.

## Phase 2: Foundational data and publication controls

- [x] T005 Create PostgreSQL migrations for batches, upload sessions, content objects, assets, edit identities, paths, holds, publication intents, and reconstruction manifests in `apps/api/Database/Migrations/` (FR-004/016, NFR-008/011/016). — Includes session/content/asset publication binding and durable idempotency records; backward-compatible migration rehearsal remains T024.
- [ ] T006 [P] Implement immutable SHA-256/length object addressing, private staging/authoritative/quarantine locations, SSE-KMS, and scoped transfer authorization in `packages/storage/` (FR-002/003/008, NFR-003/013/015). — IN PROGRESS: server-side checksum-verified conditional promotion/reuse is implemented; persisted owner/session constraints and S3 fault coverage remain.
- [x] T007 [P] Implement normalized display/collision paths and Windows-invalid/case-collision validation in `packages/storage/Paths/` (FR-004/011, AC-005, NFR-010). — Explicit Windows control-character and device-name-before-extension cases are validated on every host.
- [ ] T008 Implement publication-intent outbox, atomic metadata transition, and idempotent orphan reconciliation in `apps/ingest-worker/` (FR-007/009, AC-002/007, NFR-002/015). — IN PROGRESS: worker failures are isolated and retained for retry; PostgreSQL/MinIO fault coverage and atomic multi-item publication remain.
- [ ] T009 Implement metrics, redacted audit events, alerts, and health checks for ingest, quarantine, repair, storage failure, and integrity drift in `apps/ingest-worker/Observability/` (FR-014/015, NFR-004/007/012). — IN PROGRESS: outcome instrumentation, durable audit writer, health endpoint, and alert path remain.

**Checkpoint**: verified content can be safely published or repaired before user-story work begins.

## Phase 3: User Story 1 — Import a verified photographic batch (P1)

**Independent test**: import the representative mixed batch; every item is deterministic, supported objects are checksummed/active, and unsafe inputs are private quarantine.

- [ ] T010 [P] [US1] Implement import-batch and resumable upload-session endpoints from `contracts/ingest-storage-v1.md` in `apps/api/Features/Ingest/` (FR-001/009/015, AC-002).
- [ ] T011 [P] [US1] Implement streaming size/SHA-256 verification, content reuse, and immutable source/XMP persistence in `apps/ingest-worker/Verification/` (FR-002/003/005, AC-001/002, NFR-015).
- [ ] T012 [P] [US1] Implement corpus-backed format detection, XMP well-formedness, darktable base/numbered naming, and conservative association in `apps/ingest-worker/Association/` (FR-005/006/007, AC-003/004/009, NFR-006/008).
- [ ] T013 [US1] Implement quarantine, item state/progress, safe corrective errors, atomic-batch preparation, and durable outcomes in `apps/api/Features/Ingest/` (FR-007/008/009/015, AC-003/007, NFR-001/005).
- [ ] T014 [US1] Add resumable/retry/corruption/duplicate/atomic-publication/association contract and fault tests in `tests/integration/ingest/` (FR-001–009, AC-001–004/007/009, NFR-001/009/015).

## Phase 4: User Story 2 — Retrieve and organize stored assets (P2)

**Independent test**: download exact bytes, move/case-rename 10,000 assets, and associate numbered sidecars without changing identity.

- [ ] T015 [P] [US2] Implement authorized exact-byte original/XMP retrieval with checksum/provenance response in `apps/api/Features/Retrieval/` (FR-010, AC-001, NFR-003/004).
- [ ] T016 [P] [US2] Implement move/rename with optimistic lifecycle version, path history, collision checks, and audit in `apps/api/Features/Paths/` (FR-004/011/014, AC-005, NFR-010/015).
- [ ] T017 [US2] Add retrieval, duplicate-sidecar, path history, Windows-name, and 10,000-asset collision/load tests in `tests/integration/storage/` (FR-006/010/011, AC-004/005, NFR-009/016).

## Phase 5: User Story 3 — Delete and restore safely (P3)

**Independent test**: soft-delete, verify active exclusion and deadline, restore at day 90, and prove a hold/destination conflict protects bytes and identity.

- [ ] T018 [P] [US3] Implement lifecycle state transitions, 90-day deadline, active-list exclusion, and hold/reference evaluation in `apps/api/Features/Lifecycle/` (FR-012/013/015, AC-006, NFR-014/015).
- [ ] T019 [US3] Implement restore with identity/association/path-history preservation and explicit alternate-path resolution in `apps/api/Features/Lifecycle/` (FR-011/012/014, AC-006, NFR-005/010).
- [ ] T020 [US3] Add day-90, protected-reference, concurrent-operation, and audit/redaction tests in `tests/integration/lifecycle/` (FR-012–015, AC-006, NFR-004/009/015).

## Phase 6: Recovery and operational completion

- [ ] T021 Implement versioned canonical reconstruction-manifest export and verifier in `packages/storage/Reconstruction/` (FR-016, AC-008/010, NFR-008/009).
- [ ] T022 Add sampled missing/altered/extra/mis-associated/path/hold/audit/handoff manifest fault tests in `tests/integration/reconstruction/` (FR-016, AC-008/010, NFR-009/015).
- [ ] T023 Publish ingest, quarantine, orphan-repair, checksum-drift, quota, outage, and manifest-verification runbooks in `docs/runbooks/` (NFR-007/012).
- [ ] T024 Rehearse backward-compatible schema/storage rollout and repairable rollback in `docs/features/005-image-ingest-storage/validation.md` (NFR-002/011/015).
- [ ] T025 Measure batch, storage inventory/cost, and 100k/1m capacity targets in `tests/performance/ingest/` and record evidence in `docs/features/005-image-ingest-storage/validation.md` (NFR-001/013/016).
- [x] T026 Obtain owner approval for the feature specification and proposed ADR, then update `spec.md` and `docs/architecture/decisions/0005-immutable-content-and-repairable-publication.md`.

## Dependencies and traceability

T001–T009 are required before stories. Deliver US1 (T010–T014), then US2 (T015–T017), then US3 (T018–T020); recovery closure follows. FR-001–009: T004–T014; FR-010–011: T007/T015–T017; FR-012: T018–T020; FR-013: T018/T020; FR-014–015: T004/T009/T018–T020; FR-016: T005/T021–T022. AC-001–004: T010–T014; AC-005: T007/T016–T017; AC-006: T018–T020; AC-007: T008/T013–T014; AC-008: T021–T022; AC-009: T012–T014; AC-010: T021–T022. NFR-001–010: T002–T025; NFR-011: T005/T024; NFR-012: T009/T023; NFR-013: T006/T025; NFR-014: T002/T003/T018/T023; NFR-015: T006/T008/T014/T020/T022/T024; NFR-016: T003/T005/T017/T025. Milestone commits: integrity foundation; import MVP; retrieval/path; lifecycle; reconstruction readiness.
