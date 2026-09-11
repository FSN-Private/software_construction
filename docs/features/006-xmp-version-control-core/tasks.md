# Tasks: XMP Version-Control Core

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/)
**Status**: Approved — implementation authorized 2026-09-10; feature-004/005 contracts available.

## Phase 1: Foundations

- [x] T001 Create version-control domain, persistence, API, projection, and graph-test layout in `apps/api/Features/VersionControl/` and `tests/version-control/` (FR-020, NFR-008/009). — Domain/contracts, PostgreSQL persistence schema, integrity API, and executable graph-test layout build cleanly.
- [x] T002 [P] Define canonical manifest encoding/digest fixtures and compatible reader/writer versions in `packages/contracts/version-control/` (FR-004/020, NFR-006/011). — Version 1 uses fixed-order UTF-8 JSON and a locked digest fixture; unknown readers fail explicitly.
- [x] T003 [P] Add locked graph/serialization dependencies and audit source/license/advisory evidence in `docs/dependency-inventory.md` (NFR-003/014). — No package added: .NET JSON/SHA-256/collections and the already-reviewed Npgsql package cover the work.
- [x] T004 Create migrations for working changes, staging, commits/entries, refs/tags, reflogs, visibility, projections, divergence, and idempotency in `apps/api/Database/Migrations/` (FR-002–020, NFR-011/016). — Expand-only migration is embedded in the existing checksum-verified migrator.
- [x] T005 Implement feature-005 authoritative-content/identity/hold adapter and feature-004 error/audit/redaction adapter in `apps/api/Features/VersionControl/Adapters/` (FR-001/019/022, NFR-003/004). — Adapter validates owner-scoped authoritative identities and publishes payload-free holds/audits.
- [x] T006 Implement DAG integrity, parent/scope/cycle checks, manifest verification, and ref quarantine in `apps/api/Features/VersionControl/Integrity/` (FR-018, AC-008, NFR-007/015). — Integrity endpoint detects digest/manifest/parent/scope/cycle faults, records a redacted metric, and quarantines affected library refs.

## Phase 2: User Story 1 — Stage and commit coherent XMP changes (P1)

**Independent test**: a 100-image staged set either publishes all entries and one head advance or publishes neither under every injected failure.

- [x] T007 [P] [US1] Implement working-state and stage/unstage APIs with base/current identity review in `apps/api/Features/VersionControl/Working/` (FR-002/003/020, AC-001). — Owner/client/branch-scoped working rows and version-pinned staging preserve base/current identities and reject drift.
- [x] T008 [P] [US1] Implement canonical immutable commit/entry/manifest creation in `apps/api/Features/VersionControl/Commits/` (FR-001/004, AC-001, NFR-006/015). — Canonical manifests, ordered unique entries, parents, and immutable normalized rows share one content digest.
- [x] T009 [US1] Implement serializable expected-head commit/ref/reflog/hold transaction and idempotency outcome in `apps/api/Features/VersionControl/Publishing/` (FR-005/006/015/022, AC-001–003, NFR-001/015). — Publication uses serializable branch locking, CAS, transactional holds/projection/reflog/audit/idempotency, and bounded serialization retry.
- [x] T010 [US1] Add staged-set, single-step failure, retry, and 100-replay property/integration tests in `tests/version-control/commits/` (FR-002–006, AC-001–003, NFR-009/015). — PostgreSQL tests cover six rollback checkpoints, version/base drift, one-hundred replays, ten-way CAS, and complete 100-entry publication.

## Phase 3: User Story 2 — Branch and compare safely (P2)

**Independent test**: divergent branches/tags/compare/revert work; stale heads preserve both sides and default rewrite fails.

- [x] T011 [P] [US2] Implement normalized branch/tag CRUD, protection, exact resolution, and audit in `apps/api/Features/VersionControl/Refs/` (FR-008/009/013/019, AC-005). — Locale-invariant refs support protected branches, immutable tags, versioned mutation, exact resolution, reflog evidence, and redacted audit.
- [x] T012 [P] [US2] Implement compare, divergence, image projection, and whole-library traversal APIs in `apps/api/Features/VersionControl/Query/` (FR-007/010/011/020, AC-004/009, NFR-001/016). — Batched graph loading exposes exact base/ours/theirs state and paged whole-library or image-connected lineage.
- [x] T013 [US2] Implement restore/revert as new reachable commit and dirty-checkout block/preserve/discard contract in `apps/api/Features/VersionControl/Operations/` (FR-012/021, AC-010). — Restore/revert publishes new commits; checkout uses client-scoped CAS and requires block, stash, or confirmed discard.
- [x] T014 [US2] Add concurrent CAS, tag/ref naming, protected-default, compare/divergence, checkout, and projection tests in `tests/version-control/refs/` (FR-007–013/021, AC-002/004/005/009/010). — Unit/model and PostgreSQL scenarios cover naming, immutable tags, protected main, exact divergence, connected projections, checkout CAS/stash, and restore/revert.

## Phase 4: User Story 3 — Clean and recover history presentation (P3)

**Independent test**: a confirmed non-default rewrite moves one ref, preserves hashes, supports day-90 recovery, and hide/unhide changes only presentation.

- [x] T015 [P] [US3] Implement rewrite impact preview, confirmation, ref-only move, immutable reflog, and recovery references in `apps/api/Features/VersionControl/Rewrite/` (FR-014–016/019/022, AC-005/006). — Preview-bound non-default rewrites move one ref, replace its projection, retain displaced ancestry, and create exact 90-day recovery evidence.
- [x] T016 [P] [US3] Implement reversible visibility metadata and show-hidden projections in `apps/api/Features/VersionControl/Visibility/` (FR-017/020/022, AC-007). — Visibility is reversible metadata; hidden commits retain their full reconstructable ancestry without changing hashes or parentage.
- [x] T017 [US3] Implement resolver-supplied two-parent merge publication with expected-target CAS in `apps/api/Features/VersionControl/MergePublishing/` (FR-023, AC-012). — Only the merge contract can add a validated source parent, with base evidence and atomic expected-target publication.
- [x] T018 [US3] Add rewrite/recovery/hold expiry, hide/unhide, reachable-ref, and resolved-merge atomicity tests in `tests/version-control/recovery/` (FR-014–017/022/023, AC-005–007/011/012). — Tests prove exact displacement, day-90 recovery, retained holds, reversible visibility, correct two-parent evidence, and merge rollback.

## Phase 5: Reliability, operations, and readiness

- [x] T019 Rebuild materialized state/image projections solely from immutable graph evidence in `apps/api/Features/VersionControl/Projection/` (FR-007/018, AC-004/008, NFR-008/015). — Branch mutations update projections transactionally; verified immutable graph evidence can replace every projection from scratch.
- [x] T020 Add graph corruption, rebuild, latency, million-entry capacity, locale, authorization, and redaction tests in `tests/version-control/verification/` (FR-018–020, AC-008, NFR-001/003/004/009/010/016). — Verification covers canonical corruption/quarantine, immutable rows, rebuild, 10k traversal, 1m entries/100k assets, owner authorization, locale invariance, and redaction.
- [x] T021 Publish stale-ref, divergence, corruption, rebuild, reflog expiry/recovery, protected-ref, and cost runbooks in `docs/runbooks/` (NFR-007/012/013/014). — Integrity, recovery, and operating/cost procedures define alerts, diagnosis, safe repair, escalation, and evidence.
- [x] T022 Rehearse manifest/schema compatibility migration and rollback/forward-recovery constraints in `docs/features/006-xmp-version-control-core/validation.md` (NFR-011/015). — Transactional rollback, checksum reapply, v1 fixture compatibility, prior-reader safety, and forward recovery are documented and exercised.
- [x] T023 Obtain owner approval for the specification and ADR, then update `spec.md` and `docs/architecture/decisions/0006-content-addressed-xmp-commit-dag.md`. — Approved by the owner on 2026-09-10 with authorization to run the feature through completion.

## Phase 6: Adversarial-review remediation

- [x] T024 Strengthen canonical graph and Feature-005 provenance verification before ref advancement and projection rebuild; add malformed/normalized-entry/object-scope corruption tests (FR-001/018, AC-001/008, SC-005, NFR-003/015). — Canonical manifests now drive reads/rebuilds, normalized evidence and provenance are verified, malformed evidence quarantines safely, and ref advancement invokes verification.
- [x] T025 Make lifecycle-hold release depend on the union of ref ancestry, hidden ancestry, and every unexpired recovery obligation, with overlapping-retention tests (FR-016/022, AC-006/011, NFR-015). — One retention reconciler preserves indefinite and maximum overlapping recovery obligations across descendant refs, visibility, rewrite, deletion, and expiry.
- [x] T026 Give graph reads a consistent snapshot and fence projection rebuilds against concurrent ref movement; add deterministic concurrency coverage (FR-018, AC-004/008, NFR-015). — Manifest graph reads use one query/snapshot and a shared projection fence prevents rebuild from overwriting concurrent publication.
- [x] T027 Persist idempotent ref/restore outcomes in the same transaction as mutations and test the committed-before-response retry window (FR-006, NFR-015). — Ref, rewrite, recovery, checkout, stash, restore, commit, and merge results use the transactional Feature 006 idempotency ledger; replays succeed after names/heads change and restore checks replay before branch state.
- [x] T028 Apply revert deltas without rolling back unrelated changes, require caller head preconditions, and coordinate checkout preservation with working/staging writes (FR-005/012/021, AC-009/010, NFR-015). — Restore/revert publishes prepared entries atomically at the caller's expected head, non-tip revert rejects same-identity conflicts, and the shared client fence serializes working/staging/commit/checkout changes.
- [x] T029 Complete owner-scoped stash/reflog/direct-commit HTTP contracts, support slash-containing ref names, and atomically reject or migrate branch state during rename (FR-003/008/017/020/021, AC-006/007/010). — Preserve returns a retrievable/restorable stash, reflog/direct lookup are public, catch-all routes accept slash refs, deletion returns recovery ID, and rename rejects dependent state under a ref fence.
- [x] T030 Require explicit resolution for every merge conflict and bind resolution evidence into the durable request identity; add omission/replay tests (FR-011/023, AC-012, NFR-015). — Merge derives every two-sided conflict, requires a staged resolution entry for each, and includes required-resolution/base/source/target evidence in the idempotency fingerprint.
- [x] T031 Run PostgreSQL integration and persisted capacity checks in required CI, and implement scheduled integrity heartbeat/failure alarm coverage (NFR-001/007/009/012/016). — Required backend CI runs PostgreSQL/capacity mode; persisted 10k lineage and indexed state p95 checks pass; the 10-minute verifier emits log metrics backed by immediate-failure and 15-minute missing-heartbeat alarms.
- [ ] T032 Record all adversarial-review rounds, dispositions, validation, pending range, and final alternate-model agreement (repository pre-push policy).

## Traceability

FR-001–009: T005/T007–T012; FR-010: T012/T014; FR-011–012: T012–T014; FR-013: T011/T014; FR-014–017: T015–T018; FR-018: T006/T019–T020; FR-019: T004/T011/T015; FR-020: T001/T002/T007/T012/T016/T020; FR-021: T013–T014; FR-022: T005/T009/T015–T018; FR-023: T017–T018. AC-001: T009–T010; AC-002: T009/T014; AC-003: T009–T010; AC-004: T012/T014/T019; AC-005: T011/T014–T015/T018; AC-006–007: T015–T018; AC-008: T006/T019–T020; AC-009–010: T012–T014; AC-011: T005/T015/T018; AC-012: T017–T018. NFR-001: T009/T012/T020; NFR-002: T009/T020; NFR-003: T003/T005/T020; NFR-004: T004/T020; NFR-005: T007/T012; NFR-006: T002/T008/T022; NFR-007: T006/T021; NFR-008: T001/T019; NFR-009: T010/T014/T018/T020; NFR-010: T002/T020; NFR-011: T002/T004/T022; NFR-012: T021; NFR-013: T015/T021; NFR-014: T003/T021; NFR-015: T006/T009/T010/T014/T018–T022; NFR-016: T004/T012/T020. Milestones: graph foundation; commit MVP; refs/compare; recovery/visibility; rebuild readiness.

Remediation traceability: F006-01/02 → T024; F006-03 → T025; F006-04 → T026; F006-05 → T027; F006-06/07 → T028; F006-08/10 → T029; F006-09 → T030; F006-11/12 → T031; final convergence/review record → T032. These tasks extend FR-001/003/005/006/008/011/012/016–018/020–023, AC-001/004/006–012, SC-005, and NFR-001/003/007/009/012/015/016 without renumbering the approved plan.
