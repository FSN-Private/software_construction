# Tasks: Darktable Rendering and Preview Provenance

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [rendering-v1 contract](contracts/rendering-v1.md), [ADR-0007](../../architecture/decisions/0007-isolated-darktable-rendering-provenance.md), and [ADR-0009](../../architecture/decisions/0009-production-renderer-vulnerability-gate.md).

**Tests**: The specification requires automated model, integration, corpus, failure, resource-limit, and performance coverage. Native rendering evidence is a trusted-runner release gate; it cannot be represented by a fake successful CLI execution.

## Phase 1: Planning and supply-chain foundation

- [x] T001 Create implementation design, research, data model, quickstart, and v1 API contract in `docs/features/008-darktable-rendering/` (FR-001–017, NFR-001–017).
- [x] T002 Record the isolated native-renderer/provenance boundary in `docs/architecture/decisions/0007-isolated-darktable-rendering-provenance.md` (FR-009/017, NFR-005).
- [x] T003 Add the reviewed digest-pinned darktable artifact, license, SBOM, vulnerability scan, and regression evidence to `docs/dependency-inventory.md` and `docs/rendering-compatibility.md` (FR-001/015/016, NFR-007/010/016). — The hardened immutable ECR digest has digest-qualified Trivy SBOM/SARIF with 0 critical/high and 5 accepted medium findings; it remains pending until T026 post-merge acceptance.

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
- [x] T010 [US1] Add PostgreSQL/MinIO current-preview integration coverage and a trusted-runner current RAW/XMP render gate in `tests/rendering/current/` (AC-001/003/004/007, NFR-001/002/005/011/017). — Validated by `node tests/integration/ingest/run.mjs` on the local PostgreSQL/MinIO stack and the trusted-runner current-XMP state in the ECR-digest corpus evidence on 2026-09-24.

## Phase 4: User Story 2 — Inspect a historical version (P1)

**Goal**: An owner can request an exact selected feature-006 XMP version and never receive a substitute rendition.

**Independent test**: Select two committed XMP identities, request previews, and verify distinct hashes/provenance; request an unavailable version and observe no derivative.

- [x] T011 [P] [US2] Add library-scoped selected-version, unavailable-without-fallback, and exact historical-provenance tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-002/005/007/013, AC-002/005/009, NFR-004/011). — Validated by rendering and Version Control unit suites on 2026-09-21.
- [x] T012 [US2] Implement the feature-006 selected-version adapter and historical request path in `apps/api/Features/Rendering/VersionControlSelectedVersionResolver.cs` and `apps/api/Features/Rendering/RenderingService.cs` (FR-002/013, AC-002/005/009). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T013 [US2] Add current-plus-three-historical-XMP native corpus assertions in `tests/rendering/corpus/` (FR-005/015, AC-002/009, NFR-007/011). — Validated by the CC0 CR2 fixture, current plus three historical XMP states, checksum-enforcing native harness, published ECR-digest evidence, and non-production compatibility approval on 2026-09-23.

## Phase 5: User Story 3 — Understand rendering failures (P2)

**Goal**: An owner sees an actionable safe failure and can create a distinct retry attempt after correction.

**Independent test**: Submit malformed, missing, unsupported, resource-limited, and corrected inputs; verify safe category, separate retry attempt, and no substituted result.

- [x] T014 [P] [US3] Add failure-category, retry, timeout, output-validation, workspace-cleanup, and no-path/no-secret tests in `tests/unit/Platform.Api.Foundation.Tests/RenderingChecks.cs` (FR-010–014/017, AC-005–007, NFR-003/006/011/014/015). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T015 [US3] Implement safe failure/status/retry API behavior, accessible text, and render audit events in `apps/api/Features/Rendering/RenderingEndpoints.cs` and `apps/api/Features/Rendering/RenderingService.cs` (FR-006/012/014, AC-005/006, NFR-006/008/009/015). — Validated by `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` on 2026-09-23.
- [x] T016 [US3] Add malformed/missing/unsupported/resource/retry/cleanup integration and native runner checks in `tests/rendering/failures/` (FR-010–014/017, AC-005–007, NFR-003/009/011/014). — Validated by PostgreSQL/MinIO safe-failure/retry checks and the Ubuntu trusted-runner native missing-source, malformed-XMP, unsupported-source, enforced-timeout, and disposable-workspace checks against the exact local digest on 2026-09-24.

## Phase 6: Operations and release readiness

- [ ] T017 Configure the renderer worker image, SQS/DLQ, restrictive IAM, resource limits, queue drain/cancel, restart recovery, private retention, metrics, and alerts in `packages/infrastructure/` (FR-008/010/011/014/017, NFR-003/005/009/012–014/017). — IN PROGRESS: local CDK synthesis declares encrypted SQS/DLQ, a retained private derivative bucket, a zero-count renderer Fargate service with 1 vCPU/2 GiB/30 GiB workspace and no database secrets, source-read/derivative-write IAM, queue/DLQ alerts, executable SQS consumption, pause/cancel/recovery controls, and coordinated release/rollback delivery. Live non-production deployment and capacity validation remain outstanding.
- [x] T018 Add compatibility approval workflow, corpus evidence template, failure/queue-drain/retention runbooks, and a release plan in `docs/rendering-compatibility.md`, `docs/runbooks/`, and `docs/releases/008-darktable-rendering/release-plan.md` (FR-001/015/016, NFR-007/010/012/013/016).
- [ ] T019 Implement cache/uncached/freshness capacity measurements and validate [quickstart.md](quickstart.md) in `tests/rendering/performance/` and `docs/features/008-darktable-rendering/validation.md` (NFR-001/002/005/017, SC-001–006). — BLOCKED for current-preview integration and worker infrastructure: T010 and T017.
- [ ] T020 Run Spec Kit convergence, resolve appended tasks, validate all non-blocked requirements, and record feature review evidence in `docs/features/008-darktable-rendering/` (all FR/AC/NFR).
- [ ] T021 Enforce the non-production-only compatibility scope and production scan-diff gate in deployment configuration and release validation; remediate every finding accepted for the active digest before any production promotion (AC-010, NFR-018, ADR-0009).

## Dependencies and execution order

- T001–T002 are complete design prerequisites.
- T004–T006 unblock US1 and US3 model implementation.
- T007–T010 deliver current previews; their native validation waits for T003.
- T011–T013 use Feature 006's merged library-scoped selected-version contract.
- T014–T016 may run after T004–T006 and operate without historical resolution.
- T017–T019 depend on the approved renderer artifact; T020 runs after every unblocked task and again after each blocker resolves.
- T021 blocks every production renderer promotion until its remediation and replacement-digest scan requirements are satisfied.

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
| FR-015–016 | T003, T013, T018–T019, T026, T029 |
| FR-017 | T006, T009–T010, T014, T017 |
| AC-001–004 | T007–T010 |
| AC-005–007 | T006, T008–T009, T011–T012, T014–T016 |
| AC-008–009 | T003, T011–T013, T018–T019 |
| AC-010 | T021 |
| NFR-001–004 | T004–T005, T007–T010, T014–T019, T025–T026, T029 |
| NFR-005–009 | T003, T006, T010, T014–T019 |
| NFR-010–017 | T003–T005, T010, T013–T020 |
| NFR-018 | T021 |

## Implementation strategy

Implement and commit the render-model/containment milestone first, then current-preview and failure slices. Do not mark a native-rendered acceptance criterion complete without trusted-runner evidence. Historical resolution uses Feature 006's library-scoped runtime contract and retains `version_resolution_unavailable` for outages or unavailable selected states.

## Phase 7: Convergence

- [x] T022 Implement the executable renderer runtime: SQS envelope consumption, idempotent claim/visibility handling, immutable source materialization, darktable command creation, private derivative publication, and AWS IAM/SigV4-authenticated worker-to-API completion/failure callbacks without database credentials (FR-002/004/007–011/014/017, AC-001/003/004/007, NFR-002–005/009/011/014, ADR-0010). — Validated by API/worker builds, foundation queue-envelope checks, CDK synthesis/assertions, infrastructure dependency/security validation, and the local `platform-render-worker:feature008-local` Docker build on 2026-09-24.
- [x] T023 Return an owner-authorized private derivative locator with completed status/provenance and verify cross-library access is refused (FR-006, AC-001/002/003, NFR-005/006). — Validated by foundation locator/provenance and cross-library-refusal checks on 2026-09-24.
- [x] T024 Implement authenticated render-intake pause, pending-work drain/cancel, and stale-running-attempt recovery within ten minutes; connect the controls to the renderer queue/service and operational events (FR-008/014/017, NFR-003/009/013/014). — Validated by foundation tests on 2026-09-24; every cancelled pending attempt records a `cancelled` event, and the recovery worker runs every minute and rejects only attempts stale for more than ten minutes.
- [ ] T025 Add cache-hit, uncached-render, and 100,000-image thumbnail-freshness capacity measurements using the executable renderer runtime; update the feature validation evidence and quickstart (NFR-001/002/005/017, SC-001–006; missing). — IN PROGRESS: user-approved modeled native freshness evidence measures a 4,570 ms p95 CR2 thumbnail and derives a 1,509-worker lower bound for 99,000 thumbnails in five minutes. NFR-017/SC-006 are waived; cache-hit/request-start latency still requires a deployed HTTP fixture.
## Phase 8: Convergence

- [ ] T026 Deploy the approved renderer release to non-production and run the owner-authorized trusted-Linux four-state corpus plus HTTP fixture to measure cache-hit and uncached request-start p95, retaining immutable deployment evidence (NFR-001/002/005/009, FR-015; post-merge acceptance).
- [x] T027 Define and implement a renderer capacity policy that can meet the modeled 1,509-worker lower bound for the 100,000-image freshness target without exceeding the approved cost constraint, or obtain and record an explicit requirement waiver (NFR-012/017, SC-006; partial). — The product owner explicitly waived NFR-017/SC-006 on 2026-09-24; the USD 20 monthly constraint and retained capacity model remain in effect.
- [x] T028 Bind compatibility selection and render provenance to the exact renderer image digest deployed with the API release; reject a mismatched digest before queueing (FR-005/015/016, AC-008/010, NFR-010/018; critical review remediation). — Validated by foundation and release-workflow contract checks on 2026-09-24.
- [x] T029 Gate candidate admission behind the configured release operator, compare four decoded-pixel corpus references, run owner-scoped HTTP acceptance before activation, and abort the candidate before paired rollback (FR-015/016, AC-008, NFR-001/002/004/013/014; adversarial-review remediation). — Validated by API build, foundation tests, shell syntax checks, and release-workflow contract tests on 2026-09-24. Live evidence remains T026 post-merge acceptance.
