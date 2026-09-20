# Tasks: Windows Cloud Files Synchronization

**Status**: Round 3 remediation complete — independent re-review pending 2026-09-20; trusted-host/signing evidence remains a release gate.

## Phase 1: Foundations

- [X] T001 Create provider, native-adapter, journal, companion UI, installer, and Windows-host test layout in `apps/windows-sync/` and `tests/windows-sync/` (FR-018, NFR-008/009).
- [X] T002 [P] Add locked/reviewed Cloud Files, SQLite, credential-storage, installer, and signing dependencies in repository manifests and `docs/dependency-inventory.md` (NFR-003/014).
- [X] T003 Implement versioned Cloud Files adapter, opaque identity metadata, and Windows-safe path mapper in `apps/windows-sync/Platform/` (FR-003/004/012, AC-007, NFR-006/008/010).
- [X] T004 Implement protected credential custody and SQLite journal schema/replay/migrations in `apps/windows-sync/State/` (FR-002/014, NFR-003/011/015).
- [X] T005 Implement feature-005/006 client/server adapters, immutable content downloads, error/audit/redaction, metrics, and trusted-host CI boundary in `apps/windows-sync/Remote/`, `apps/api/Features/WindowsSync/`, and `.github/workflows/` (FR-017/020, NFR-004/007/009).

## Phase 2: User Story 1 — Browse and hydrate (P1)

**Independent test**: enumerate 100k placeholders and hydrate darktable files across interruption/restart with exact bytes.

- [X] T006 [P] [US1] Implement device/root registration, branch cursor enumeration, and placeholder population in `apps/windows-sync/Provider/` (FR-001–004, AC-001).
- [X] T007 [US1] Implement range-aware hydration, temporary bytes, SHA-256 completion, retry/cancellation, and status transitions in `apps/windows-sync/Provider/Hydration/` (FR-005/015, AC-002, NFR-001/002/015).
- [X] T008 [US1] Add Explorer/darktable hydration, interruption, restart, and namespace performance tests on `tests/windows-sync/integration/` (FR-003–005, AC-001/002, NFR-001/006/009/016).

## Phase 3: User Story 2 — Pin and edit offline (P2)

**Independent test**: pin recursively, edit offline across restart, reconnect, and upload exactly once as working state.

- [X] T009 [P] [US2] Implement recursive pin/unpin, per-item outcome, safe dehydration, and disk/quota protection in `apps/windows-sync/Provider/Availability/` (FR-006/007/015, AC-003, NFR-013/015).
- [X] T010 [P] [US2] Implement stable-write detection, local XMP journal, hash/base/idempotency upload, and restart replay in `apps/windows-sync/Sync/` (FR-008–011/014, AC-004, NFR-002/015).
- [X] T011 [US2] Implement offline move/rename/delete/restore journal semantics distinct from dehydration in `apps/windows-sync/Sync/Lifecycle/` (FR-012/019, AC-007/009).
- [X] T012 [US2] Add offline/darktable/restart/pin/disk/lock/antivirus/delete-restore transfer tests in `tests/windows-sync/offline/` (FR-006–012/019, AC-003/004/007/009, NFR-009/015).

## Phase 4: User Story 3 — Branch and conflict safety (P3)

**Independent test**: two devices use different branches; dirty checkout and divergence preserve every XMP input.

- [X] T013 [P] [US3] Implement checkout dirty-state block/preserve/discard, branch delta, and per-device independence in `apps/windows-sync/Sync/Checkout/` (FR-002/013, AC-005/006).
- [X] T014 [P] [US3] Implement expected-base conflict preservation, remote/web change application, and companion conflict/status UI in `apps/windows-sync/Sync/Conflicts/` and `apps/windows-sync/UI/` (FR-010/015/020, AC-006/010, NFR-005).
- [X] T015 [US3] Implement revocation/auth-expiry pause and explicit local cleanup/export guidance in `apps/windows-sync/Device/` and `docs/runbooks/` (FR-016/017, AC-008, NFR-003/012/014).
- [X] T016 [US3] Add two-device, stale cursor, dirty checkout, branch, conflict, web-update, revocation, Unicode/name, and accessibility tests in `tests/windows-sync/conflicts/` (FR-013–017/020, AC-005–008/010, NFR-005/007/010/015).

## Phase 5: Release readiness

- [X] T017 Document darktable XMP settings, excluded databases/configuration, installation, diagnostics consent, signing, uninstall, and retained-local-data policy in `docs/windows-sync.md` (FR-018, NFR-014).
- [X] T018 Rehearse journal/root-compatible upgrade and rollback, cursor reconciliation, and safe uninstall in `docs/features/007-windows-cloud-files-sync/validation.md` (NFR-011/012).
- [X] T019 Measure 100k namespace, five devices, multiterabyte no-mirror, transfer accounting, reliability, and fault-matrix evidence in `tests/windows-sync/performance/` (NFR-001/013/016).
- [X] T020 Obtain owner approval for the specification and ADR, then update `spec.md` and `docs/architecture/decisions/0007-windows-cloud-files-provider-and-journal.md`.

## Phase 6: Pre-push remediation

- [X] T021 Dispose PostgreSQL namespace readers before cursor mutation and preserve a terminal head cursor so idle polling does not re-enumerate 100,000 items (B-001; FR-003, AC-001, NFR-001/016).
- [X] T022 Reconcile a complete branch snapshot by removing only clean absent placeholders, preserving unsafe bytes, and preventing stale-branch path collisions (B-003; FR-002/003/013/014, AC-005/006, T013/T016).
- [X] T023 Carry authoritative asset/edit lifecycle versions through namespace, journal, replay, response handling, and routes (B-004; FR-010/012/019, AC-007/009, T011/T012).
- [X] T024 Correct asynchronous HTTP request lifetime/error parsing, align device/checkout response contracts, and keep the API routable when Windows Sync storage is unconfigured (FR-001/002/017, T005/T013).
- [X] T025 Derive namespace path/visibility from authoritative asset/edit lifecycle state and invalidate cursors with an owner-scoped lifecycle revision; verify cross-device move/delete/restore convergence (B-005; FR-012/019, AC-005/009, T011/T013/T023).
- [X] T026 Preserve conflict metadata when a delete lifecycle replay conflicts after the local file is already absent (FR-010/019, AC-007/009, T011/T012).
- [X] T027 Translate a coordinated original/sidecar rename into one asset move and preserve edit-only sidecar renames as explicit conflicts (B-006; FR-012/019, AC-005/007, T011/T012).

## Traceability

FR-001–006: T003–T009; FR-007: T009/T012; FR-008–012: T010–T012/T014; FR-013–014: T004/T013–T016; FR-015: T007/T009/T014; FR-016–017: T005/T015–T016; FR-018: T001/T017; FR-019: T011–T012; FR-020: T005/T014/T016. AC-001–002: T006–T008; AC-003–004: T009–T012; AC-005–008: T013–T016; AC-009: T011–T012; AC-010: T014/T016. NFR-001: T007–T008/T019; NFR-002: T007/T010/T012; NFR-003: T002/T004/T015; NFR-004: T005/T016; NFR-005: T014/T016; NFR-006: T003/T008; NFR-007: T005/T016; NFR-008: T001/T003/T004; NFR-009: T001/T005/T008/T012/T016; NFR-010: T003/T016; NFR-011: T004/T018; NFR-012: T015/T018; NFR-013: T009/T019; NFR-014: T002/T015/T017; NFR-015: T004/T007/T010/T012/T016/T019; NFR-016: T008/T019. Milestones: provider foundation; hydration MVP; offline journal; branch/conflict safety; signed-release readiness.
