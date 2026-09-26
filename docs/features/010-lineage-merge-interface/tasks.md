# Tasks: Lineage and Merge Interface

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contract](contracts/lineage-merge-v1.md)
**Branch**: `feature/010-lineage-merge-interface`
**Rule**: This numbered checklist is the sole execution plan. Complete code, applicable unit tests, affected documentation, and validation in the same task before checking it.

## Phase 1 — Planning milestone

- [x] T001 Validate the 010 design against features 006/008/009, complete plan/research/data model/contract/quickstart, run `$speckit-analyze`, and commit the planning milestone (FR-001–023, AC-001–016, NFR-001–017). No implementation test applies; artifact consistency is the verification. — Cross-artifact analysis found and resolved content-provenance, source-ref atomicity, and re-authentication design gaps; no unresolved critical inconsistency remains.

## Phase 2 — History and comparison milestone

- [ ] T002 [US1] Add owner-scoped paged image lineage with exact parent/ref/tag/hidden context and bounded loading in `apps/api/Features/Lineage/` and `packages/contracts/lineage/`; add .NET projection and authorization tests (FR-001/002/013/014/016/017, AC-001/002/008/016, NFR-001/004/005/010/014/015).
- [ ] T003 [US1] Add a context-preserving gallery history entry and keyboard-accessible graph plus searchable table in `apps/web/src/Gallery.tsx`, `apps/web/src/Lineage.tsx`, and styles; add browser navigation/accessibility checks (FR-001/002/022, AC-001/009/014/016, NFR-006/007/013/014).
- [ ] T004 [US2] Add exact committed image comparison and independent feature 008 preview/provenance states in the lineage API; test absent image, no common ancestor, pending/failed preview, and sensitive-error handling (FR-003/004/016, AC-003, NFR-002/004/005/010/016).
- [ ] T005 [US2] Add two-version selection, textual differences, preview state, provenance, and degraded-render behavior in the web workspace; add browser tests and update user guidance (FR-003/004, AC-003/009, NFR-006/007/013/016).
- [ ] T006 Validate the history/comparison milestone with .NET tests, web typecheck/format/source/accessibility/browser checks and representative 500-version timing; update `validation.md`, task states, and commit (NFR-001/002/010/014).

## Phase 3 — Safe merge milestone

- [ ] T007 [US3] Implement versioned conservative XMP diff/merge policy in `apps/api/Features/Lineage/` using authoritative bounded XML reads; add fixtures for seven recognized fields, disjoint changes, concurrent scalar changes, unknown processing/mask/history nodes, malformed input, and exact output bytes (FR-003/008/009, AC-004/007, NFR-005/009/010).
- [ ] T008 [US3] Add the server-generated XMP provenance migration and feature 006 content-authority/integrity checks, then implement owner-scoped merge preparation over exact source/target heads and complete library-wide affected identities; test cross-asset forgery, absent objects, no base, changed heads, and no mutation during prepare (FR-007/008/020, AC-004/012/015, NFR-003/004/010/016/017).
- [ ] T009 [US3] Implement final reviewed resolution and idempotent confirmation through feature 006 prepared-entry and two-parent publication; check both refs inside the publication transaction and working state before confirmation, then prove atomicity, audit, and stale rejection in integration tests (FR-009/010/015/020, AC-005/007/012/015, NFR-003/005/008/010/012).
- [ ] T010 [US3] Add web merge review with source/target/base, all affected images, exact proposed XMP and preview states, explicit conflict selection, and final confirmation; add keyboard/browser tests and help text (FR-007–010/016/020, AC-004/005/007/009/012/015, NFR-006/007/013).
- [ ] T011 Validate clean/conflict/stale/duplicate/interrupted merge fixtures and performance; update `validation.md`, task states, and commit the safe merge milestone (NFR-002/003/008/010/012).

## Phase 4 — History actions and readiness milestone

- [ ] T012 Add branch/tag creation and removal, staged commit preparation/publication, hide/reveal, restore/revert, and protected/non-default rewrite review through feature 006; test expected-state and audit evidence in `apps/web/src/Lineage.tsx` and lineage/API tests (FR-005/006/011–019/023, AC-002/006/008/010/011/016, NFR-003/004/005/010/015).
- [ ] T013 Add browser branch switching with gallery web working and device-conflict protection, preserve/stash/confirmed-discard paths, and tests showing no silent loss (FR-010/021/023, AC-013/014/015, NFR-003/010/012).
- [ ] T014 Add five-minute signed `auth_time` re-authentication gate to rewrite/recovery, mutation-disable, safe telemetry/alert and incident/rollback guidance in lineage API and `docs/runbooks/`; exercise authorization, privacy, interruption, three locale/time-zone settings, and rollout/rollback compatibility (FR-015/016/018, NFR-004/005/007/008/011/012/013/015/017).
- [ ] T015 Run full local .NET/web/integration/browser validation, capacity and accessibility checks; record evidence and any unavailable external gate in `validation.md`; update user/API documentation (all AC and NFR IDs).
- [ ] T016 Run `$speckit-converge`, implement every actionable appended task, repeat until no work remains, complete traceability, and commit the readiness milestone (all FR, AC, NFR and SC IDs).

## Dependency order and milestones

T001 precedes implementation. T002 and T004 establish server reads; T003 follows T002, T005 follows T004. T006 closes milestone 2. T007 precedes T008, T008 precedes T009 and T010, and T011 closes milestone 3. T012 and T013 follow history and merge foundations; T014 and T015 verify readiness; T016 closes milestone 4. Independent test and documentation changes stay in their corresponding task. No parallel agent work is required.

## Traceability

| Requirement IDs | Implementation/verification tasks |
|---|---|
| FR-001–004 | T002–T005, T015 |
| FR-005–006 | T012, T015 |
| FR-007–010 | T007–T010, T013, T015 |
| FR-011–019 | T002, T009, T012–T014, T015 |
| FR-020–023 | T003, T008–T010, T012–T013, T015 |
| AC-001–003 | T002–T005, T015 |
| AC-004–005 | T007–T011, T015 |
| AC-006–008 | T002, T012, T015 |
| AC-009–012 | T003, T005, T009–T012, T015 |
| AC-013–016 | T002–T003, T008–T010, T012–T013, T015 |
| NFR-001–002 | T002, T004, T006, T011, T015 |
| NFR-003–005 | T002, T004, T007–T009, T012–T015 |
| NFR-006–007 | T003, T005, T010, T014–T015 |
| NFR-008–010 | T002, T004, T007–T009, T011–T015 |
| NFR-011–012 | T009, T011, T013–T015 |
| NFR-013–014 | T002–T003, T005–T006, T010, T014–T015 |
| NFR-015–017 | T002, T004, T012, T014–T015 |
| SC-001–006 | T006, T011, T015–T016 |
