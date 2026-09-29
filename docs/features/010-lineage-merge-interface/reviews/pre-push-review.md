# Feature 010 independent pre-push review

- **Date:** 2026-09-27
- **Reviewer model:** `gpt-5.6-sol`, independent of the implementing GPT-6 model; read-only review
- **Remote and proposed destination:** `origin`, `refs/heads/feature/010-lineage-merge-interface-fresh`
- **Base:** `origin/master` at `520e255cf665ac2c5821e80bb17d33fad662785d`
- **Implementation range reviewed:** `520e255..9ede8e2` (six local feature commits)

**Round 1 pending push scope:** those six commits plus this review-record commit, if a later push is authorized. The current proposed scope is recorded in the final round below. No push, merge, deployment, or remote mutation is authorized by this review.

The old remote `refs/heads/feature/010-lineage-merge-interface` points to `49cfe875` and has separate history (five commits unique to the old ref, 159 unique to the new local branch). A read-only `git ls-remote --heads` on 2026-09-27 confirmed the proposed fresh ref did not exist. A later push must recheck the ref and use a normal, non-force update; the stale ref is to remain untouched.

## Earlier specification-only review

The 2026-09-03 `gpt-5.5` specification review was against `feature/003-darktable-version-control`, before the feature implementation. It found and resolved omissions for tag actions and audit (FR-017/018, AC-010), reachable restore/revert (FR-019, AC-011), complete merge-result review (FR-020, AC-012), and protected branch switching (FR-021, AC-013). Its `PASS` applied to the specification only. The implementation review below is the current push gate.

## Round 1: complete implementation review

The reviewer was asked to inspect the complete `origin/master...HEAD` diff; approved `spec.md`, `plan.md`, `tasks.md`, contracts and validation; relevant source, tests, migrations, configuration and generated artifacts; and repository policy. The prompt required significant blocking and nonblocking findings with severity, evidence, requirement/task IDs, impact and remedy, plus explicit clear areas. It covered requirements and acceptance criteria, correctness, failure and concurrency paths, authorization, privacy, integrity, performance, accessibility, operations, migration, tests, unrelated changes, and destination safety. The reviewer made no edits.

### B-001 — unbounded history traversal

- **Reviewer classification:** Blocking, high; NFR-014, with NFR-001 and SC-003 availability risk; T002, T006, T015.
- **Evidence:** `apps/api/Features/Lineage/LineageService.cs:26-44` recursively constructs `reachable` from the selected head through all ancestors, then `matched` and `connecting`. `OFFSET/LIMIT` applies only to the final result. A first-page request for 100 nodes can therefore process the full branch ancestry. `validation.md` records 500-version service and browser fixtures, and says the optional million-entry run was not repeated.
- **Impact:** The declared one-million-revision reference library can make first-page history consume database work proportional to the complete history, with a material latency and availability risk. The existing 500-version timings do not establish reference-scale behavior.
- **Suggested remedy:** Bound first-page server work with an indexed asset-to-commit projection or another proven pagination strategy; verify the query plan, bounded request/payload behavior and latency against the one-million-revision reference fixture.
- **Implementer assessment and disposition:** **Valid, unresolved.** I independently inspected the recursive query: its traversal has no page bound, and the final `LIMIT` cannot bound the recursive work. The requirement expressly calls for bounded initial loading at reference scale, and no test demonstrates that this query remains viable there. This is a material availability and acceptance-evidence gap. This request is for review only, so no implementation or plan status has been changed. The current pre-push gate remains blocked.

### N-001 — gallery staging sets remain abandoned after stash restore

- **Reviewer classification:** Nonblocking, medium; FR-021, AC-013, T013.
- **Evidence:** `apps/api/Features/VersionControl/Operations/OperationService.cs:90-102` snapshots open staging sets for both the lineage and gallery web clients. At lines 128-142, restore reopens sets only where `s.client_id=$4`, and `$4` is the lineage client, while staged entries from both clients are reinserted. The integration fixture at `tests/lineage/LineageIntegrationChecks.cs:217-239` restores an unstaged gallery working row.
- **Impact:** Gallery working content is restored, but a previously staged gallery set can remain `abandoned`, requiring the user to stage it again before committing.
- **Suggested remedy:** Reopen the snapshot sets for all allowed, owner-scoped snapshot clients and add a staged-gallery restore fixture.
- **Implementer assessment and disposition:** **Non-blocking note.** The SQL matches the reviewer's account. Content remains in working changes, so it can be restaged; the observed defect does not silently discard an edit or independently meet the review skill's blocking threshold. Record for follow-up; no code change in this review-only request.

### Clear areas and validation basis

The reviewer found no other blocking issue in owner authorization or signed recent authentication, merge head locking and dirty-state checks, proposal/review digest binding and idempotency, transaction atomicity, generated XMP authority and provenance, conservative opaque-field resolution, preview failure separation, protected rewrite/recovery/visibility/tag audit, mutation pause, privacy-safe telemetry, additive migrations and rollback, browser confirmation/context, branch history, or destination-ref safety. `git diff --check` passed. The prior local validation record reports API build, disposable PostgreSQL/S3 lineage and version-control integration, web checks, three-browser behavior and capacity fixtures, and infrastructure checks as passing, with manual screen-reader and deployed pre-production exercises listed as release gates. No validation was rerun or represented as newly passing in this review round.

## Round 1 reviewer agreement and gate

After inspecting this pending record, the alternate reviewer explicitly agrees that B-001 is a valid unresolved blocking finding, N-001 is nonblocking, and there is no other reported blocking issue in the reviewed range. **Final pre-push gate: BLOCKED.** A later remediation would require a numbered plan task or mapping, validation, a fresh review of the complete revised range, and a new final agreement. No push has occurred.

## Round 2: review after first remediation milestone

- **Reviewer:** `gpt-5.6-sol`, read-only, 2026-09-27.
- **Full reviewed range:** `origin/master...b064f26`, eight local feature commits. The proposed fresh destination and base above were unchanged; no remote mutation occurred.
- **Scope:** Complete feature diff and approved artifacts, both earlier findings, new migration 020, history and stash code, tests, reference-scale and browser validation, migration/rollback and destination safety.

The reviewer explicitly assessed **B-001 as valid and fixed**: the indexed asset-entry and branch-reachability intersection replaced whole-branch history traversal, and the persisted one-million-entry/100,000-asset fixture measured first-page image p95 **153 ms** and branch backfill **0.6 s**. The reviewer assessed **N-001 as valid and fixed**: stash restore now reopens the allowed gallery staging set and restores its selected entry under owner/library checks, verified in disposable integration. The implementer agrees with both assessments; T018 and T019 and the first remediation milestone `b064f26` cover these fixes.

### B-002 — hidden ancestry lost across omitted connector commits

- **Reviewer classification:** Blocking, high; FR-001/013, AC-001/016, T002/T018, with NFR-015 presentation evidence.
- **Evidence:** The indexed history query at `LineageService.cs:27-49` returns only commits changing the selected image. The collapsed marker query at lines 61-67 checked only their direct parents. A visible image commit followed by an unrelated library-wide connector, then a hidden image commit and a prior visible image commit, therefore had no `HiddenParentIds` marker. Unit and browser fixtures covered only a direct hidden parent.
- **Impact:** Normal image history could omit a hidden version between visible versions without a collapsed-ancestry indication. Exact raw parent IDs alone did not identify the hidden image version, contrary to AC-016.
- **Suggested remedy:** Preserve bounded page selection while deriving collapsed ancestry across omitted connectors; cover linear and merge paths in integration/browser checks.
- **Implementer assessment and disposition:** **Valid and fixed in `53f3f57` (T021), awaiting independent Round 3 assessment.** The page still loads at most 101 selected manifests. For normal history, the service walks parent paths from only those selected commits through omitted connectors, stops at the next image-changing commit on each path, and records hidden identities on those segments for the visible node. Show-hidden returns the exact hidden image commit and its canonical parent IDs. A partial visibility index enables a no-hidden fast path. Disposable PostgreSQL/S3 integration passes linear and merge connector paths; the browser mock includes an omitted connector and checks marker/reveal. The final persisted reference-scale fixture passes with one million entries/100,000 assets: ordinary image-history p95 **186 ms**, hidden-ancestry p95 **186 ms**, reachability backfill **0.6 s**. Focused Playwright passes **9/9** across Chrome, Edge, Firefox with 500-version p95 **447/521/614 ms**; API/worker builds, web tests/accessibility/build, Prettier, and whitespace checks pass.

The reviewer found no other blocking issue in Round 2. Its preliminary gate was **BLOCKED** solely for B-002. Round 3 is reviewing the complete `origin/master...53f3f57` range and these dispositions. The final gate remains **BLOCKED pending the reviewer's explicit agreement and inspection of this updated record**. No push has occurred.

## Round 3: review after hidden-connector remediation

- **Reviewer:** `gpt-5.6-sol`, read-only, 2026-09-27.
- **Full reviewed range:** `origin/master...53f3f57`, nine local feature commits. Base and proposed fresh destination unchanged; no remote mutation occurred.
- **Scope:** Complete revised implementation, all earlier dispositions, migration and operations, browser/integration/reference-scale evidence, and destination safety.

The reviewer explicitly assessed **N-001 as valid and fixed** and **B-002 as functionally fixed** for linear and merge connector paths. It agreed that **B-001's indexed page selection remained fixed for the no-hidden path**, but found that the T021 hidden-ancestor traversal reintroduced unbounded work when hidden visibility existed. It reported no other blocking issue in the full range and a preliminary **BLOCKED** gate for B-003.

### B-003 — sparse image history can reintroduce full connector traversal

- **Reviewer classification:** Blocking, high; NFR-001/014, SC-003, T018/T020/T021 and regression of B-001's bounded-work objective.
- **Evidence:** `LineageService.cs:63-91` enabled a recursive parent walk whenever any hidden row existed for the owner/library. From each of up to 101 page commits it could follow every parent path until another commit changed the selected asset. The reference-scale fixture changed the 100 measured assets in every one of 10,000 commits and hid an immediate parent, so its 186 ms p95 did not cover sparse images or unrelated hides.
- **Impact:** A single hidden commit in the library could cause a sparse image's first page to traverse a long unrelated connector span, recreating material latency and database-cost risk.
- **Suggested remedy:** Materialize indexed image-lineage edges and collapsed metadata, or use another structure whose read work does not depend on connector length; validate a long sparse connector span with relevant and unrelated hidden commits.
- **Implementer assessment and disposition:** **Valid and fixed in `1f2f0b6` (T022), awaiting independent Round 4 assessment.** The per-node recursive connector walk was removed. Normal history now queries the indexed image-entry set for a hidden commit that is also in the selected branch's derived reachability set, returning one `hasHiddenHistory` boolean for the page. The browser shows a branch-and-image-scoped collapsed-history notice; canonical parent IDs and direct hidden-parent IDs remain on nodes, while show-hidden returns exact hidden image commits and parents. This does not assign a hidden image commit to an unproven node edge. The persisted fixture now contains 1,000,003 entries, 100,000 assets, 10,000 commits, and a sparse image with changes only at commits 0, 5000, and 9999. With an unrelated hidden connector, image-scoped `hasHiddenHistory` stayed false and first-page p95 was **17 ms**; with the middle image version hidden, the indicator was true, first-page p95 was **15 ms**, and show-hidden recovered its exact identity and canonical connector parent. Dense image-history p95 was **210 ms**, hidden dense-image p95 **174 ms**, backfill **0.6 s**. Disposable PostgreSQL/S3 lineage integration passed with 500-version history/comparison p95 **36/124 ms**. Focused Playwright passed **9/9** in Chrome, Edge, Firefox with 500-version p95 **485/551/627 ms**; API/worker builds, web tests/accessibility/build/typecheck, Prettier, and whitespace checks passed.

Round 4 is independently assessing the complete `origin/master...1f2f0b6` range and the B-001/N-001/B-002/B-003 dispositions. The final gate remains **BLOCKED pending explicit reviewer agreement and inspection of the complete pending review record**. No push has occurred.

## Round 4: complete-range reassessment after sparse-history remediation

- **Reviewer:** `gpt-5.6-sol`, independent read-only review, 2026-09-27.
- **Full reviewed implementation range:** `origin/master...1f2f0b6`, ten local feature commits from base `520e255cf665ac2c5821e80bb17d33fad662785d`.
- **Proposed remote and destination:** `origin`, `refs/heads/feature/010-lineage-merge-interface-fresh`. The old stale feature ref remains untouched. A push would require a fresh destination check and separate explicit user authorization.
- **Pending push scope if later authorized:** those ten local feature commits plus this final review/task/validation record commit, by normal non-force update to the fresh destination.

The reviewer explicitly assessed **B-001 as valid and fixed**: history selects at most 101 manifests using indexed asset entries and transactionally maintained branch reachability. It assessed **N-001 as valid and fixed**: restored gallery staging entries remain open under owner/library/allowed-client constraints. It assessed **B-002 as valid and fixed**: the image-and-branch-scoped page notice visibly indicates collapsed hidden history across omitted connector and merge paths; show-hidden returns exact hidden image commits and canonical parents, while direct hidden-parent IDs remain on nodes where applicable. It assessed **B-003 as valid and fixed**: the recursive connector query was removed; the sparse persisted fixture proves first-page work and p95 remain bounded across a 10,000-commit span with relevant and unrelated hidden commits. The reviewer reported no new blocking or material nonblocking finding.

The reviewer explicitly found no blocking issue in owner authorization and recent authentication, privacy/logging, merge digest binding, head locking, stale working checks, idempotency and atomic publication, generated XMP authority and conservative conflict resolution, preview failure handling, ref/rewrite/recovery/tag/visibility audit, mutation pause, migration 018–020/backfill/rebuild/rollback, gallery context, accessibility/browser behavior, validation sufficiency, commit scope, or fresh destination safety. The reviewer checked the complete revised diff and `git diff --check`; no edits, commits, pushes, or external mutations were made by it.

**Round 4 preliminary gate: PASS**, with no valid unresolved blocking finding. This pending record and the final T020/validation updates require the reviewer's separate inspection and explicit final agreement before the final gate is recorded. No push or merge has occurred.

## Final record inspection and gate

The alternate reviewer inspected this pending record and the T020/validation updates. It found the Rounds 1–4 evidence and dispositions accurate, requested the wording correction above from “implementation commits” to “local feature commits” because the range includes an earlier review-record commit, and stated: **“no valid unresolved blocking finding remains and the final pre-push gate should be PASS.”** The correction and this agreement are included in the exact final record submitted for one last read-only reviewer inspection before commit.

**Final pre-push gate: PASS.** No blocking finding remains unresolved. This gate does not authorize a push, merge, deployment, or other remote mutation. No push or merge has occurred.

## Round 5: PR #29 formatting CI follow-up (2026-09-29)

The user authorized push, PR creation, CI follow-up, and merge after passing checks. The initial reviewed head `855cedd` was pushed normally to `origin/feature/010-lineage-merge-interface-fresh`, and [PR #29](https://github.com/FSN-Private/software_construction/pull/29) was opened against `master`. The initial [CI run 36563967248](https://github.com/FSN-Private/software_construction/actions/runs/36563967248) rejected two whitespace errors in the XMP writer initializer; T023 and `22ff656` format its three properties onto separate lines, retaining identical values and behavior.

- **Alternate reviewer:** `gpt-5.6-sol`, read-only.
- **Base/full revised range:** `origin/master` at `520e255`, through `22ff656`.
- **Remote/destination:** `origin`, `refs/heads/feature/010-lineage-merge-interface-fresh`, currently at `855cedd`.
- **Pending push scope:** `855cedd..22ff656` plus this final review/validation record commit, by normal non-force update. The earlier eleven published commits remain included in PR #29.
- **Validation:** `dotnet format apps/api/Platform.Api.csproj --verify-no-changes --no-restore` and `git diff --check` pass. No runtime change was made, so further behavioral tests were not necessary for this correction; remote CI will rerun before merge.

The reviewer inspected the complete revised range and reported **PASS**: the formatting retains `Encoding`, `Indent`, and `CloseOutput` values without runtime, serialization, compatibility, security, or integrity change. Earlier B-001/B-002/B-003/N-001 dispositions remain valid and fixed. It found no new blocking correctness, concurrency, authorization, privacy, migration, performance, accessibility, test, dependency, or scope issue.

**N-002 — nonblocking, low, documentation accuracy:** the validation record still said no push was requested after recording PR #29. The implementer independently confirmed the stale sentence and corrected it to record the authorized initial push/PR and the follow-up review requirement. This correction is part of the exact pending final record scope submitted for the reviewer's final inspection before commit.

**Current final pre-push gate: PASS**, with no unresolved blocking finding. After inspecting this exact pending Round 5 record and the N-002 validation correction, the alternate reviewer explicitly agreed that no valid unresolved blocking finding remains, the final pre-push gate is PASS, and these files may be committed unchanged. This gate does not authorize deployment. The initial push and PR were performed under the user's separate authorization; merge remains conditional on passing remote CI.

## Round 6: rewrite/recovery browser CI follow-up (2026-09-29)

- **Alternate reviewer:** `gpt-5.6-sol`, independent read-only review.
- **Base/full revised range:** `origin/master` at `520e255`, through `745cf15`.
- **Remote/destination:** `origin`, `refs/heads/feature/010-lineage-merge-interface-fresh`, still at `855cedd`.
- **Exact pending push scope:** formatter commit `22ff656`, approved Round 5 record `87a77e6`, test synchronization/task/validation commit `745cf15`, plus this final Round 6 review-record commit. Push normally without force.

The initial PR CI web job passed 41 cases but timed out in Chrome's rewrite/recovery journey when recovery controls were detached and disabled after the test entered fields. The test observed the rewrite request before the successful action completed its history refresh/remount. T024 registers an endpoint-scoped history response wait before confirming rewrite and awaits that response before entering recovery details. Product code and the existing rewrite/recovery request assertions are unchanged. The exact journey passed **15/15** across Chrome/Edge/Firefox, five repetitions each; changed-file Prettier and `git diff --check` passed.

The reviewer inspected the complete revised range and explicitly reported **PASS** with no valid unresolved blocking finding. It agreed that the response wait matches the successful action's refresh sequence, preserves assertions, avoids arbitrary delays, and changes no product behavior. It accepted the repeated browser evidence as proportionate. B-001/B-002/B-003/N-001/N-002 remain fixed and unaffected. It found no new blocking or material nonblocking correctness, test validity, authorization, privacy, integrity, concurrency, migration, performance, accessibility, dependency, documentation, or scope issue.

**Round 6 final gate: PASS.** After inspecting this exact pending Round 6 record, the alternate reviewer explicitly agreed that no valid unresolved blocking finding remains and the Round 6 pre-push gate is PASS. It confirmed the four-commit pending scope, evidence, unchanged assertions, and conditional merge accurately, with no correction needed. This final text is submitted for its last inspection before unchanged commit. The user has authorized the follow-up push; merge is authorized only after all remote CI passes. No deployment is authorized.
