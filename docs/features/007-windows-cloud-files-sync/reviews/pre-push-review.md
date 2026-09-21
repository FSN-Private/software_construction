# Feature 007 Adversarial Review Record

**Status**: Post-merge CI repair code gate passed; repair-PR validation pending
**Reviewer model**: gpt-5.5  
**Review date**: 2026-09-03  
**Base**: `feature/003-darktable-version-control`  
**Scope**: Feature 007 specification; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| Web-originated changes lacked a safe Windows application contract. | High | Parent FR-025/AC-009; 007 FR-020, AC-010 | **Valid and fixed.** Remote working changes now preserve dirty/conflict and unrelated-XMP protections. |
| Local delete and restore semantics were incomplete. | Medium | Parent AC-006; 007 FR-019, AC-009 | **Valid and fixed.** Deletion, dehydration, and restore are distinguished and offline intent is replayed safely. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

## Final agreement

The alternate reviewer confirmed offline delete/restore semantics and branch-safe web-originated XMP synchronization. No valid unresolved finding remains.

**Final gate result: PASS**

## Implementation pre-push review — 2026-09-20

**Reviewer model**: gpt-5.5
**Remote**: `origin` (`https://github.com/FSN-Private/software_construction`)
**Base**: `origin/master` at `0d8d510`
**Reviewed source range**: `0d8d510..d3e1332`
**Local source ref**: `feature/007-windows-cloud-files-sync` at `d3e1332`
**Pending push scope**: eight Feature 007 commits, with no merge or deployment
**Destination status**: unresolved. The same-named remote ref is `da5fc87` and diverges by 5 remote-only/132 local-only commits; a normal push cannot update it. No force push or history rewrite is authorized. A fresh destination ref or explicit force-with-lease authority is required before final agreement.

### Round 1 scope

The alternate reviewer received the approved Feature 007 artifacts, repository instructions, complete intended diff, source/tests/contracts/migration/CI evidence, validation results, and runtime constraints. It was asked to trace every requirement, acceptance criterion, NFR, and numbered task; inspect failure, concurrency, integrity, security, compatibility, performance, migration, rollback, tests, documentation, and commit history; classify findings by the repository blocking threshold; and explicitly state areas with no blocking issue.

### Round 1 findings and dispositions

| ID | Classification | Severity | Evidence, impact, and affected IDs | Disposition |
|---|---|---|---|---|
| B-001 | Blocking | High | `WindowsSyncService.NamespaceAsync` left an active Npgsql reader while issuing the device cursor `UPDATE`, risking `command already in progress` and breaking FR-003, AC-001, T006/T008, NFR-001/016. | **Valid and fixed (T021).** The reader is scoped and disposed before cursor mutation. Terminal head cursors prevent idle 100,000-item re-enumeration. A PostgreSQL integration check now exercises enumeration, cursor update, and idle reuse in the existing isolated CI job. |
| B-002 | Blocking | High | `origin/feature/007-windows-cloud-files-sync` is an older merged history. Normal push rejects; force-with-lease would rewrite a remote ref without authority. | **Needs user decision.** No remote mutation has occurred. Choose a fresh destination ref or explicitly authorize force-with-lease after the code gate passes. |
| B-003 | Blocking | High | Checkout enumeration only added/updated placeholders, leaving stale prior-branch entries and same-path collisions; affected FR-002/003/013/014, AC-005/006, SC-004, T013/T016. | **Valid and fixed (T022).** A complete snapshot tracks seen identities, removes only clean absent items, preserves dirty/pending bytes as conflicts, and distinguishes stale-branch collisions from selected-branch Windows collisions. Tests cover clean replacement, dirty remote-missing preservation, and deterministic collision suffixing. |
| B-004 | Blocking | High | Lifecycle replay used local sequence values, always targeted asset routes, and did not consume authoritative lifecycle response versions; affected FR-010/012/019, AC-007/009, T011/T012. | **Valid and fixed (T023).** Namespace/journal contracts carry asset/edit lifecycle versions; edit delete/restore uses edit routes; offline operations chain expected versions and deduplicate duplicate notifications; replay stores the returned version. Contract and offline tests cover versions 4→5→6. |

The reviewer reported no other blocking issue in dependency and migration changes, pinned CI/trusted-environment isolation, token custody and revocation, hash validation, diagnostic redaction, or signing/runbook documentation.

### Additional remediation discovered during assessment

T024 fixes four related contract/reliability defects found while validating Round 1: asynchronous request disposal before completion, error-body parsing after response disposal, checkout/device response-shape drift, and checkout not re-registering the Windows Sync device on the selected branch. Broader regression validation also found that mapped Windows Sync endpoints prevented unconfigured API startup; endpoint resolution now preserves liveness and returns the existing safe unavailable path only when invoked.

### Validation after remediation

- `dotnet restore tests/windows-sync/Platform.WindowsSync.Tests.csproj --locked-mode` — passed.
- `dotnet build tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-restore --nologo` — passed with zero warnings/errors.
- `dotnet run --project tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-build` — passed; 1,000-placeholder p95 0.236 s, 100,000-placeholder enumeration 18.484 s, zero content bytes, one-request idle poll.
- `dotnet build apps/api/Platform.Api.csproj --no-restore --nologo` and API formatting verification — passed.
- API foundation and version-control non-database executables, release-manifest check, API correlation, and CI contract validation — passed.
- `dotnet build tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj --no-restore --nologo` — passed, including compilation of the new PostgreSQL namespace regression. Docker was unavailable locally, so execution remains in the existing isolated CI job.
- `dotnet list tests/windows-sync/Platform.WindowsSync.Tests.csproj package --vulnerable --include-transitive` — no vulnerable packages.
- `git diff --check` — passed.

### Round 2

**Reviewer model**: gpt-5.5
**Reviewed source range**: `0d8d510..d3e1332`

The alternate reviewer accepted the implemented dispositions for B-001, B-003, and B-004. B-002 remains an authorization boundary, not an implementation defect: the existing remote ref cannot be updated normally and has not been mutated.

| ID | Classification | Severity | Evidence, impact, and affected IDs | Disposition |
|---|---|---|---|---|
| B-005 | Blocking | High | Namespace enumeration used global Feature 005 lifecycle endpoints for replay but used stale branch-projected path/state for enumeration. A successful move, delete, or restore could remain invisible to another device and reconciliation could restore an old local path. Affected FR-012/019, AC-005/009, T011/T013/T023. | **Valid and fixed (T025) in `08ec9b6`.** Namespace composition now uses branch-projected content with authoritative asset/edit state and active asset paths. Migration 008 adds owner-scoped lifecycle revisions to the opaque cursor snapshot. The PostgreSQL scenario applies all migrations and verifies two-device move, edit delete/restore, asset delete/restore, cursor expiry, deterministic sidecar paths, and idle terminal polling. |

T026 was found during local resilience validation and fixed in the same milestone: lifecycle replay now preserves conflict metadata when an expected delete leaves no source file to copy.

### Round 3

**Reviewer model**: gpt-5.5
**Reviewed source range**: `0d8d510..08ec9b6`

The alternate reviewer accepted B-001 through B-005, including migration 008, lifecycle cursor invalidation, all-migrations test setup, T026, and the rollback/runbook controls. B-002 remains destination authority only.

| ID | Classification | Severity | Evidence, impact, and affected IDs | Disposition |
|---|---|---|---|---|
| B-006 | Blocking | High | Every renamed placeholder was queued as an asset move. Renaming an XMP sidecar could therefore move the asset to an `.xmp` path and derive a second `.xmp` suffix. Affected FR-012/019, AC-005/007, T011/T012. | **Valid and fixed (T027) in `02e012c`.** A coordinated sidecar rename is translated into exactly one asset move only when its companion original has moved to the derived original destination. An edit-only rename becomes an explicit local conflict and never mutates the asset. Offline replay tests cover both cases. |

### Round 4

**Reviewer model**: gpt-5.5
**Reviewed source range**: `0d8d510..02e012c`

The alternate reviewer accepted B-001 through B-005 and the B-006 sidecar-first path. It found that the original-first watcher order changed the original journal path before the sidecar event, causing the later event to be falsely marked as an unsupported sidecar rename.

| ID | Classification | Severity | Evidence, impact, and affected IDs | Disposition |
|---|---|---|---|---|
| B-006-R4 | Blocking | High | Coordinated original/sidecar rename behavior depended on independent `FileSystemWatcher` event order. Original-first handling could report a false sidecar conflict. Affected FR-012/019, AC-005/007, T011/T012. | **Valid and fixed (T027) in `c2d366b`.** A sidecar event matching the already-updated canonical asset path updates only its local journal path; it does not create another lifecycle mutation or conflict. Offline tests cover sidecar-first and original-first order, each with exactly one asset move. |

### Round 5

**Reviewer model**: gpt-5.5
**Reviewed source range**: `0d8d510..c2d366b`

The alternate reviewer found no unresolved material code blocker. It accepted B-001, B-003, B-004, B-005, B-006, and B-006-R4 as fixed, including the reader/cursor scope, complete reconciliation, lifecycle version/routing, migration 008 cursor invalidation, and both original-first and sidecar-first coordinated rename paths. It also accepted migration embedding/rehearsal, rollback controls, security/auth posture, dependency posture, CI/test plausibility, and artifact traceability.

**Code gate: PASS.** B-002 remains a separate authority boundary: the same-named remote ref still diverges and cannot be updated by normal push.

### Implementation final agreement

**PASS for code** at `c2d366b`.

**Implementation final gate result: BLOCKED** — code is approved, but B-002 requires a destination decision before any remote mutation.

## CI remediation review — 2026-09-21

PR #25 backend CI failed after the first push, while Windows, web, security, and infrastructure passed. The failure was deterministic: `tests/version-control/commits/CommitChecks.cs` expected three migration ledger records although `DatabaseMigrator` now embeds five migrations, including 007 and 008. Its rollback rehearsal also did not execute migration 008.

**Candidate remediation**: `a645e3a` (`test(007): align migration rehearsal`)

T028 adds migration 008 to the transactional rollback input and expects five checksum-pinned ledger records after two migrator passes. The targeted build and version-control non-database executable passed locally. Docker is unavailable on the workstation, so the PostgreSQL/MinIO replay remains assigned to PR CI.

### Round 6

**Reviewer model**: gpt-5.5
**Reviewed source range**: `c2d366b..a645e3a`

The alternate reviewer found no material blocker. It confirmed that the transactional rollback rehearsal includes migration 008 and that the repeated migration ledger expects the five migrations embedded by `DatabaseMigrator`. Migration ordering, rollback/replay coverage, checksum behavior, documentation/task traceability, and prior B-001 through B-006 dispositions remain sound.

**CI remediation code gate: PASS.**

The prior B-002 destination authority is resolved: the user authorized and the branch was pushed to fresh ref `feature/007-windows-cloud-files-sync-implementation`; no existing remote history was overwritten. PR #25 remains the final PostgreSQL/MinIO validation gate.

### CI rerun follow-up

The first remediation rerun passed the five-migration Version Control replay and capacity checks, then failed in the Feature 007 PostgreSQL convergence scenario. `WindowsSyncChecks` selected the first active asset, which could be the retained fixture created earlier by lifecycle coverage; its intended move correctly returned `retention_hold`.

**Candidate remediation**: `49153c4` (`test(007): isolate lifecycle convergence fixture`)

T029 excludes an asset with an active lifecycle hold from the convergence fixture query. The API foundation build and non-database executable pass locally; Docker remains unavailable locally.

### Round 7

**Reviewer model**: gpt-5.5
**Reviewed source range**: `ded95df..49153c4`

The reviewer found B-007: excluding held fixtures could leave no active, unheld asset/edit pair because the shared ingest scenario mutates or deletes every suitable pair. The predicate itself was owner-safe but insufficient to guarantee the convergence test precondition.

**Candidate remediation**: `3c6f901` (`test(007): isolate convergence fixture state`)

T029 now creates a unique active asset/edit/path fixture using already-authoritative original and XMP content. Its lifecycle state is independent of earlier shared fixtures and it supplies the same dedicated pair to the version-control namespace/convergence assertions.

### Round 8

**Reviewer model**: gpt-5.5
**Reviewed source range**: `49153c4..3c6f901`

The alternate reviewer accepted B-007 as fixed. It confirmed the fresh asset/edit/path fixture satisfies schema defaults and uniqueness, is created before cursor registration, invokes migration 008 revisions for its lifecycle mutations, and removes shared-lifecycle-state coupling while retaining authoritative immutable content. No material blocker was found in schema, lifecycle, cursor, test isolation, or resource integrity.

**Dedicated CI fixture code gate: PASS.**

## Post-merge CI repair review — 2026-09-21

After Feature 007 merged, `master` CI run `35556452195` failed only in the backend ingest suite. PostgreSQL correctly rejected the Windows Sync test fixture with `23514: normalized page index must match canonical commit evidence`. The fixture serialized a `DateTimeOffset.UtcNow` value with 100-nanosecond precision into the manifest, while PostgreSQL stores the normalized commit timestamp at microsecond precision.

**Candidate remediation**: `00d1f83` (`test(007): normalize sync fixture timestamp`)

T029's dedicated fixture now normalizes its timestamp to the same 10-tick precision already used by `CommitService`, before using it for the canonical manifest and normalized database rows. The API foundation build completed with zero warnings; its non-database executable and `git diff --check` passed. Docker is unavailable locally, so the exact PostgreSQL trigger path remains the repair PR's CI gate.

### Round 9

**Reviewer model**: gpt-5.5

**Reviewed source range**: `origin/master..00d1f83`, plus this review-record update in the pending push

**Pending destination**: `fix/007-windows-sync-timestamp-normalization`

The alternate reviewer found no material blocker. It confirmed that migration 006's deferred normalization trigger compares canonical manifest `receivedAt` with `vc_commits.received_at`, the codec retains seven fractional timestamp digits, and the test now uses one microsecond-aligned value for both representations. Production commit creation already applies this exact normalization. No regression was found in migration compatibility, codec behavior, production parity, or the CI unblock path.

**Post-merge CI repair gate: PASS.**
