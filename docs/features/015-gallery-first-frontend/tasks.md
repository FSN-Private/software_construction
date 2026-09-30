# Tasks: Gallery-First Frontend

**Input**: Approved spec and derived plan in `docs/features/015-gallery-first-frontend/`.
**Authorization**: Owner requested full implementation on 2026-09-29. No push/deploy authorization.
**Tests**: Required by specification; reuse existing browser/API fixtures, add behavioral and layout checks. CSS-only declarations do not need mirrored unit tests; browser geometry/focus checks validate user behavior.

## Phase 1: Setup and Design — M0

- [x] T001 Record approval, research existing contracts, produce plan/research/data-model/contracts/quickstart and validate full requirement coverage in docs/features/015-gallery-first-frontend/. No implementation test applicable; artifact consistency/link checks required.

## Phase 2: Foundation and US1 — M1

- [x] T002 [US1] Compose owner workspace/secondary lifecycle/diagnostics and safe sign-out in apps/web/src/App.tsx and workspace.css; update apps/web/README.md and source/accessibility checks. Validate session/privacy/navigation behavior. FR-001, FR-018; AC-001, AC-014; NFR-004, NFR-009, NFR-014.
- [x] T003 [US1] Implement contact-sheet design, toolbar/scoped search, real counts/ranges/paging and density in apps/web/src/Gallery.tsx and gallery.css; tests in apps/web/tests/browser/frontend.spec.ts. FR-002–004; AC-002–003; NFR-010, NFR-012; SC-001.

## Phase 3: US2 — M1

- [x] T004 [US2] Implement native left filter drawer, complete common/advanced facets, draft/apply/cancel/reset, validation/chips/privacy and focus in apps/web/src/Gallery.tsx; update browser/accessibility tests and web guide. FR-005–007; AC-004–005; NFR-005–006; SC-002.

## Phase 4: US3 — M2

- [x] T005 [US3] Implement contained detail/semantic inspector and exact image-history selection in apps/web/src/Gallery.tsx, ImageHistory.tsx and Comparison.tsx, preserve full lineage and disclose unavailable historical metadata. Extend browser fixtures. FR-009–013; AC-007–009; NFR-003, NFR-008; SC-003.
- [x] T006 [US3] Preserve browse/reload/back/scroll, cancel obsolete reads, protect unsaved edits/session drafts and show truthful retry states in apps/web/src/Gallery.tsx/App.tsx; extend tests. FR-008, FR-016; AC-006, AC-012; NFR-008.

## Phase 5: US4 — M2

- [x] T007 [US4] Preserve lifecycle/edit/history safeguards, add library-wide history and guarded header branch entry in apps/web/src/LibraryHistory.tsx/App.tsx, reuse existing components. Update browser regression expectations and web guide. FR-014–015; AC-010–011; NFR-004, NFR-016; SC-004.

## Phase 6: Cross-Cutting Quality and Convergence — M3

- [x] T008 Validate responsive/zoom/text expansion/focus/reduced motion/contrast, geometry screenshots, error/private-network limits and timing in apps/web/tests/browser/frontend.spec.ts and existing gallery.spec.ts; run web checks and relevant backend regressions. Record exact browser versions/results in docs/features/015-gallery-first-frontend/validation.md. FR-017–018; AC-002, AC-013–014; NFR-002–007, NFR-009–012, NFR-015; SC-001–006. Local validation complete; T009/T011 hold external evidence for SC-001/002/005/006.
- [ ] T009 Obtain real 100,000-asset capacity, 500-version service performance, supported screen-reader walkthrough, owner visual acceptance and pre-production rollout/rollback evidence in docs/features/015-gallery-first-frontend/validation.md. NFR-001–002, NFR-006, NFR-013–014; SC-001, SC-005–006. External access/participant required; fixture results are not a waiver.
- [ ] T010 Update apps/web/README.md and docs/features/015-gallery-first-frontend/validation.md with usage/operations/rollback, run Spec Kit convergence, append/remediate remaining numbered work and commit milestones. NFR-011, NFR-013–016; all FR/AC/SC completion gate. No pre-push review/push unless later explicitly authorized.

## Dependencies and Execution Strategy

T001 precedes T002–T007; T003 before T004–T006. T005/T006 integrate T007; T008 verifies all implementation. T009 external evidence can run independently after local UI exists; T010 concludes only when required evidence is complete or owner-waived. Sequential execution for shared Gallery/App files. A future separate contributor may work on ImageHistory and CSS after interfaces are agreed; no delegated work authorized for this implementation. MVP is US1; current request continues through all stories.

## Requirement Traceability

FR/AC coverage is inline above; contiguous ID ranges include each ID. NFR-001→T009; NFR-002→T008/T009; NFR-003→T005/T008; NFR-004→T002/T007/T008; NFR-005→T004/T008; NFR-006→T004/T008/T009; NFR-007→T008; NFR-008→T005/T006; NFR-009→T002/T008; NFR-010→T003/T008; NFR-011→T008/T010; NFR-012→T003/T008; NFR-013→T009/T010; NFR-014→T002/T009/T010; NFR-015→T008/T010; NFR-016→T007/T010. SC-001→T003/T008/T009; SC-002→T004/T008; SC-003→T005/T006/T008; SC-004→T007/T008; SC-005→T008/T009; SC-006→T008/T009.

M1 validation: TypeScript/source/rendered-accessibility checks passed; Chromium browser suite 22 passed, 1 live-reference skipped. Gallery/drawer desktop geometry and responsive fixture checks passed; historical integration is validated further in M2.

## Phase 7: Convergence

- [ ] T011 Time the owner's rating + camera/tag → image → capture metadata/history → gallery return journey without diagnostics, record the result against SC-002's two-minute target in docs/features/015-gallery-first-frontend/validation.md, and remediate any observed usability blocker (partial; SC-002). Requires owner participation; deterministic browser navigation checks do not substitute for the timed owner journey.

## Phase 8: Owner-requested revision — M4 through M7

- [x] T012 Document the 2026-09-30 owner-approved behavior and read-only values contract in spec/plan/tasks/contracts; check constitution, requirement traceability, branch/clean tree and contract risks. M4. FR-019–023; AC-015–018; NFR-017–018.
- [x] T013 Add owner/branch-scoped bounded searchable filter-values API over the active gallery projection in apps/api/Features/Gallery/, with integration checks for beyond-page values, owner isolation, hidden location, numeric values and invalid facets. M4/M5. FR-020; AC-016; NFR-017. Local PostgreSQL/S3 integration and API build passed 2026-09-30.
- [ ] T014 Make grouped compact filters, one-row date ranges, API-backed existing choices and free numeric bounds in apps/web/src/Gallery.tsx/gallery.css; update browser tests and guide. M5. FR-019–020; AC-015–016; NFR-017. — IN PROGRESS
- [ ] T015 Make gallery use full viewport and image detail a native overlay that preserves URL/back/focus, exact selected preview and same-overlay comparison; update ImageHistory/Comparison and browser tests. M6. FR-021/023; AC-015/018; NFR-006/008.
- [ ] T016 Add recursive folder selection with relative-path collision validation; identify the preview fixture as read-only and remove Inspector docking from the interactive preview. Update import/preview browser tests and web guide. M6. FR-022; AC-017; NFR-018.
- [ ] T017 Run affected API/web tests, type/format/build/accessibility and deterministic responsive/privacy/preview regressions; update validation evidence and run convergence. Commit M7. FR-019–023; AC-015–018; NFR-017–018. Keep T009/T011 pending until external evidence exists.

Revision traceability: FR-019→T014; FR-020→T013/T014; FR-021→T015; FR-022→T016; FR-023→T015; AC-015→T014/T015/T017; AC-016→T013/T014/T017; AC-017→T016/T017; AC-018→T015/T017; NFR-017→T013/T014/T017; NFR-018→T016/T017.
