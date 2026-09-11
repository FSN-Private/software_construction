# Adversarial Specification Review

**Status**: Round 1 findings fixed; awaiting independent Round 2  
**Reviewer model**: gpt-5.5  
**Review date**: 2026-09-03  
**Base**: `feature/003-darktable-version-control`  
**Scope**: Feature 006 specification; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| Dirty checkout protection was absent. | High | Parent AC-010; 006 FR-021, AC-010 | **Valid and fixed.** Checkout explicitly blocks, preserves, or confirmed-discards dirty state. |
| History retention did not publish concrete storage holds. | High | Parent FR-028; 006 FR-022, AC-011 | **Valid and fixed.** Every history/recovery reference is tied to storage lifecycle holds. |
| No core contract published a resolved merge. | Medium | Parent merge requirements; 006 FR-023, AC-012 | **Valid and fixed.** Resolved merge publication is validated and atomic. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

## Final agreement

The alternate reviewer confirmed dirty checkout protection, lifecycle holds, and atomic resolved-merge publication. No valid unresolved finding remains.

**Final gate result: superseded by the current pending push range.**

## Current pre-push review

**Reviewer**: gpt-5.4 | **Destination**: `origin/feature/006-xmp-version-control-core` | **Range**: `origin/feature/006-xmp-version-control-core..HEAD`

The independent review found stale exact-range evidence and reversed divergence wording. Both are **valid and fixed** by this section and [plan.md](../plan.md). `git diff --check origin/feature/006-xmp-version-control-core..HEAD` passed. Follow-up agreement pending.

## Final agreement for current pending range

The alternate reviewer rechecked the pending push range `origin/feature/006-xmp-version-control-core..HEAD` and confirmed the stale exact-range record and reversed divergence wording are fixed. The superseded earlier gate result is now correctly bounded by a current-range review section, the plan uses stable divergence wording, and the newly added ADR/contract/data-model/plan/tasks artifacts are consistent with the prior resolved findings on checkout safety, lifecycle holds, and resolver-supplied merge publication. No valid unresolved finding remains on the provided evidence.

**Final gate result: PASS**

## Completed implementation review — 2026-09-11

**Status**: Round 1 `BLOCKED`; remediation in progress

**Reviewer model**: `gpt-6-astra` (alternate model)

**Remote**: `https://github.com/FSN-Private/software_construction.git`

**Destination**: `origin/feature/006-xmp-version-control-core` at `9d3df7d44062f8be4f7533cf804d065bb5bb6077`

**Integration base**: `origin/master` at `ba134a93ac08fa0f572fcba2a3442cd10a80a65f`

**Reviewed HEAD**: `c4ce22bf265354e77ed2a82aab08c9307d977a15`

**Complete pending scope**: `9d3df7d44062f8be4f7533cf804d065bb5bb6077..c4ce22bf265354e77ed2a82aab08c9307d977a15` (123 commits, including the approved `origin/master` merge)
**Feature delta**: `origin/master...HEAD` (10 commits at review start; 47 files, 2,496 insertions, 4 deletions)

The reviewer inspected the approved specification, plan, tasks, ADR/contracts/data model/runbooks, complete pending history, Feature 006 delta, source, migration, tests, workflow, and reported validation. The prompt required requirement/task mapping; correctness, concurrency, integrity, authorization/privacy, compatibility, performance, reliability, tests, documentation, CI, unrelated-change, and history review; stable finding IDs with evidence and remediation; and an explicit `PASS` or `BLOCKED` verdict. The review was read-only.

### Round 1 findings and initial dispositions

| ID | Reviewer classification | Evidence and impact | Implementing-agent assessment |
|---|---|---|---|
| F006-01 | Blocking / High | Canonical manifests were not compared with normalized commit metadata/entries before advancement or rebuild; malformed JSON could escape quarantine. FR-018, AC-008, SC-005; T006/T019/T020. | **Valid; remediation T024.** Canonical evidence must be authoritative and malformed input must become a safe integrity failure. |
| F006-02 | Blocking / High | Authoritative XMP validation accepted a global digest without proving owner/asset/edit provenance or stored object availability. FR-001, NFR-003, AC-001/008; T005/T009/T020. | **Valid; remediation T024.** Feature 005 owner-scoped reconstruction/object evidence must be checked. |
| F006-03 | Blocking / High | Expiry checked direct heads and unhide ignored other unexpired recovery obligations, allowing premature hold expiry. FR-016/022, AC-006/011; T015/T016/T018. | **Valid; remediation T025.** Retention release must use the union of all ancestry and recovery roots. |
| F006-04 | Blocking / High | Rebuild read graph/heads before its transaction and graph loading used multiple snapshots, permitting stale projection replacement or torn reads. FR-018, AC-004, NFR-015; T012/T019/T020. | **Valid; remediation T026.** Snapshot consistency and ref fencing are required. |
| F006-05 | Blocking / High | Ref outcomes were recorded by middleware only after service commit; restore recreated transient state before replay detection. FR-006, NFR-015; T009/T011/T013/T015. | **Valid; remediation T027.** Durable replay must be atomic with mutation and restore preparation must not precede replay. |
| F006-06 | Blocking / High | Revert replaced current state with the selected commit's parent snapshot and restore lacked caller expected-head input. FR-005/012, AC-009/010, NFR-015; T013/T014. | **Valid; remediation T028.** Revert must apply only the selected delta atop an explicitly reviewed head. |
| F006-07 | Blocking / High | Checkout's advisory lock was not shared by working mutations, so preserve could delete an acknowledged unsnapshotted update. FR-021, AC-010, NFR-015; T013/T014. | **Valid; remediation T028.** Client mutations must share the checkout fence. |
| F006-08 | Blocking / Medium | HTTP lacked stash retrieval/restore, reflog listing, direct commit lookup, returned recovery IDs, and consistent slash-ref routing. FR-008/017/020/021, AC-006/007/010; T011/T013/T015/T016. | **Valid; remediation T029.** The published core contract must make its promised recovery workflows usable. |
| F006-09 | Blocking / High | Merge accepted a staging set that omitted divergent identities and still recorded resolved divergence. FR-011/023, AC-012; T017/T018. | **Valid; remediation T030.** Every conflict needs an explicit resolver choice bound to the request. |
| F006-10 | Blocking / Medium | Rename moved only the ref/projection, stranding working, staging, and checkout state under the old name. FR-003/008/021; T011/T013/T014. | **Valid; remediation T029.** The smallest safe contract is to reject rename while dependent mutable state exists. |
| F006-11 | Blocking / Medium | Required CI skipped the PostgreSQL suite; capacity evidence was largely in-memory and did not establish persisted reference-scale latency. NFR-001/009/016; T010/T014/T018/T020. | **Valid; remediation T031.** Required CI must exercise PostgreSQL and persisted measurements. |
| F006-12 | Blocking / Medium | Metrics existed only in-process; no scheduled verification heartbeat/exported alarm implemented the 15-minute alert. NFR-007/012; T006/T020/T021. | **Valid; remediation T031.** The scheduled verification and operational alarm need executable coverage. |
| F006-N01 | Non-blocking / Medium | Overlapping staging sets can retain foreign-key references that prevent publication. | **Non-blocking note.** Recoverable by unstaging; may be simplified during adjacent remediation but does not gate the push. |
| F006-N02 | Non-blocking / Low | Full pending range contains inherited trailing whitespace while `origin/master...HEAD` is clean. | **Non-blocking note.** Do not modify unrelated inherited documentation. |

Round 1 validation by the reviewer: the no-build contract/model suite with capacity enabled passed (one-million-entry in-memory reconstruction about 0.28 s; 10,000-node projection about 10 ms). The PostgreSQL rerun could not connect to `127.0.0.1:54329`; this was an environment limitation, not an implementation-test failure. The working tree remained clean during the reviewer pass.

**Round 1 gate result: BLOCKED**

The earlier PASS above reviewed planning artifacts and is superseded for the completed implementation by this active review series.

### F006-02 storage-version addendum

While preparing the Round 2 handoff, the implementing agent rechecked F006-02 against FR-001, FR-018, and AC-008 and found the first remediation still proved only authoritative database provenance, not physical availability of the pinned storage object version. This was **valid and fixed** before final agreement.

Fixes:

- `S3ObjectStore.VerifyStoredAsync` verifies a specific key/version against the expected SHA-256 and byte length.
- `IntegrityService` uses the configured object store during full verification and reports `missing_object` or `altered_object` for unavailable or checksum-mismatched pinned versions.
- DI now supplies the storage-aware `IntegrityService` to the API, scheduled verifier, ref creation, rewrite, recovery, and projection rebuild paths.
- The PostgreSQL/MinIO version-control harness seeds a real versioned XMP object, verifies healthy storage-backed integrity, then proves altered and missing pinned object versions fail integrity and block ref advancement.

Validation after this addendum:

- `dotnet build tests/version-control/Platform.VersionControl.Tests.csproj --no-restore` passed.
- `VERSION_CONTROL_INTEGRATION=1 VERSION_CONTROL_CAPACITY=1 dotnet run --no-build --project tests/version-control/Platform.VersionControl.Tests.csproj` passed, including the new storage-backed object corruption regression.
- `dotnet format tests/version-control/Platform.VersionControl.Tests.csproj --verify-no-changes --no-restore` passed.
- `git diff --check` passed.
- `npm test`, `npm run lint`, `npm run typecheck`, `npm run build`, `npm run test:contract`, `npm run test:ci-contract`, and `npm run test:reliability` passed.
- `npm run validate:security` passed with network access for npm advisory checks.
- `npm run validate:infrastructure` passed with network access for npm advisory checks; the run emitted only the existing CDK deprecation/feature-flag warnings.

The updated pending diff requires a fresh alternate-model review before the pre-push gate can pass.

## Stable implementation review Round 2 — 2026-09-11

**Status**: `BLOCKED`; remediation T033–T036 in progress

**Reviewer model**: `gpt-5.6-sol` (alternate model)

**Destination**: `origin/feature/006-xmp-version-control-core` at `9d3df7d44062f8be4f7533cf804d065bb5bb6077`

**Integration base**: `origin/master` at `ba134a93ac08fa0f572fcba2a3442cd10a80a65f`

**Reviewed HEAD**: `cc66b34bbd1319e2d904dd36dfb2722ad3b03df5`
**Complete pending scope**: `9d3df7d44062f8be4f7533cf804d065bb5bb6077..cc66b34bbd1319e2d904dd36dfb2722ad3b03df5` (128 commits)

The first replacement reviewer attempt was invalidated because that agent mutated the shared worktree despite a read-only instruction and returned no verdict. The implementing agent stopped it, audited the changes, retained only valid F006-02 storage-integrity remediation in `9718b38` and `cc66b34`, revalidated the clean scope, and assigned the stable range above to a different read-only alternate model.

Round 2 directly inspected requirements, plan/tasks/review history, contracts/schema, canonical and physical-object integrity, working/commit/ref/restore/rewrite/merge/query/projection paths, CI/alarms, tests, privacy, and branch history. It classified F006-01/02/03/04/06/07/09 as resolved and retained F006-N01/N02 as non-blocking. It reported these valid blocking findings:

| ID | Severity | Disposition |
|---|---|---|
| F006-05 | High | **Valid; T033.** Ref creation, rewrite/recovery, and merge can evaluate mutable preconditions before a committed retry consults its durable result. |
| F006-08 | Medium | **Valid; T034.** The HTTP staging route is not catch-all, so a slash-containing branch cannot complete the stage/commit flow. |
| F006-10 | High | **Valid; T034.** Checkout does not share the target-ref fence with rename and can write a stale renamed ref. |
| F006-11 | High | **Valid; T035.** The measured 100-entry commit omits production S3 validation and persisted capacity does not yet exercise the specified 1M entries/100k assets. |
| F006-12 | High | **Valid; T036.** Ordinary S3 failures escape the scheduled worker and can terminate verification/host execution. |
| F006-13 | High | **Valid; T036.** Concurrent verifiers can publish quarantine decisions out of snapshot order. |
| F006-N03 | Non-blocking | **Valid; fix with adjacent commit work.** Normalize authored timestamps to UTC before microsecond truncation. |

**Round 2 gate result: BLOCKED**

### Round 2 remediation

- F006-05: added a request fingerprint alongside the fully derived operation fingerprint, moved ref-create/rewrite/recovery/merge replay ahead of mutable preconditions, and covered retries after graph corruption, reflog expiry, and consumed merge staging.
- F006-08/F006-10: changed staging to a catch-all branch route, asserted the registered route, and made checkout share the target-ref fence with rename under a deterministic race.
- F006-11: batched provenance, commit-entry, projection, and hold writes; measured 20 production-equivalent MinIO-backed 100-image commits; and persisted 1,000,000 working rows plus 100,000 assets alongside the 10,000-node lineage.
- F006-12/F006-13: contained storage-provider faults per verification scope, emitted the operational failure signal, and serialized per-library snapshots through quarantine publication with a stale-healthy/new-unhealthy race test.
- F006-N03: canonical authored time now converts to UTC before microsecond truncation.

Validation after remediation:

- PostgreSQL/MinIO integration plus capacity passed: storage-backed 100-image commit p95 380 ms; regular 100-image commit 218 ms; 10,000-node persisted lineage p95 517 ms; 100,000-asset state p95 12.5 ms; persisted 1,000,000 working rows; one-million-entry model 0.69 s.
- `dotnet build` and `dotnet format --verify-no-changes` passed with zero warnings.
- `npm test`, `npm run lint`, `npm run typecheck`, `npm run build`, `npm run test:contract`, `npm run test:ci-contract`, and `npm run test:reliability` passed. The web build retained its known non-module `platform-config.js` warning.
- `npm run validate:security` and `npm run validate:infrastructure` passed, including online advisory checks; infrastructure emitted only the known CDK deprecation and unconfigured-feature-flag notices.
- `git diff --check` passed.

**Current status**: remediation complete; exact-scope Round 3 alternate-model review pending.
