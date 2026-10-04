# Tasks: Versioned Export Basket

**Branch**: feature/012-versioned-export-basket
**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/export-api.md](contracts/export-api.md)
**Status**: Approved on 2026-10-04; implementation in progress. Requirements approved 2026-10-03.
**Execution rule**: each slice includes tests, affected documentation, validation and task status; immutable task IDs. Continue through all milestones and supported review remediation after approval. No push or deployment authorized.

## Phase 1: Setup and approval (M0)

- [x] T001 Record maintainer approval of plan/tasks in docs/features/012-versioned-export-basket/plan.md, accept docs/architecture/decisions/0004-versioned-export-snapshots.md and 0014-export-download-and-purge-boundaries.md if authorized, validate artifact coverage and commit planning milestone (Constitution I, IV).
- [x] T002 Establish fixture/test runners in tests/exports/Platform.Exports.Tests.csproj and tests/integration/exports/run.mjs using repository harness/toolchain conventions; verify local PostgreSQL/S3 and approved darktable corpus availability, document prerequisites in quickstart.md (NFR-011).

## Phase 2: Foundation

- [x] T003 Define schema-1 preset/pin/manifest/state/reason/filename contracts and runnable invariant tests in packages/contracts/Exports/ExportContracts.cs and tests/exports/Program.cs; document mandatory manifest, originals semantics and configured catalog/limits in packages/contracts/README.md (FR-010, FR-011, FR-014, FR-017, FR-018, FR-026; AC-009, AC-014, AC-015; NFR-008, NFR-010).
- [x] T004 Add owner-scoped basket/snapshot/request/result/package/grant/audit/lease and scoped purge-tombstone/outbox tables in apps/api/Database/Migrations/021_export_baskets.sql, no retention holds or purge-blocking foreign keys; exercise additive apply/old API rollback in tests/integration/exports/run.mjs and document migration (NFR-013; FR-025). Apply-twice and prior-reader integration pass; workspace/provider journal is additive within unreleased 021.

## Phase 3: US1 persistent basket (M1)

Independent test: CRUD, duplicates, defaults and reopening across three filtered gallery contexts preserve exactly expected identities/counts without affecting lifecycle holds.

- [x] T005 [US1] Implement create/list/rename/default/delete and revision-checked add/remove/clear in apps/api/Features/Exports/ExportBasketService.cs; tests/integration/exports/run.mjs covers 20 saved baskets, distinct duplicates, request evidence surviving basket deletion and race handling; update apps/api/README.md (FR-001, FR-002, FR-006, FR-010; AC-001, AC-004; NFR-018). CRUD/revision/duplicate/quota/deleted-basket evidence pass; API README updated.
- [x] T006 [US1] Map owner-authorized private/no-store basket routes in apps/api/Features/Exports/ExportEndpoints.cs and apps/api/Program.cs; test owner/library boundaries, safe denial/audit payloads in tests/integration/exports/run.mjs; document API errors (FR-022, FR-023; AC-011; NFR-005, NFR-006).

## Phase 4: US2 exact eligible versions (M1)

Independent test: current plus three historical versions, hidden and retained displaced/deleted versions remain exactly pinned after ref movement/hide/rewrite; mutable working selection rejected; purge invalidates without extending retention.

- [x] T007 [US2] Resolve current/ref/commit original and XMP identities transactionally in apps/api/Features/Exports/ExportSelectionService.cs; require committed working state and explicit checked advanced history for hidden pins; retain distinct selected contexts; integration fixtures verify no substitution after ref movement/hide/rewrite (FR-003..FR-008; AC-002..AC-007; NFR-011).
- [ ] T008 [US2] Implement retained-byte eligibility and permanently-unavailable readiness in apps/api/Features/Exports/ExportSelectionService.cs using existing 005/006 retention contracts plus the proposed 012 tombstone consumer contract; distinguish missing bytes from authorized purge and label consumer fixtures separately from T029 real-publisher evidence; test expired/displaced/soft-deleted/hidden/integrity-error/tombstoned fixtures and absence of new/released holds; document 011 integration boundary in docs/operations/export-basket.md; validate and commit M1 (FR-008, FR-009, FR-025; AC-007, AC-013; NFR-016). - IN PROGRESS

## Phase 5: US3 preparation and verified private download (M2)

Independent test: immutable mixed-outcome snapshot produces verified original/rendered outputs and complete manifest; 50 concurrent retries create one request; altering inputs/output/manifest prevents publication.

- [x] T009 [US3] Capture immutable snapshots/preset/consent/warning acknowledgment and durable idempotency receipts in apps/api/Features/Exports/ExportPreparationService.cs; test concurrent basket edits and 50 duplicate submissions plus changed-payload conflicts in tests/integration/exports/run.mjs (FR-011, FR-012, FR-021, FR-026; AC-008, AC-010, AC-014; NFR-002, NFR-015).
- [ ] T010 [US3] Add exact pinned original/XMP export rendering, including stored-identity redispatch, and versioned private/retain export-only metadata transforms layered on the existing approved renderer profile/matrix in apps/api/Features/Rendering/RenderingService.cs, packages/contracts/Rendering/RenderingContracts.cs and apps/render-worker/RenderRuntime.cs; enforce validated JPEG treatment, render plus transform provenance and transform-aware cache identity without compatibility-primary-key migration; changed worker artifacts require existing admission/promotion, retain existing preview behavior; test approved real-render corpus and encoded metadata fixtures, document compatibility (FR-013, FR-026; AC-002, AC-003, AC-006, AC-014; NFR-006, NFR-008). - IN PROGRESS
- [x] T011 [US3] Implement database-leased preparation/outcome worker in apps/api/Features/Exports/ExportPreparationWorker.cs and registration in apps/api/Program.cs; use verified object-version originals or pinned renderer output, record all safe outcomes without substitution, enforce limits/attempt fences; tests/integration/exports/run.mjs covers source/render outages, all mixed outcomes and original-only semantics (FR-013..FR-015; AC-002, AC-008; NFR-004, NFR-011, NFR-015, NFR-017).
- [x] T012 [US3] Build bounded verified ZIPs with standard library in apps/api/Features/Exports/ExportPackageService.cs; full-request manifest/failure summary, deterministic Windows names, output hashes/lengths and separate ZIP checksum, private versioned storage and publication fencing; inject corruption/missing output/oversize failures in tests/integration/exports/run.mjs; document schema (FR-016..FR-018; AC-009, AC-015; NFR-004, NFR-008, NFR-010, NFR-015).
- [ ] T013 [US3] Add owner-only request/progress/manifest/grant routes plus dedicated-origin native exchange and range-download routes in apps/api/Features/Exports/ExportEndpoints.cs; stream exact private object through T027 CloudFront/private ALB with lifecycle checks and bounded buffers; one-use <=60-second bootstrap POST, path-scoped Secure/HttpOnly/SameSite=Lax <=5-minute owner-session cookie, session revocation and original bearer-expiry limit; checksum/ETag/If-Range/safe filename headers; test owner/operator/anonymous access, origin/CSRF/replay, native interrupted ranges, session sign-out and expired grants through the deployed path; update apps/api/README.md, validate and commit M2 (FR-016, FR-017, FR-020, FR-022, FR-023; AC-011, AC-015; NFR-003, NFR-005, NFR-006). - IN PROGRESS

## Phase 6: US4 cancellation, expiry and operations (M3)

Independent test: cancellation stops/fences further outputs, old grants deny access after cancel/expiry/purge, restart resolves visible state within ten minutes; audit survives physical cleanup.

- [ ] T014 [US4] Implement cancel/draining, attempt fencing, 24-hour expiry, derivative cleanup and bounded worker-loss recovery in apps/api/Features/Exports/ExportPreparationWorker.cs and ExportPreparationService.cs; race/fault tests cover late callbacks/package publication and shared render jobs in tests/integration/exports/run.mjs (FR-019, FR-020, FR-022; AC-008, AC-012; NFR-004, NFR-017). - IN PROGRESS
- [ ] T015 [US4] Implement export invalidation/revocation and cleanup acknowledgment in apps/api/Features/Exports/ExportSelectionService.cs and ExportPreparationWorker.cs against T028 scoped tombstone/outbox contract; do not add permanent-purge ownership to packages/storage/AssetService.cs; consumer fixtures test lifecycle-lock races versus snapshot/publication/download and deletion of exact export object versions without extra holds/shared-rendition deletion, document required 011 publisher and T029 completion gate in docs/operations/export-basket.md (FR-009, FR-025; AC-007, AC-013; NFR-016). - IN PROGRESS
- [x] T016 [US4] Add operator pause/resume/drain/cancel-pending and aggregate metrics in apps/api/Features/Exports/ExportOperationsService.cs, ExportEndpoints.cs and apps/api/Telemetry/PlatformTelemetry.cs; enforce no operator media/detail access, test pause/drain/retry and redacted denials, document controls (FR-022, FR-024; NFR-006, NFR-009, NFR-014).
- [ ] T017 [US4] Configure private export-prefix IAM/encryption/lifecycle cleanup and <=15-minute sustained-failure alarms plus T027 origin permissions/networking, log redaction and added ALB/CloudFront budget/nonprod lifecycle controls in packages/infrastructure/ following existing patterns; run infrastructure/security/contract validation and simulated quota/cancellation-backlog alarms; update docs/operations/export-basket.md, validate and commit M3 (FR-020, FR-024; NFR-005, NFR-009, NFR-014, NFR-015, NFR-016). - IN PROGRESS

## Phase 7: browser integration (M4)

Independent test: add selections from gallery/history, navigate across contexts, manage basket, consent/start/monitor/cancel/download using keyboard; current browsing state survives.

- [ ] T018 [US1] Add basket view/client/localizable messages in apps/web/src/ExportBasket.tsx, export-api.ts, export-messages.ts and App.tsx; complete CRUD/default selection/item detail/readiness/preview/output-name UI with semantic forms/live status; browser tests in apps/web/tests/browser/exports.spec.ts (FR-001, FR-002, FR-006, FR-010; AC-001, AC-004; NFR-007, NFR-012). - IN PROGRESS: round-5 request-discovery/live-announcement remediation verification.
- [x] T019 [US2] Add gallery multi-selection and explicit history/version basket actions in apps/web/src/Gallery.tsx and ImageHistory.tsx; preserve filter/branch/scroll/context, exclude hidden suggestions unless explicit advanced context; test three gallery contexts and hidden/working/displaced selections in apps/web/tests/browser/exports.spec.ts (FR-002, FR-004..FR-008; AC-001, AC-003..AC-007; NFR-007).
- [ ] T020 [US3] Add preset/privacy/warning confirmation, snapshot acknowledgment, per-item progress/failure, manifest/checksum and native download form/exchange controls without whole-file JS Blob buffering in apps/web/src/ExportBasket.tsx; test original versus rendered semantics, consent required before retained metadata, >10 MB and multi-GiB native downloads/range resume across Chrome/Edge/Firefox, grant renewal via fresh exchange and bounded memory through T027 origin in apps/web/tests/browser/exports.spec.ts (FR-011, FR-015..FR-018, FR-026; AC-008, AC-009, AC-014, AC-015; NFR-007, NFR-012, NFR-015). - IN PROGRESS
- [ ] T021 [US4] Add cancel/draining/expiry/unavailability/retry guidance and owner-session cleanup in apps/web/src/ExportBasket.tsx; keyboard/browser/locale/denial tests plus existing gallery/history regression suites, update apps/web/README.md, run web format/typecheck/build and commit M4 (FR-019, FR-020, FR-023; AC-011, AC-012; NFR-007, NFR-012). - IN PROGRESS: round-5 revocation/expired-session/live-announcement remediation verification.

## Phase 8: acceptance, operations and review (M5)

- [ ] T022 Measure 20-basket/250-item/10-request capacity and p95 open/update/submission in tests/performance/exports/run.mjs, gallery-index dependency failure in tests/integration/exports/run.mjs; record exact results and 100-item under-two-minute usability evidence in docs/features/012-versioned-export-basket/validation.md (NFR-001..NFR-003, NFR-018; SC-001, SC-003). - IN PROGRESS
- [ ] T023 Run all provenance/metadata/manifest/idempotency/authorization/purge/cancel/restart/observability fixtures and browser/locale checks; perform manual keyboard/assistive WCAG 2.2 AA review, log/telemetry privacy scan and approved real darktable corpus, record traceable evidence in docs/features/012-versioned-export-basket/validation.md (AC-001..AC-015; NFR-004..NFR-012, NFR-015..NFR-017; SC-002, SC-004, SC-005).
- [ ] T024 Complete stuck-job/provider-outage/integrity/auth/quota/backlog/retention runbooks, additive migration and capability rollout/rollback rehearsal in docs/operations/export-basket.md; update affected READMEs, CI validation entrypoints and root package.json as needed; validate repository checks and commit M5 acceptance (NFR-010, NFR-011, NFR-013, NFR-014, NFR-016). - IN PROGRESS
- [ ] T025 Assess code against full spec/plan/tasks with speckit-converge; append and implement traceable remaining work until no gaps; record evidence in docs/features/012-versioned-export-basket/validation.md (all requirements; Constitution II).
- [ ] T026 Obtain read-only alternate-model adversarial review of complete base-to-HEAD range, all artifacts and validation; independently assess findings, remediate valid in-scope blockers with task mapping, repeat review until PASS; preserve each round and commit final record unchanged in docs/features/012-versioned-export-basket/reviews/pre-push-review.md; final clean-tree handoff without push (Constitution V).

## Dependencies and execution strategy

T001 approval gates all implementation. T028 consumer contract precedes T008/T015. T030 durable write-intent/settlement invariant precedes T012 media publication and T014/T015 cleanup acknowledgment. T027 infrastructure download path precedes T013/T020 deployed validation. T029 real feature 011 integration gates full acceptance, T025 full convergence, T026 completion review and production enablement; independent consumer work may proceed without it. These append-only remediation IDs are scheduled by prerequisites rather than numeric order. T002-T004 precede US1/US2. US2 selection is required before US3 snapshots/rendering. US4 fences publication/download before browser delivery. Browser US1/US2 follows backend contracts; preparation/cancellation UI follows US3/US4. All acceptance tasks and convergence precede independent review. Deliver all stories, not only an MVP; commit each named milestone. No parallel agents except authorized reviewer. Independent opportunities within a phase: contract/fixture documentation versus isolated browser checks, after their prerequisites; shared service files remain sequential.

Unit tests apply to pure contract/state/filename/metadata rules. Database, object storage, lease races and authorization require real integration tests because unit mocks cannot prove transaction/access behavior. UI behavior requires browser tests; WCAG and usability also require manual evidence. Migration/infrastructure need rehearsal and infrastructure validation, not unit tests duplicating declarations.

## Requirement traceability

Ranges below include every ID in the inclusive range; each referenced task defines the verification/evidence path.

| Source | Tasks |
|---|---|
| FR-001..FR-002 | T005, T018, T019 |
| FR-003..FR-005 | T007, T010, T019 |
| FR-006..FR-007 | T005, T007, T018, T019 |
| FR-008..FR-009 | T007, T008, T015 |
| FR-010..FR-011 | T003, T005, T009, T018, T020 |
| FR-012..FR-014 | T009, T010, T011 |
| FR-015..FR-018 | T011, T012, T013, T020 |
| FR-019..FR-020 | T013, T014, T017, T021 |
| FR-021 | T009 |
| FR-022..FR-024 | T006, T013, T014, T016, T017, T021 |
| FR-025..FR-026 | T004, T008, T009, T010, T015, T020 |
| AC-001..AC-015 | T023, with targeted checks T005..T015, T018..T021 |
| NFR-001..NFR-003 | T009, T013, T022 |
| NFR-004..NFR-006 | T006, T010..T016, T017, T023 |
| NFR-007..NFR-008 | T003, T010, T012, T018..T021, T023 |
| NFR-009..NFR-011 | T002, T003, T007, T011, T012, T016, T017, T023, T024 |
| NFR-012..NFR-014 | T004, T016..T018, T020, T021, T023, T024 |
| NFR-015..NFR-018 | T005, T008, T009, T011, T012, T014, T015, T017, T022..T024 |
| SC-001..SC-005 | T022, T023 |

## Approval and progress

Requirements approved by maintainer on 2026-10-03. Design and tasks proposed after discovering that local/remote feature 012 had neither plan.md nor tasks.md. Design/tasks/ADRs approved by maintainer on 2026-10-04. T001 completed; implementation and validation proceed against the numbered plan. Full acceptance remains gated by T029.

Round-3 implementation-review remediation under T016/T017/T023/T024: allowlisted export-meter collection through safe structured JSON logs and CloudWatch filters/dashboard; independent exports/ current/noncurrent/multipart lifecycle backstop preserving the publication availability window. Collection/privacy and infrastructure assertions are executable. UI milestone and acceptance validation continue; no full task completion or production approval is inferred.

T006/T007/T016/T018/T021 targeted implementation, documentation and local checks are complete. M4 browser checkpoint covers 24 passing export cases, localizable messages, exact hidden-history actions, immutable retries and revocation handling. T019 additional working/displaced/library-history workflow evidence and T020 deployed transfer acceptance remain open. T023 acceptance is in progress, including actual unknown multipart-initiation draining and local >10 MB bounded/in-flight-expiry checks; T029 still blocks full completion.

Local acceptance follow-up completes T009/T011/T012/T019/T028/T030 targeted checks: concurrent basket edits and immutable receipts; original/rendered mixed outcomes, captured-renderer outage, metadata/provenance ZIP verification; 27-case browser matrix including working/hidden/library history, retained displaced backend pins; actual late multipart completion, lost completion/initiation callbacks, OS/filesystem/provider faults and consumer grant revocation. Full feature acceptance remains open under T008/T010/T013/T014/T015/T017/T020/T022–T027/T029 for integration/deployment/manual evidence. This checkpoint is not the final M5 acceptance milestone or completion/pre-push review. No new source-policy publisher or deployment is authorized.

## Phase 9: planning-review remediation and explicit integration gates

Round-5 interim review of `2680c6a..a02f1e1` found four local blockers mapped to T013/T018/T020/T021/T022/T023: bearer revocation before grant registration (012-IMPL-007), expired-JWT sign-out (008), retained-request discovery behind terminal history (009), and live announcements of polled progress (010). T018/T021 require review-remediation verification before their earlier completion claims can be relied on. Fixes and focused regressions are in progress; the numbered plan remains the task source. The pending admitted-transfer expiry adjustment additionally requires resolution of automatic approval review's rejection; full acceptance remains gated independently by T029.

- [ ] T027 Build the narrowly routed HTTPS CloudFront/private-ALB download path to existing API tasks in packages/infrastructure/src/platform-foundation-stack.ts per ADR-0014; disable caching/error caching, forward session cookies/range headers, constrain listener/security groups, configure idle/read timeouts without total completion cutoff, exact-origin CORS and safe logging, account for budget/lifecycle; validate actual >10 MB and multi-GiB downloads/ranges/revocation/timeout/memory via tests/integration/exports/run.mjs and browser matrix, document infrastructure evidence in validation.md (012-PLAN-001; FR-016, FR-020, FR-023; AC-015; NFR-003, NFR-005, NFR-008, NFR-015; prerequisite to T013/T020; M2 transport, M3 controls). - IN PROGRESS
- [x] T028 Implement the versioned scoped tombstone/outbox and trusted in-process export invalidation consumer in packages/contracts/Exports/ExportContracts.cs and apps/api/Features/Exports/ExportInvalidationService.cs per contracts/purge-invalidation.md; shared lifecycle lock/fencing and generation/idempotency checks, exact export-version cleanup acknowledgment, no public publisher; add labeled consumer fixtures in tests/integration/exports/run.mjs and document feature 011 publisher requirements without claiming real purge integration (012-PLAN-002; FR-009, FR-025; AC-007, AC-013; NFR-011, NFR-016; prerequisite to T008/T015; M1 contract, M3 cleanup).
- [ ] T029 Obtain feature 011 authoritative publisher integration and actual authorized source/XMP/recovery-copy purge evidence, consumer fencing and export-version cleanup acknowledgment in tests/integration/exports/run.mjs and docs/features/012-versioned-export-basket/validation.md; satisfy AC-007/AC-013/NFR-016 against real policy execution, not synthetic tombstones; keep full feature completion/convergence and production capability blocked until evidence exists; do not merge or implement 011 under this task without separate authorization (012-PLAN-002; FR-009, FR-025; AC-007, AC-013; NFR-011, NFR-016; M5) - BLOCKED: feature 011 is unmerged; authoritative permanent-purge publisher and integration evidence unavailable.
- [x] T030 Register durable write intents/unique destinations before every export-owned upload or multipart initiation in apps/api/Features/Exports/ExportPackageService.cs, ExportPreparationWorker.cs and ExportInvalidationService.cs plus apps/api/Database/Migrations/021_export_baskets.sql; close write admission on invalidation, require verifiable settlement of admitted storage operations and reconciliation/deletion of all intent-owned versions/multipart remnants before cleanup acknowledgment, keep unknown outcomes draining rather than relying on lease expiry/timeouts; test paused write across tombstone followed by late success, callback rejection/loss, worker death, unknown multipart initiation and deletion failure in tests/integration/exports/run.mjs; record privacy/cleanup evidence in validation.md (012-PLAN-004; FR-009, FR-025; AC-013; NFR-004, NFR-016; prerequisite to T012/T014/T015 acknowledgment and T029; M2 write admission, M3 cleanup).
