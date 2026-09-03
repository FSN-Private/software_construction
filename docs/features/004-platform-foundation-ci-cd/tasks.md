# Tasks: Platform Foundation and CI/CD

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), and [contracts/](contracts/)  
**Status**: Proposed — execute only after the specification and plan are approved.

## Phase 1: Setup

- [ ] T001 Create the .NET, Node, CDK, release-tool, test, and runbook layout in `apps/`, `packages/`, `tests/`, and `docs/runbooks/` (FR-001, NFR-008/009).
- [ ] T002 [P] Pin .NET 10 and Node 24 toolchain configuration in `global.json`, `.nvmrc`, and repository documentation (FR-001, NFR-006/009).
- [ ] T003 [P] Define deterministic local validation commands in `README.md` and root package/build scripts (FR-001, AC-001, NFR-009).
- [ ] T004 [P] Add lockfile and dependency inventory policy in `docs/dependency-policy.md` (FR-015, AC-008, NFR-003/016).

## Phase 2: Foundational delivery controls

- [ ] T005 Create versioned shared error, operation-ID, idempotency, audit, telemetry-redaction, health, migration, and promotion fixtures in `packages/contracts/` (FR-016, AC-011, NFR-004/008).
- [ ] T006 [P] Implement API correlation, structured redacted logging, metrics, and tracing foundations in `apps/api/` (FR-013, AC-006, NFR-004/007).
- [ ] T007 [P] Create CDK account boundary, private network, KMS, budget, telemetry, and bootstrap-evidence constructs in `packages/infrastructure/` (FR-010/011/017, AC-009/010, NFR-003/007/013).
- [ ] T008 [P] Implement release-manifest, checksum, SBOM, provenance, and compatibility verification tooling in `packages/release-tools/` (FR-005/008/009/015, AC-003/008, NFR-016).
- [ ] T009 Establish CI workflow skeleton, stable job names, timeout/concurrency, read-only defaults, and aggregate result in `.github/workflows/ci.yml` (FR-002/003/004, AC-001/002, NFR-001/015).
- [ ] T010 Add GitHub OIDC trust-policy templates, protected environment evidence, and drift verification in `packages/infrastructure/` and `.github/workflows/verify-protection.yml` (FR-010/017, AC-002/010, NFR-003).

**Checkpoint**: The foundation is ready for independently testable stories.

## Phase 3: User Story 1 — Validate a proposed change (P1)

**Independent test**: Seed one failure per required category and prove its stable check blocks merging while a corrected revision passes.

- [ ] T011 [P] [US1] Add backend formatting, static analysis, unit/integration, package, and contract test commands in `apps/api/` and `tests/` (FR-001/002, AC-001, NFR-001/009).
- [ ] T012 [P] [US1] Add web formatting, typecheck, accessibility, test, and build commands in `apps/web/` (FR-001/002, AC-001, NFR-005/006/009).
- [ ] T013 [P] [US1] Add infrastructure synthesis, assertion, IaC, secret, dependency, license, and vulnerability validation commands in `packages/infrastructure/` and `packages/release-tools/` (FR-001/015, AC-001/008, NFR-003/016).
- [ ] T014 [US1] Implement protected/fork-safe CI jobs and the `ci / required` stale/cancelled/skipped guard in `.github/workflows/ci.yml` (FR-002/003/004, AC-001/002, NFR-001/015).
- [ ] T015 [US1] Add validation-failure fixtures and check-name contract tests in `tests/infrastructure/ci-contract/` (FR-001/003, AC-001, NFR-009).

## Phase 4: User Story 2 — Promote and roll back one release artifact (P2)

**Independent test**: Build one release, deploy non-production, promote identical digests after approval, inject failed health, and restore the last compatible release without rebuilding.

- [ ] T016 [P] [US2] Create locked container, web, CDK, and Windows package build definitions in `apps/` and `packages/` (FR-005/015, AC-008, NFR-016).
- [ ] T017 [US2] Implement attested build-and-manifest generation in `.github/workflows/build-release.yml` (FR-005, AC-003/008, NFR-016).
- [ ] T018 [US2] Implement exact-digest non-production deployment, smoke, and health evidence in `.github/workflows/deploy-nonprod.yml` (FR-006/008, AC-003, NFR-011).
- [ ] T019 [US2] Implement tag, approval, evidence, and database-compatibility gates for production promotion in `.github/workflows/release.yml` (FR-007/008, AC-003/004, NFR-003/011).
- [ ] T020 [US2] Implement compatible-manifest rollback and forward-recovery refusal in `.github/workflows/rollback.yml` and `docs/runbooks/deployment-recovery.md` (FR-009, AC-004, NFR-011/012).
- [ ] T021 [US2] Add immutable-promotion and failed-health integration tests in `tests/infrastructure/release/` (FR-005–009, AC-003/004, NFR-015/016).

## Phase 5: User Story 3 — Operate an authenticated observable skeleton (P3)

**Independent test**: Provision non-production, sign in as owner, deny anonymous access, trace a synthetic request, and verify a threshold alert and budget context.

- [ ] T022 [P] [US3] Implement v1 health, session, version, error, and operation-ID contract in `apps/api/` and `packages/contracts/` (FR-012/013/016, AC-005/006/011, NFR-004/008).
- [ ] T023 [P] [US3] Implement private Cognito owner configuration and no-sign-up boundary in `packages/infrastructure/identity/` (FR-012, AC-005, NFR-003/004).
- [ ] T024 [P] [US3] Implement accessible sign-in and service/deployment-status shell in `apps/web/` (FR-012/013, AC-005, NFR-005/006/010).
- [ ] T025 [US3] Configure deployment, authentication, availability, CI, cost, and backup-age-placeholder alerts plus dashboards/runbook links in `packages/infrastructure/observability/` (FR-014, AC-007, NFR-002/007/012/013).
- [ ] T026 [US3] Add API contract, authorization, redaction, trace-correlation, locale, and accessibility tests in `tests/contract/` and `tests/integration/` (FR-012/013/016, AC-005/006/011, NFR-004/005/010).
- [ ] T027 [US3] Add non-production alert, budget, and account-recreation exercises in `tests/infrastructure/operations/` (FR-011/014/017, AC-007/009/010, NFR-007/013).

## Phase 6: Cross-cutting completion

- [ ] T028 Publish CI, security incident, rollback/forward recovery, alert failure, cost anomaly, and bootstrap runbooks in `docs/runbooks/` (FR-014, NFR-012/014).
- [ ] T029 Complete a third-party action/package review in `docs/dependency-inventory.md` (FR-015, AC-008, NFR-003/014/016).
- [ ] T030 Execute the quickstart and clean-machine validation in `docs/features/004-platform-foundation-ci-cd/validation.md` (FR-001, NFR-009).
- [ ] T031 Run cancellation, duplicate delivery, runner loss, superseding revision, and rollback-compatibility fault injection in `tests/infrastructure/reliability/` (FR-003/009, AC-004, NFR-015).
- [ ] T032 Revalidate region availability, quotas, action SHAs, package patches, pricing, and code-signing custody in `docs/features/004-platform-foundation-ci-cd/pre-deploy-check.md` (NFR-003/011/013/016).
- [ ] T033 Obtain owner approval for the specification and ADR, then update `spec.md` and `docs/architecture/decisions/0003-platform-foundation-and-delivery.md`.

## Dependencies and milestones

`T001–T010` block all stories. Complete US1 (`T011–T015`) first, then US2 (`T016–T021`), then US3 (`T022–T027`); Phase 6 validates the integrated result. Milestone commits: foundation controls; CI MVP; immutable release path; authenticated skeleton; operations readiness.

## Traceability

FR-001–004: T003, T009, T011–T015; FR-005–011: T007–T010, T016–T021, T027; FR-012–014: T006, T022–T028; FR-015: T004, T008, T013, T016–T017, T029, T032; FR-016: T005, T022, T026; FR-017: T007, T010, T027. AC-001: T011–T015; AC-002: T009–T010/T014; AC-003: T008/T017–T019/T021; AC-004: T019–T021/T031; AC-005: T022–T024/T026; AC-006: T006/T022/T026; AC-007: T025/T027; AC-008: T004/T013/T016–T017/T029; AC-009: T007/T027; AC-010: T010/T027; AC-011: T005/T022/T026. NFR-001–009: T002–T032; NFR-010: T024/T026; NFR-011: T018–T020/T032; NFR-012: T020/T025/T028; NFR-013: T007/T025/T027/T032; NFR-014: T028/T029; NFR-015: T009/T014/T021/T031; NFR-016: T004/T008/T013/T016–T017/T029/T032.
