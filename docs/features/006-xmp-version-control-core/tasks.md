# Tasks: XMP Version-Control Core

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/)
**Status**: Approved — implementation authorized 2026-09-10; feature-004/005 contracts available.

## Phase 1: Foundations

- [x] T001 Create version-control domain, persistence, API, projection, and graph-test layout in `apps/api/Features/VersionControl/` and `tests/version-control/` (FR-020, NFR-008/009). — Domain/contracts, PostgreSQL persistence schema, integrity API, and executable graph-test layout build cleanly.
- [x] T002 [P] Define canonical manifest encoding/digest fixtures and compatible reader/writer versions in `packages/contracts/version-control/` (FR-004/020, NFR-006/011). — Version 1 uses fixed-order UTF-8 JSON and a locked digest fixture; unknown readers fail explicitly.
- [x] T003 [P] Add locked graph/serialization dependencies and audit source/license/advisory evidence in `docs/dependency-inventory.md` (NFR-003/014). — No package added: .NET JSON/SHA-256/collections and the already-reviewed Npgsql package cover the work.
- [x] T004 Create migrations for working changes, staging, commits/entries, refs/tags, reflogs, visibility, projections, divergence, and idempotency in `apps/api/Database/Migrations/` (FR-002–020, NFR-011/016). — Expand-only migration is embedded in the existing checksum-verified migrator.
- [x] T005 Implement feature-005 authoritative-content/identity/hold adapter and feature-004 error/audit/redaction adapter in `apps/api/Features/VersionControl/Adapters/` (FR-001/019/022, NFR-003/004). — Adapter validates owner-scoped authoritative identities and publishes payload-free holds/audits.
- [x] T006 Implement DAG integrity, parent/scope/cycle checks, manifest verification, and ref quarantine in `apps/api/Features/VersionControl/Integrity/` (FR-018, AC-008, NFR-007/015). — Integrity endpoint detects digest/manifest/parent/scope/cycle faults, records a redacted metric, and quarantines affected library refs.

## Phase 2: User Story 1 — Stage and commit coherent XMP changes (P1)

**Independent test**: a 100-image staged set either publishes all entries and one head advance or publishes neither under every injected failure.

- [ ] T007 [P] [US1] Implement working-state and stage/unstage APIs with base/current identity review in `apps/api/Features/VersionControl/Working/` (FR-002/003/020, AC-001).
- [ ] T008 [P] [US1] Implement canonical immutable commit/entry/manifest creation in `apps/api/Features/VersionControl/Commits/` (FR-001/004, AC-001, NFR-006/015).
- [ ] T009 [US1] Implement serializable expected-head commit/ref/reflog/hold transaction and idempotency outcome in `apps/api/Features/VersionControl/Publishing/` (FR-005/006/015/022, AC-001–003, NFR-001/015).
- [ ] T010 [US1] Add staged-set, single-step failure, retry, and 100-replay property/integration tests in `tests/version-control/commits/` (FR-002–006, AC-001–003, NFR-009/015).

## Phase 3: User Story 2 — Branch and compare safely (P2)

**Independent test**: divergent branches/tags/compare/revert work; stale heads preserve both sides and default rewrite fails.

- [ ] T011 [P] [US2] Implement normalized branch/tag CRUD, protection, exact resolution, and audit in `apps/api/Features/VersionControl/Refs/` (FR-008/009/013/019, AC-005).
- [ ] T012 [P] [US2] Implement compare, divergence, image projection, and whole-library traversal APIs in `apps/api/Features/VersionControl/Query/` (FR-007/010/011/020, AC-004/009, NFR-001/016).
- [ ] T013 [US2] Implement restore/revert as new reachable commit and dirty-checkout block/preserve/discard contract in `apps/api/Features/VersionControl/Operations/` (FR-012/021, AC-010).
- [ ] T014 [US2] Add concurrent CAS, tag/ref naming, protected-default, compare/divergence, checkout, and projection tests in `tests/version-control/refs/` (FR-007–013/021, AC-002/004/005/009/010).

## Phase 4: User Story 3 — Clean and recover history presentation (P3)

**Independent test**: a confirmed non-default rewrite moves one ref, preserves hashes, supports day-90 recovery, and hide/unhide changes only presentation.

- [ ] T015 [P] [US3] Implement rewrite impact preview, confirmation, ref-only move, immutable reflog, and recovery references in `apps/api/Features/VersionControl/Rewrite/` (FR-014–016/019/022, AC-005/006).
- [ ] T016 [P] [US3] Implement reversible visibility metadata and show-hidden projections in `apps/api/Features/VersionControl/Visibility/` (FR-017/020/022, AC-007).
- [ ] T017 [US3] Implement resolver-supplied two-parent merge publication with expected-target CAS in `apps/api/Features/VersionControl/MergePublishing/` (FR-023, AC-012).
- [ ] T018 [US3] Add rewrite/recovery/hold expiry, hide/unhide, reachable-ref, and resolved-merge atomicity tests in `tests/version-control/recovery/` (FR-014–017/022/023, AC-005–007/011/012).

## Phase 5: Reliability, operations, and readiness

- [ ] T019 Rebuild materialized state/image projections solely from immutable graph evidence in `apps/api/Features/VersionControl/Projection/` (FR-007/018, AC-004/008, NFR-008/015).
- [ ] T020 Add graph corruption, rebuild, latency, million-entry capacity, locale, authorization, and redaction tests in `tests/version-control/verification/` (FR-018–020, AC-008, NFR-001/003/004/009/010/016).
- [ ] T021 Publish stale-ref, divergence, corruption, rebuild, reflog expiry/recovery, protected-ref, and cost runbooks in `docs/runbooks/` (NFR-007/012/013/014).
- [ ] T022 Rehearse manifest/schema compatibility migration and rollback/forward-recovery constraints in `docs/features/006-xmp-version-control-core/validation.md` (NFR-011/015).
- [x] T023 Obtain owner approval for the specification and ADR, then update `spec.md` and `docs/architecture/decisions/0006-content-addressed-xmp-commit-dag.md`. — Approved by the owner on 2026-09-10 with authorization to run the feature through completion.

## Traceability

FR-001–009: T005/T007–T012; FR-010: T012/T014; FR-011–012: T012–T014; FR-013: T011/T014; FR-014–017: T015–T018; FR-018: T006/T019–T020; FR-019: T004/T011/T015; FR-020: T001/T002/T007/T012/T016/T020; FR-021: T013–T014; FR-022: T005/T009/T015–T018; FR-023: T017–T018. AC-001: T009–T010; AC-002: T009/T014; AC-003: T009–T010; AC-004: T012/T014/T019; AC-005: T011/T014–T015/T018; AC-006–007: T015–T018; AC-008: T006/T019–T020; AC-009–010: T012–T014; AC-011: T005/T015/T018; AC-012: T017–T018. NFR-001: T009/T012/T020; NFR-002: T009/T020; NFR-003: T003/T005/T020; NFR-004: T004/T020; NFR-005: T007/T012; NFR-006: T002/T008/T022; NFR-007: T006/T021; NFR-008: T001/T019; NFR-009: T010/T014/T018/T020; NFR-010: T002/T020; NFR-011: T002/T004/T022; NFR-012: T021; NFR-013: T015/T021; NFR-014: T003/T021; NFR-015: T006/T009/T010/T014/T018–T022; NFR-016: T004/T012/T020. Milestones: graph foundation; commit MVP; refs/compare; recovery/visibility; rebuild readiness.
