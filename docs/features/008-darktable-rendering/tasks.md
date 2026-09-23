# Tasks: Darktable Rendering and Preview Provenance

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [rendering-v1 contract](contracts/rendering-v1.md), and [ADR-0007](../../architecture/decisions/0007-isolated-darktable-rendering-provenance.md).

**Tests**: The specification requires automated model, integration, corpus, failure, resource-limit, and performance coverage. Native rendering evidence is a trusted-runner release gate; it cannot be represented by a fake successful CLI execution.

## Phase 1: Planning and supply-chain foundation

- [x] T001 Create implementation design, research, data model, quickstart, and v1 API contract in `docs/features/008-darktable-rendering/` (FR-001–017, NFR-001–017).
- [x] T002 Record the isolated native-renderer/provenance boundary in `docs/architecture/decisions/0007-isolated-darktable-rendering-provenance.md` (FR-009/017, NFR-005).
- [ ] T003 Add the reviewed digest-pinned darktable artifact, license, SBOM, vulnerability scan, and regression evidence to `docs/dependency-inventory.md` and `docs/rendering-compatibility.md` (FR-001/015/016, NFR-007/010/016). — IN PROGRESS: trusted runner and a source-verified 5.6.1 local candidate are available. The existing CC0 `tests/fixtures/ingest/sample.dng` corpus input and the non-production ECR/approval evidence path are documented. Blocked on a successful SBOM/CVE report, native corpus evidence, immutable registry publication, and explicit owner approval.

## Phase 2: Shared rendering foundation

- [x] T004 Implement canonical rendition-key, request/attempt state, provenance, safe-code, resource-policy, and selected-version resolver contracts in `packages/contracts/Rendering/` with unit checks in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-002/005–008/010/012–014, NFR-004/006/008/010/011/015). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj`.
- [x] T005 Implement durable rendering request, attempt, rendition, provenance, compatibility, and event tables with atomic completion/coalescing constraints in `apps/api/Database/Migrations/008_rendering.sql` and migration checks in `tests/unit/Platform.Api.Foundation.Tests/MigrationChecks.cs` (FR-005–008/014–016, NFR-004/010/014). — Validated by the PostgreSQL/MinIO integration suite on 2026-09-21.
- [x] T006 Implement a no-write source resolver, private derivative store abstraction, queue envelope, and constrained process/workspace executor interfaces in `apps/render-worker/` and `apps/api/Features/Rendering/` with isolation tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-002/004/009–011/017, AC-007, NFR-005/006/011). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj`.

## Phase 3: User Story 1 — View a current edit preview (P1)

**Goal**: An owner can request configured thumbnails or an on-demand preview for a current feature-005 edit and observe a cache hit or one pending render.

**Independent test**: Seed a current immutable asset/XMP pair, request each rendition, complete through the executor seam, and prove exact provenance/cache reuse without source mutation.

- [x] T007 [P] [US1] Add current-preview request, cache-hit, duplicate-coalescing, output/provenance atomicity, and owner-isolation tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-003/005–009, AC-001/003/004/007, NFR-001/002/004/011). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T008 [US1] Implement owner-authorized current-edit source resolution, canonical cache lookup, request creation, and status response in `apps/api/Features/Rendering/RenderingService.cs` and `apps/api/Features/Rendering/RenderingEndpoints.cs` (FR-002/003/005–008/013, AC-001/003/004/005, NFR-001/004/008). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T009 [US1] Implement queue dispatch, idempotent claim, private workspace cleanup, output validation, private derivative publication, and atomic completed provenance in `apps/render-worker/RenderWorker.cs` (FR-004/007–011/014/017, AC-003/004/007, NFR-002–005/009/014). — Validated by worker/API builds and `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [ ] T010 [US1] Add PostgreSQL/MinIO current-preview integration coverage and a trusted-runner current RAW/XMP render gate in `tests/rendering/current/` (AC-001/003/004/007, NFR-001/002/005/011/017). — BLOCKED for native portion: trusted runner and approved darktable artifact are unavailable.

## Phase 4: User Story 2 — Inspect a historical version (P1)

**Goal**: An owner can request an exact selected feature-006 XMP version and never receive a substitute rendition.

**Independent test**: Select two committed XMP identities, request previews, and verify distinct hashes/provenance; request an unavailable version and observe no derivative.

- [x] T011 [P] [US2] Add library-scoped selected-version, unavailable-without-fallback, and exact historical-provenance tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-002/005/007/013, AC-002/005/009, NFR-004/011). — Validated by rendering and Version Control unit suites on 2026-09-21.
- [x] T012 [US2] Implement the feature-006 selected-version adapter and historical request path in `apps/api/Features/Rendering/VersionControlSelectedVersionResolver.cs` and `apps/api/Features/Rendering/RenderingService.cs` (FR-002/013, AC-002/005/009). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [ ] T013 [US2] Add current-plus-three-historical-XMP native corpus assertions in `tests/rendering/corpus/` (FR-005/015, AC-002/009, NFR-007/011). — BLOCKED: the CC0 RAW input is already present, but T003 must supply the approved registry digest and trusted native-run evidence before reference XMP/output records can be accepted.

## Phase 5: User Story 3 — Understand rendering failures (P2)

**Goal**: An owner sees an actionable safe failure and can create a distinct retry attempt after correction.

**Independent test**: Submit malformed, missing, unsupported, resource-limited, and corrected inputs; verify safe category, separate retry attempt, and no substituted result.

- [x] T014 [P] [US3] Add failure-category, retry, timeout, output-validation, workspace-cleanup, and no-path/no-secret tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-010–014/017, AC-005–007, NFR-003/006/011/014/015). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T015 [US3] Implement safe failure/status/retry API behavior, accessible text, and render audit events in `apps/api/Features/Rendering/RenderingEndpoints.cs` and `apps/api/Features/Rendering/RenderingService.cs` (FR-006/012/014, AC-005/006, NFR-006/008/009/015). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [ ] T016 [US3] Add malformed/missing/unsupported/resource/retry/cleanup integration and native runner checks in `tests/rendering/failures/` (FR-010–014/017, AC-005–007, NFR-003/009/011/014).

## Phase 6: Operations and release readiness

- [ ] T017 Configure the renderer worker image, SQS/DLQ, restrictive IAM, resource limits, queue drain/cancel, restart recovery, private retention, metrics, and alerts in `packages/infrastructure/` (FR-008/010/011/014/017, NFR-003/005/009/012–014/017). — IN PROGRESS: the CDK stack now declares the separate immutable, scan-on-push, retained renderer repository and exports it for protected non-production configuration. Worker runtime resources remain blocked on T003's approved renderer artifact.
- [x] T018 Add compatibility approval workflow, corpus evidence template, failure/queue-drain/retention runbooks, and a release plan in `docs/rendering-compatibility.md`, `docs/runbooks/`, and `docs/releases/008-darktable-rendering/release-plan.md` (FR-001/015/016, NFR-007/010/012/013/016).
- [ ] T019 Implement cache/uncached/freshness capacity measurements and validate [quickstart.md](quickstart.md) in `tests/rendering/performance/` and `docs/features/008-darktable-rendering/validation.md` (NFR-001/002/005/017, SC-001–006). — BLOCKED for native evidence: T003 and T012.
- [ ] T020 Run Spec Kit convergence, resolve appended tasks, validate all non-blocked requirements, and record feature review evidence in `docs/features/008-darktable-rendering/` (all FR/AC/NFR).

## Dependencies and execution order

- T001–T002 are complete design prerequisites.
- T004–T006 unblock US1 and US3 model implementation.
- T007–T010 deliver current previews; their native validation waits for T003.
- T011–T013 use Feature 006's merged library-scoped selected-version contract.
- T014–T016 may run after T004–T006 and operate without historical resolution.
- T017–T019 depend on the approved renderer artifact; T020 runs after every unblocked task and again after each blocker resolves.

## Traceability

| Source | Tasks |
|---|---|
| FR-001 | T003, T013, T018 |
| FR-002 | T004, T006, T008, T011–T012 |
| FR-003 | T007–T010 |
| FR-004 | T006, T009–T010 |
| FR-005–008 | T004–T005, T007–T013 |
| FR-009–011 | T006, T009–T010, T014, T017 |
| FR-012–014 | T004, T014–T016, T017 |
| FR-015–016 | T003, T013, T018–T019 |
| FR-017 | T006, T009–T010, T014, T017 |
| AC-001–004 | T007–T010 |
| AC-005–007 | T006, T008–T009, T011–T012, T014–T016 |
| AC-008–009 | T003, T011–T013, T018–T019 |
| NFR-001–004 | T004–T005, T007–T010, T014–T019 |
| NFR-005–009 | T003, T006, T010, T014–T019 |
| NFR-010–017 | T003–T005, T010, T013–T020 |

## Implementation strategy

Implement and commit the render-model/containment milestone first, then current-preview and failure slices. Do not mark a native-rendered acceptance criterion complete without trusted-runner evidence. Historical resolution uses Feature 006's library-scoped runtime contract and retains `version_resolution_unavailable` for outages or unavailable selected states.
