# Feature 010 independent pre-push review

- **Date:** 2026-09-27
- **Reviewer model:** `gpt-5.6-sol`, independent of the implementing GPT-6 model; read-only review
- **Remote and proposed destination:** `origin`, `refs/heads/feature/010-lineage-merge-interface-fresh`
- **Base:** `origin/master` at `520e255cf665ac2c5821e80bb17d33fad662785d`
- **Implementation range reviewed:** `520e255..9ede8e2` (six local feature commits)

**Pending push scope:** those six commits plus this review-record commit, if a later push is authorized. No push, merge, deployment, or remote mutation is authorized by this review.

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

## Final reviewer agreement and gate

After inspecting this pending record, the alternate reviewer explicitly agrees that B-001 is a valid unresolved blocking finding, N-001 is nonblocking, and there is no other reported blocking issue in the reviewed range. **Final pre-push gate: BLOCKED.** A later remediation would require a numbered plan task or mapping, validation, a fresh review of the complete revised range, and a new final agreement. No push has occurred.
