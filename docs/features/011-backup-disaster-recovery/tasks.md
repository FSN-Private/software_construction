# Tasks: Backup and Disaster Recovery

**Branch**: `feature/011-backup-disaster-recovery-fresh`
**Status**: Approved for local implementation on 2026-10-05; external qualification remains gated.
**Source of intent**: [spec.md](spec.md), [plan.md](plan.md), [data-model.md](data-model.md), [contracts/recovery.md](contracts/recovery.md).
**Branch exception**: The owner explicitly requested a fresh branch on 2026-10-05. Keep the canonical feature directory and preserve the stale branch without merging or rewriting it.

T001–T006 preserve the stale plan's IDs and scope. T007 onward make that scope executable. M0 design is approved. Application implementation and acceptance evidence are tracked below. Approval T006 precedes all implementation despite its inherited ID. Each umbrella task completes only after its supporting tasks, validation, and milestone commit.

## Phase 1: Design and approval (M0)

- [x] T006 Obtain owner approval of the concrete design, cold-storage policy, independent identity recovery, workload envelope, validation gates and documented assumptions in `docs/features/011-backup-disaster-recovery/plan.md`; record approval in `spec.md` and accept or supersede `docs/architecture/decisions/0003-cross-region-library-recovery.md` only when authorized. Validate artifact consistency using speckit-analyze; commit the approved planning milestone. — ? Approved by owner on 2026-10-05; M0 artifact validation passed.

## Phase 2: Shared foundations (M1)

- [ ] T001 Define data classification, recovery manifests, holds, purge and drill schema in `packages/recovery/` (FR-001–FR-013, FR-017; AC-001, AC-007, AC-009). Complete after T007–T009 and M1 validation/commit.
- [ ] T007 Define versioned recovery-point, exact object-version identity, verification, restore, hold, purge and drill records in `packages/recovery/Platform.Recovery.csproj` and `packages/recovery/RecoveryContracts.cs`; reuse content-address and export contracts. Add isolated executable checks in `tests/recovery/Platform.Recovery.Tests.csproj` and `tests/recovery/Program.cs`; reject malformed hashes, foreign owners, unsupported schema versions and ambiguous timestamps (FR-001, FR-005, FR-010, FR-013; NFR-009, NFR-014, NFR-016).
- [ ] T008 Add durable owner-scoped recovery points, observations, restores, validation revisions, holds, purge journals and audit-chain records through additive `apps/api/Database/Migrations/022_backup_recovery.sql`; exercise apply-twice, old-reader compatibility and independent reconstruction in `tests/integration/recovery/run.mjs`. Document compatibility and migration in `docs/operations/backup-recovery.md` (FR-003, FR-009–FR-013, FR-017; NFR-002, NFR-006, NFR-015, NFR-016).
- [ ] T009 Implement independent recovery-object adapters and streamed exact-version SHA-256 verification in `packages/recovery/RecoveryObjectStore.cs`; distinguish unavailable, corrupt, archived/pending retrieval and verified content. Test actual bytes, swapped versions, empty inventories, interrupted reads and denied key access in `tests/recovery/Program.cs` and `tests/integration/recovery/run.mjs`; document trusted evidence boundaries (FR-002–FR-005, FR-010, FR-016; AC-001, AC-006; NFR-001, NFR-002, NFR-004, NFR-005, NFR-015).

## Phase 3: US1 — Verify protection (M2)

Independent test: create all protected fixture classes, establish a consistent recovery point, verify recovery-account objects and database/configuration/access evidence, then inject late/missing/corrupt copies. Health must fail closed and alerts must meet the 15-minute requirement.

- [ ] T002 [US1] Implement recovery-account/region storage, independent KMS/IAM, replication, cold lifecycle, inventory and alerts in `packages/infrastructure/src/recovery/` (FR-002–FR-008, FR-016, FR-018; AC-001–AC-004, AC-011; NFR-001–NFR-007, NFR-011, NFR-015). Complete after T010–T012 and corresponding infrastructure validation.
- [ ] T003 [US1] Implement integrity/coverage verification and owner health status in `apps/api/Features/Recovery/` (FR-005, FR-006, FR-018; AC-001, AC-002, AC-011; NFR-002, NFR-003, NFR-013, NFR-016). Complete after T013–T015 and M2 validation/commit.
- [ ] T010 [P] [US1] Create opt-in independently deployable recovery stack and primary read-only capture integration in `packages/infrastructure/src/recovery/recovery-stack.ts` and `packages/infrastructure/src/app.ts`; reject equal accounts/regions, placeholders in deployment mode, public access and primary-controlled recovery administrators. Recovery-controlled copies must use durable write intents; do not enable unfenced native replication that could resurrect purged content. Keep existing deployments unchanged until explicitly configured. Assert synthesized policy/key/versioning/retention boundaries in `tests/infrastructure/recovery/recovery-contract.mjs` and document ordered provisioning in `packages/infrastructure/README.md` (FR-002–FR-004, FR-012, FR-016; NFR-001, NFR-004, NFR-005, NFR-011, NFR-015).
- [ ] T011 [US1] Implement immutable database/catalog/configuration/release/access evidence capture and consistent recovery watermark orchestration in `packages/recovery/BackupCoordinator.cs` and `apps/recovery-worker/`; coordinate PostgreSQL 18 `pg_dump` with the same exported MVCC snapshot as the inventory every 15 minutes. Add the transactional change ledger in `apps/api/Database/Migrations/022_backup_recovery.sql` and shared write/audit paths in `packages/storage/IngestDb.cs` and `apps/api/Contracts/PostgresAuditWriter.cs`, covering any protected mutation that bypasses those paths. Do not infer completeness from a maximum sequence alone when transactions commit out of order. Preserve full graph, working state, metadata and audit/lifecycle authorities rather than relying on the incomplete feature 005 manifest. Use recovery-controlled exact-version copies with durable intents and independently checked purge fences. Persist capture identities and backup-write admissions before external writes; test interrupted capture, missing dependencies, concurrent publication, out-of-order commits and replay in `tests/integration/recovery/run.mjs` (FR-001–FR-005, FR-007; AC-001, AC-003, AC-004; NFR-001, NFR-002, NFR-009, NFR-012, NFR-015).
- [ ] T012 [US1] Implement scheduled capture, overdue/missing/integrity/worker-loss metrics and recovery-controlled owner/operator alerts in `packages/infrastructure/src/recovery/recovery-stack.ts` and `apps/recovery-worker/`; mirror signed safe observations to a dedicated primary SQS result queue and verify/import them through `apps/api/Features/Recovery/RecoveryObservationWorker.cs` without granting primary roles protected recovery reads. Verify timing, missing telemetry, signature/foreign/replay failures, delivery failure and routing using `tests/infrastructure/recovery/recovery-contract.mjs` and `tests/integration/recovery/run.mjs`; record actual notification delivery evidence when authorized (FR-006–FR-008, FR-018; AC-002, AC-011; NFR-007, NFR-010).
- [ ] T013 [US1] Implement persisted completeness/integrity verification across every protected class and independent recovery access checks in `packages/recovery/RecoveryVerifier.cs`; only verified observations advance the recovery watermark. Test corrupted sources, bad signatures/catalogs, graph mismatch, missing owner policy and replayed/future timestamps in `tests/recovery/Program.cs` and `tests/integration/recovery/run.mjs` (FR-005–FR-008, FR-010, FR-016; AC-001–AC-004, AC-006, AC-010; NFR-001, NFR-002, NFR-012, NFR-016).
- [ ] T014 [US1] Add owner-authorized, private/no-store health, exception and operation endpoints in `apps/api/Features/Recovery/RecoveryEndpoints.cs` and `RecoveryHealthService.cs`, wire `apps/api/Program.cs`, and validate configured canonical-library ownership plus safe localizable status codes/UTC values against `contracts/recovery.md`. Test unauthenticated/foreign-owner/configuration-failure responses and sensitive-data exclusion in `tests/integration/recovery/run.mjs` (FR-006, FR-013, FR-016; NFR-003, NFR-004, NFR-005, NFR-013, NFR-014).
- [ ] T015 [US1] Expose accessible textual backup health/exception information in `apps/web/src/` using the established gallery/workspace navigation and localization patterns; add keyboard/screen-reader and fetch-failure tests in `apps/web/tests/` and health-query capacity checks in `tests/integration/recovery/run.mjs`. Record p95 <=5 seconds at the approved workload in `docs/features/011-backup-disaster-recovery/validation.md`; document owner actions in `docs/operations/backup-recovery.md` (FR-006; AC-002; NFR-003, NFR-013, NFR-014).

## Phase 4: US2 — Recover accidental loss (M3)

Independent test: restore deleted/displaced fixtures into a separate review namespace; corrupted or unauthorized candidates cannot be promoted. Promotion requires recent owner authentication and unchanged validated target state. Expired, owner-authorized purge is durable across primary, recovery and export cleanup; final-copy retention remains safe.

- [ ] T004 [US2] Implement isolated restore, validation, explicit promotion, retention/purge and audit workflows in `packages/recovery/Restore/` (FR-009–FR-017; AC-005–AC-010; NFR-004–NFR-006, NFR-009, NFR-010, NFR-012, NFR-016). Complete after T016–T021 and M3 validation/commit.
- [ ] T016 [US2] Implement isolated restore sessions and resumable retrieval in `packages/recovery/Restore/RestoreService.cs`; only independently verified materials enter review and never write live destinations during restore/validation. Test namespace escape, foreign sessions, cold pending retrieval, restart and altered input in `tests/integration/recovery/run.mjs` (FR-009, FR-010, FR-013, FR-016; AC-005, AC-006, AC-010; NFR-004, NFR-005, NFR-009, NFR-015).
- [ ] T017 [US2] Implement complete hash/graph/XMP/metadata/audit/owner-policy validation and explicitly confirmed promotion fencing in `packages/recovery/Restore/RestoreValidation.cs` and `RestorePromotion.cs`, plus API contracts in `apps/api/Features/Recovery/RecoveryEndpoints.cs`. Test recent-auth expiry, target mutation, cross-owner restoration, validation revision changes, rollback and retry in `tests/integration/recovery/run.mjs`; document promotion/rollback limits in `docs/runbooks/library-recovery.md` (FR-009, FR-010, FR-013, FR-016; AC-005, AC-006, AC-010; NFR-004, NFR-010, NFR-016).
- [ ] T018 [US2] Implement 90-day deletion/displaced-history holds, hidden-history preservation, final-verified-copy protection and conditional retirement in `packages/recovery/RetentionService.cs`; serialize against verification/restore/purge changes and recheck retention immediately before deletion. Test exact deadline, active holds, shared content, last-copy race and failed verification in `tests/recovery/Program.cs` and `tests/integration/recovery/run.mjs` (FR-011, FR-012; AC-007; SC-004; NFR-006, NFR-009).
- [ ] T019 [US2] Implement freshly owner-authorized scoped permanent-purge publisher and durable journal in `packages/recovery/PurgeService.cs` and `apps/api/Features/Recovery/RecoveryPurgeService.cs`; require independent recovery-side approval of the exact plan digest before recovery deletion. Recheck source and history holds under established lifecycle locks, publish feature 012 tombstone/invalidation in the same transaction, and fence all normal read/restore/render/write admission through `packages/storage/AssetService.cs`, `apps/api/Features/Rendering/` and their version-control/working/lineage callers before deletion. Test wrong scope/owner/generation, deduplicated references, hidden commits, authorization expiry and concurrent new write admission in `tests/integration/recovery/run.mjs` (FR-011–FR-013, FR-016, FR-017; AC-007, AC-009; NFR-004, NFR-006, NFR-010, NFR-016).
- [ ] T020 [US2] Implement recovery-controlled purge deletion, backup/replication/render/ingest writer fencing, exact-version/multipart reconciliation and persistent receipts in `packages/recovery/PurgeWorker.cs` and affected `apps/api/Features/Rendering/`/`packages/storage/` adapters. Remove all service-controlled target payload copies, including retained derivatives, staging/quarantine and temporary media, while preserving bytes still legitimately shared by another eligible identity. Refuse completion while writer outcomes, provider deletion, protected holds, payload-bearing snapshots or feature 012 cleanup acknowledgments remain unresolved. Preserve safe independently verifiable purge evidence and prove no late write/old-point restore resurrects content in `tests/integration/recovery/run.mjs`; document retry and manual abort thresholds in `docs/runbooks/library-recovery.md` (FR-012, FR-013, FR-017; AC-009; NFR-002, NFR-006, NFR-010, NFR-016).
- [ ] T021 [US2] Exercise the actual 011 publisher with source/XMP/recovery-copy purge and feature 012 invalidation, private download denial and exact export cleanup acknowledgment in `tests/integration/recovery/run.mjs` and `tests/integration/exports/run.mjs`. Record attributable evidence in `docs/features/011-backup-disaster-recovery/validation.md`; update only directly affected 012 dependency evidence without declaring full feature 012 complete (FR-017; AC-009; NFR-009, NFR-016; feature 012 T029).

## Phase 5: US3 — Recover regional loss (M4)

Independent test: deny all primary-account/region credentials and network access, restore a representative complete library plus owner access from recovery materials, record verified RPO <=1 hour and RTO <=24 hours, and repeat quarterly. A local simulation is distinct from the required AWS rehearsal.

- [ ] T005 [US3] Create scheduled recovery-drill workflow, runbooks, fault tests and evidence capture in `.github/workflows/` and `docs/runbooks/` (FR-014, FR-015; AC-003, AC-004, AC-008; NFR-007–NFR-010, NFR-012, NFR-014, NFR-015). Complete after T022–T024 and M4 validation/commit; required external evidence cannot be replaced with synthetic success.
- [ ] T022 [US3] Implement independent recovery runner/bootstrap and private owner-access reconstruction in `apps/recovery-worker/` and `packages/recovery/Restore/RegionalRecovery.cs`; recover pinned application/schema/configuration artifacts from the recovery region with primary access explicitly denied. Add recovery-only server-side verified issuer/audience/subject mapping to the preserved owner ID through `packages/contracts/OwnerAuthorization.cs` and `apps/api/Program.cs`, bound to an independently MFA-approved signed identity receipt; never accept caller-supplied owner overrides or rewrite historical owner rows. Add independent-runtime/fault tests in `tests/integration/recovery/run.mjs` proving original-owner access and foreign identity denial, and include permission/identity validation before promotion (FR-003, FR-007, FR-008, FR-010, FR-014–FR-016; AC-003, AC-004, AC-010; NFR-001, NFR-004, NFR-005, NFR-012, NFR-015).
- [ ] T023 [P] [US3] Author owner-readable regional recovery, accidental loss, cold retrieval and retention/purge runbooks in `docs/runbooks/library-recovery.md` and cost/monitoring guidance in `docs/operations/backup-recovery.md`; identify decision owner, independent prerequisites, numeric abort thresholds, signed evidence, quarterly corrective actions and rollback limits. Add a parameterized 100,000-image storage/transfer/retrieval/runner estimate and record owner-approved tier policy before enabling transitions (FR-004, FR-014, FR-015; NFR-007, NFR-008, NFR-010, NFR-011, NFR-013, NFR-014).
- [ ] T024 [US3] Add protected scheduled quarterly/manual recovery drill workflow in `.github/workflows/recovery-drill.yml` and underlying runner command in `apps/recovery-worker/`; pin actions, scope OIDC only to independent recovery credentials, prevent fork/untrusted-branch access and prohibit unattended promotion/purge. Capture failure/success RPO/RTO, actual alert delivery, policy validation and corrective actions as encrypted recovery evidence. Test workflow semantics locally in `tests/infrastructure/recovery/recovery-contract.mjs`; record a real AWS rehearsal in `docs/features/011-backup-disaster-recovery/validation.md` only after explicit deployment/drill authorization (FR-007, FR-008, FR-013–FR-016, FR-018; AC-003, AC-004, AC-008, AC-010, AC-011; SC-001, SC-002; NFR-001, NFR-007–NFR-010, NFR-012, NFR-015, NFR-016).

## Phase 6: Acceptance and independent review (M5)

- [ ] T025 Integrate real local recovery tests, migration/package validation, web checks and recovery template assertions into `package.json`, `scripts/validate.mjs` and `.github/workflows/ci.yml`; preserve stable check names and use existing pinned tools/actions. Add recovery qualification generation/verification to `scripts/generate-release-evidence.mjs`, `scripts/verify-release-evidence.mjs` and applicable release workflows; require signed attributable <=3-calendar-month evidence matching recovery-relevant digests and measured RPO/RTO, reject fixtures/expired/mismatched/failed qualifications without pretending cloud evidence exists. Run required checks, scan dependency/lockfile changes, logs and public responses for secrets/private content; update `apps/api/README.md`, `apps/web/README.md`, `packages/infrastructure/README.md` and `docs/features/011-backup-disaster-recovery/quickstart.md` with actual commands/results (FR-007, FR-008, FR-015; NFR-004, NFR-005, NFR-009, NFR-012–NFR-016).
- [ ] T026 Record requirement-by-requirement acceptance and non-functional results, approved workload/cost envelope, real independent-account drill, alert delivery and cold retrieval evidence in `docs/features/011-backup-disaster-recovery/validation.md`; mark unavailable release criteria blocked rather than passed. Review classification/runbooks after rehearsal, implement corrective actions, run speckit-converge, and append/complete every actionable convergence task in `tasks.md` without renumbering (FR-001–FR-018; AC-001–AC-011; NFR-001–NFR-016; SC-001–SC-004).
- [ ] T027 Run read-only alternate-model adversarial implementation review against the complete intended range from the recorded base; independently assess findings, remediate valid in-scope blockers, rerun validation/convergence and review until agreement. Preserve all rounds and exact evidence in `docs/features/011-backup-disaster-recovery/reviews/pre-push-review.md`; reviewer inspects final pending record, then commit it unchanged as the dedicated review milestone. No push or merge without separate authorization (Constitution V; FR-001–FR-018; AC-001–AC-011; NFR-001–NFR-016).

## Dependencies and delegation

T006 precedes all code. M1 precedes backup verification and recovery workflows. T010 infrastructure and T011 capture can proceed with separate file ownership after contracts stabilize. T013–T015 depend on capture/observations. Restore depends on a verified point. T019–T021 depend on T018 and the existing 012 in-process consumer; backup deletion must cooperate with T011 write fencing. T022–T024 depend on verified point/restore/identity support. All milestones include relevant tests, affected documentation, task state and a coherent commit; code may not be marked complete because only its model/unit fixture passes.

Delegate infrastructure/capture, API/restore/purge and validation/UI work to separate `gpt-6-luna` agents at `max` reasoning with disjoint ownership. The orchestrator verifies integration and commits milestones. Use a genuinely different model for the final independent adversarial review. Do not substitute local fixtures for actual cross-account independence, KMS/Object Lock, notification delivery, identity bootstrap or achieved AWS RPO/RTO.

## Traceability

| Specification | Implementation and verification tasks |
|---|---|
| FR-001 | T001, T007, T011, T026 |
| FR-002–FR-004 | T002, T009–T011, T022–T024, T026 |
| FR-005 | T001, T009, T011, T013, T026 |
| FR-006 | T003, T012, T014, T015, T026 |
| FR-007–FR-008 | T011–T013, T022, T024, T026 |
| FR-009–FR-010 | T004, T016, T017, T022, T026 |
| FR-011–FR-012 | T008, T018–T020, T026 |
| FR-013 | T008, T014, T016–T020, T024, T026 |
| FR-014–FR-015 | T005, T022–T024, T026 |
| FR-016 | T009, T010, T013, T014, T017, T019, T022, T024, T026 |
| FR-017 | T019–T021, T026 |
| FR-018 | T012, T024, T026 |
| AC-001–AC-002 | T009–T015, T026 |
| AC-003–AC-004 | T011, T013, T022, T024, T026 |
| AC-005–AC-006 | T009, T013, T016, T017, T026 |
| AC-007 | T018, T019, T026 |
| AC-008 | T024, T026 |
| AC-009 | T019–T021, T026 |
| AC-010 | T013, T017, T022, T024, T026 |
| AC-011 | T012, T024, T026 |
| NFR-001–NFR-002 | T009–T013, T020, T022, T024, T026 |
| NFR-003 | T014, T015, T026 |
| NFR-004–NFR-005 | T009, T010, T014, T016, T017, T019, T022, T025, T026 |
| NFR-006 | T008, T018–T020, T026 |
| NFR-007 | T012, T023, T024, T026 |
| NFR-008 | T023, T024, T026 |
| NFR-009 | T007–T025, T026 |
| NFR-010 | T012, T017, T019, T020, T023, T024, T026 |
| NFR-011 | T010, T023, T026 |
| NFR-012 | T011–T013, T022, T024, T026 |
| NFR-013–NFR-014 | T007, T014, T015, T023, T025, T026 |
| NFR-015 | T008–T011, T016, T022, T024–T026 |
| NFR-016 | T007, T008, T013, T017, T019, T020, T024–T027 |
| SC-001–SC-002 | T022, T024, T026 |
| SC-003 | T009, T011, T013, T026 |
| SC-004 | T018, T026 |

## Current evidence and authorization limits

2026-10-05: fresh branch created at `8b2a121` from `origin/master`, with zero divergence from that ref; local `master` is 43 commits behind. Previous 011 specification review is historical and does not certify this proposed design or future implementation. Current work consists of design preparation only. Production/account provisioning, deployment, persistent migration, live recovery, cold transitions and any irreversible purge remain outside authorization. Account/region/admin/alert values and a protected rehearsal environment must be provided before those actions; local model tests alone cannot satisfy binding release criteria.

## Approval record

2026-10-05: the owner approved the design and explicitly instructed implementation according to the earlier instructions. All local numbered tasks, validation, milestone commits, convergence and in-scope review remediation are authorized. Media-only purge with retained historical paths/descriptive metadata is approved. Concrete external account identities, budget/byte-envelope qualification and live AWS actions remain separately gated; no push or merge is authorized.
