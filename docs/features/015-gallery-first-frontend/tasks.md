# Tasks: Gallery-First Frontend

**Input**: Approved spec and derived plan in `docs/features/015-gallery-first-frontend/`.
**Authorization**: Owner requested full implementation on 2026-09-29. No push/deploy authorization.
**Tests**: Required by specification; reuse existing browser/API fixtures, add behavioral and layout checks. CSS-only declarations do not need mirrored unit tests; browser geometry/focus checks validate user behavior.

## Phase 1: Setup and Design — M0

- [x] T001 Record approval, research existing contracts, produce plan/research/data-model/contracts/quickstart and validate full requirement coverage in docs/features/015-gallery-first-frontend/. No implementation test applicable; artifact consistency/link checks required.

## Phase 2: Foundation and US1 — M1

- [ ] T002 [US1] ? IN PROGRESS Compose owner workspace/secondary lifecycle/diagnostics and safe sign-out in apps/web/src/App.tsx and workspace.css; update apps/web/README.md and source/accessibility checks. Validate session/privacy/navigation behavior. FR-001, FR-018; AC-001, AC-014; NFR-004, NFR-009, NFR-014.
- [x] T003 [US1] Implement contact-sheet design, toolbar/scoped search, real counts/ranges/paging and density in apps/web/src/Gallery.tsx and gallery.css; tests in apps/web/tests/browser/frontend.spec.ts. FR-002–004; AC-002–003; NFR-010, NFR-012; SC-001.

## Phase 3: US2 — M1

- [x] T004 [US2] Implement native left filter drawer, complete common/advanced facets, draft/apply/cancel/reset, validation/chips/privacy and focus in apps/web/src/Gallery.tsx; update browser/accessibility tests and web guide. FR-005–007; AC-004–005; NFR-005–006; SC-002.

## Phase 4: US3 — M2

- [ ] T005 [US3] Implement contained detail/semantic inspector and exact image-history selection in apps/web/src/Gallery.tsx, ImageHistory.tsx and Comparison.tsx, preserve full lineage and disclose unavailable historical metadata. Extend browser fixtures. FR-009–013; AC-007–009; NFR-003, NFR-008; SC-003.
- [ ] T006 [US3] Preserve browse/reload/back/scroll, cancel obsolete reads, protect unsaved edits/session drafts and show truthful retry states in apps/web/src/Gallery.tsx/App.tsx; extend tests. FR-008, FR-016; AC-006, AC-012; NFR-008.

## Phase 5: US4 — M2

- [ ] T007 [US4] Preserve lifecycle/edit/history safeguards, add library-wide history and guarded header branch entry in apps/web/src/LibraryHistory.tsx/App.tsx, reuse existing components. Update browser regression expectations and web guide. FR-014–015; AC-010–011; NFR-004, NFR-016; SC-004.

## Phase 6: Cross-Cutting Quality and Convergence — M3

- [ ] T008 Validate responsive/zoom/text expansion/focus/reduced motion/contrast, geometry screenshots, error/private-network limits and timing in apps/web/tests/browser/frontend.spec.ts and existing gallery.spec.ts; run web checks and relevant backend regressions. Record exact browser versions/results in docs/features/015-gallery-first-frontend/validation.md. FR-017–018; AC-002, AC-013–014; NFR-002–007, NFR-009–012, NFR-015; SC-001–006.
- [ ] T009 Obtain real 100,000-asset capacity, 500-version service performance, supported screen-reader walkthrough, owner visual acceptance and pre-production rollout/rollback evidence in docs/features/015-gallery-first-frontend/validation.md. NFR-001–002, NFR-006, NFR-013–014; SC-001, SC-005–006. External access/participant required; fixture results are not a waiver.
- [ ] T010 Update apps/web/README.md and docs/features/015-gallery-first-frontend/validation.md with usage/operations/rollback, run Spec Kit convergence, append/remediate remaining numbered work and commit milestones. NFR-011, NFR-013–016; all FR/AC/SC completion gate. No pre-push review/push unless later explicitly authorized.

## Dependencies and Execution Strategy

T001 precedes T002–T007; T003 before T004–T006. T005/T006 integrate T007; T008 verifies all implementation. T009 external evidence can run independently after local UI exists; T010 concludes only when required evidence is complete or owner-waived. Sequential execution for shared Gallery/App files. A future separate contributor may work on ImageHistory and CSS after interfaces are agreed; no delegated work authorized for this implementation. MVP is US1; current request continues through all stories.

## Requirement Traceability

FR/AC coverage is inline above; contiguous ID ranges include each ID. NFR-001→T009; NFR-002→T008/T009; NFR-003→T005/T008; NFR-004→T002/T007/T008; NFR-005→T004/T008; NFR-006→T004/T008/T009; NFR-007→T008; NFR-008→T005/T006; NFR-009→T002/T008; NFR-010→T003/T008; NFR-011→T008/T010; NFR-012→T003/T008; NFR-013→T009/T010; NFR-014→T002/T009/T010; NFR-015→T008/T010; NFR-016→T007/T010. SC-001→T003/T008/T009; SC-002→T004/T008; SC-003→T005/T006/T008; SC-004→T007/T008; SC-005→T008/T009; SC-006→T008/T009.

M1 validation: TypeScript/source/rendered-accessibility checks passed; Chromium browser suite 22 passed, 1 live-reference skipped. Gallery/drawer desktop geometry and responsive fixture checks passed; historical integration is validated further in M2.
