# Tasks: Gallery Metadata Search

**Status**: Approved by project owner on 2026-09-25; implementation in progress.
**Branch**: `feature/009-gallery-metadata-search`
**Task IDs are stable after implementation begins.** Each completed task includes its test and documentation in the same work slice.

## Phase 1: Approval and foundation (M1)

- [x] T001 Record owner approval of [plan.md](plan.md), [data-model.md](data-model.md), [gallery-v1.md](contracts/gallery-v1.md), [ADR-0003](../../architecture/decisions/0003-branch-scoped-web-working-overlay.md), and this task list; resolve any material decision before code (all IDs). Approved in conversation on 2026-09-25.
- [x] T002 Select the smallest maintained embedded metadata parser covering the feature-005 format corpus; document source, pinned version/license/advisories/transitive impact, update locked package files, and validate restore/build (`NFR-005`, `NFR-008`, `NFR-010`). NuGet restore, API build, and transitive vulnerability audit passed 2026-09-25; fixture coverage continues in T004.
- [ ] T003 Add expand-only gallery projection, facet, owner-preference, refresh checkpoint, and shared web-overlay migrations in `apps/api/Database/Migrations/`; test fresh and upgrade migration plus rollback generation selection (`FR-003`, `FR-007`, `FR-013`, `NFR-004`, `NFR-013`, `NFR-016`) — IN PROGRESS: origin/master contains the squash-merged 006–008 prerequisites.
- [ ] T004 Implement bounded extraction, normalization, XMP precedence and clear-state handling for every documented facet in `apps/api/Features/Gallery/Metadata/`; verify JPEG/PNG/TIFF/DNG and supported RAW corpus, malformed XMP, missing timezone, and source provenance (`FR-003`, `FR-007`, `FR-015`, `AC-001`, `AC-003`, `NFR-010`, `NFR-011`).
- [ ] T005 Implement idempotent, resumable, transactionally published projection refresh and triggers for import, lifecycle, committed/XMP working changes; test interruption, stale-source rejection, and no mixed records (`NFR-003`, `NFR-013`, `NFR-016`). Validate M1 .NET build/tests and migration checks, update docs, inspect diff, then commit M1.

## Phase 2: Browse and details (M2)

- [ ] T006 Implement owner-scoped, canonical-library-checked, allowlisted combined filters, exact count, indexed stable keyset sorting, cursor binding/expiry, sensitive-field suppression, and failed-access audit in `apps/api/Features/Gallery/Browse/`; test each facet, sort direction, tie/null behavior, filter change, adjacent pages, and denied access (`FR-001`, `FR-003`–`FR-005`, `FR-013`, `FR-016`, `AC-001`, `AC-002`, `AC-007`, `NFR-001`, `NFR-004`, `NFR-005`, `NFR-006`, `NFR-011`, `NFR-014`).
- [ ] T007 Implement asset details with source/clear state, current branch/working status, thumbnail pending/failed/available state and exact feature-008 provenance; test source and rendition mismatches (`FR-002`, `FR-006`, `FR-007`, `FR-010`, `FR-015`, `AC-003`, `AC-008`, `NFR-003`).
- [ ] T008 Build keyboard-accessible React gallery cards, filter/sort/count controls, pagination, detail view, URL browse context, preview status, invariant-value localization, and explicit original/full-preview actions in `apps/web/src/`; run typecheck, build, and browser accessibility checks (`FR-001`–`FR-007`, `FR-012`, `FR-014`, `AC-001`–`AC-003`, `AC-008`, `NFR-002`, `NFR-007`, `NFR-008`, `NFR-012`, `NFR-014`).
- [ ] T009 Add owner geolocation preference and explicit reveal endpoint; prove hidden coordinates are absent from cards, normal details, filters, facets, counts, telemetry, and unauthenticated responses while XMP stays intact (`FR-013`, `AC-007`, `NFR-005`, `NFR-006`, `NFR-015`).
- [ ] T010 Connect portal import, download, delete, and restore controls to existing feature-005 endpoints with owner checks, optimistic lifecycle version, and existing audit; verify lifecycle and denied access (`FR-016`, `FR-017`, `AC-009`, `NFR-005`, `NFR-015`). Validate M2 .NET/web tests/build, inspect diff, update docs, then commit M2.

## Phase 3: Working edits and validation (M3)

- [ ] T011 Implement namespace-aware allowlisted XMP patching for rating, rejection, color, tags, title, description, and geolocation, preserving unknown nodes and the original binary; use verified object publication and feature-006 working change CAS/idempotency, audit accepted/rejected edits, and reject unsupported fields (`FR-008`–`FR-012`, `FR-016`, `FR-018`, `AC-004`, `AC-005`, `AC-006`, `NFR-005`, `NFR-011`).
- [ ] T012 Make shared web working changes visible to every eligible feature-007 device on the branch and to owner-authorized feature-006 staging/commit, retain device-local conflicts, and expose refresh/retry/conflict in API and UI; test two devices, branch switch, stale base, revoked device, explicit commit, and supported darktable round trip with unrelated XMP intact (`FR-009`–`FR-011`, `FR-018`, `AC-004`, `AC-006`, `AC-010`, `NFR-004`, `NFR-011`).
- [ ] T013 Add end-to-end owner/denied-access, lifecycle, gallery, privacy, and current Chrome/Edge/Firefox tests plus 100,000-asset reference load benchmark for page and cached-thumbnail percentiles; record environment, measurements, and any index/query tuning (`AC-001`–`AC-010`, `SC-001`–`SC-004`, `NFR-001`–`NFR-008`, `NFR-011`).
- [ ] T014 Add redacted latency/error/preview/conflict/facet telemetry, refresh progress, operations runbook, migration/backfill/rollback and recovery checks; validate logs contain no sensitive values (`NFR-009`, `NFR-013`, `NFR-015`, `NFR-016`).
- [ ] T015 Integrate gallery tests into stable `ci / backend` and `ci / web` jobs, update contract, user guidance, compatibility matrix, validation evidence, and release plan; run full applicable local checks, dependency audit, task-to-requirement traceability, and Spec Kit convergence until no actionable gap remains. Inspect diff and commit M3 (`FR-001`–`FR-018`, `AC-001`–`AC-010`, `NFR-001`–`NFR-016`).

## Phase 4: Review (M4)

- [ ] T016 Obtain the independent alternate-model adversarial review, independently validate and remediate findings, repeat until explicit agreement, record every round in `reviews/pre-push-review.md`, validate again, and commit M4. No push without separate authorization.

## Dependencies and traceability

T001 blocks implementation. T002–T005 establish the index; T006–T010 depend on a complete projection; T011–T012 depend on working-state and namespace integration; T013–T015 follow the assembled feature; T016 follows convergence. M1, M2, M3, and M4 each require a commit.

All functional requirements map to T003–T012 and T015; every acceptance criterion maps to its functional task and T013; all non-functional requirements map to their named task and T015. Unit tests are applicable to normalization, query/cursor logic, XMP patching, and conflict handling. Browser accessibility, PostgreSQL consistency, Windows sync, darktable, and performance behavior require higher-level checks in T008, T012, and T013.
