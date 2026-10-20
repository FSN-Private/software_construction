# Native renderer diagnosis pre-push review

Reviewer: `gpt-6-astra`, separate read-only alternate-model agent.
Date: 2026-10-20. Remote/destination: `origin/fix/011-deployment-bootstrap`.
Base: `acc789ca0a7f16fc140b97a69418a42d84909dbd`.
Reviewed implementation: base through `6f8ea11`, including the initial incident
commit `9ac3ad3`. Pending push: that complete range plus this review-only record.
No push, merge or new diagnostic execution is authorized by this review.

## Scope and evidence

The user requested deployment-failure diagnosis/remediation and cleanup of
irrelevant Actions artifacts, then explicitly approved one bootstrap deployment
of release 39397523975 and fixture-variable persistence only on acceptance.
That actual deployment failed native equality before migration/startup.
T048 remains open; local CPU/resource experiments have not reproduced the
captured mismatch. The proposed maintenance slice provides controlled runner
diagnosis, not a renderer fix or qualification waiver.

The reviewer inspected repository policy, governing Feature 011 requirements,
plan and T048, the complete proposed code and documentation, actual diagnostic
limitations, workflow permissions/triggers, metadata/provenance verification,
the unchanged corpus and script regression. It was asked to attempt to identify
significant blocking functional, security/privacy, provenance, runner, failure
handling, test/reliability, scope and documentation issues, without modifying
files or external state and without a desired conclusion.

## Findings and dispositions

| ID | Classification / impact | Implementer assessment and resolution |
|---|---|---|
| R1 | Blocking, functional/reliability, T048. A push sets the effective release-run fallback, but download initially used only the empty manual input. It would search the current diagnostic run for release-evidence, preventing diagnosis. | Valid and fixed in `6f8ea11`: download uses `env.RELEASE_RUN_ID`, the same identity consumed by the API metadata check and provenance verifier. The regression now checks that binding as well as real shell dispatch/failure behavior. |
| N1 | Non-blocking traceability. New diagnostic progress initially existed only in the incident document. | Addressed in the same slice: existing T048 and the release plan now link the failed deployment, maintenance branch, controlled workflow, validation and outstanding authorization/qualification. No task is marked complete. |

The reviewer independently accepted R1 as valid and fixed after inspecting the
revised workflow and assertions and rerunning the diagnostic regression and
whitespace check. It explicitly found no new blocking finding in functional,
security/provenance/privacy, runner/failure handling, test/reliability or
scope/qualification areas. It confirmed the unchanged finite/exact PFM gates,
execution of all comparison cases after failure, nonzero result on mismatch,
verification before execution, pinned existing actions, bounded artifact output,
and absence of AWS/OIDC/protected credentials or repository writes.

## Validation

- Implementer and reviewer: actual diagnostic regression, complete release
  workflow suite, CI contract suite, Git Bash syntax, and whitespace checks PASS.
- The initial implementer release-suite invocation lacked `jq` on this Windows
  PATH and failed. Repeating with the existing local tool directory on PATH
  passed; no package or toolchain change was made.
- Script regression exercises baseline failure, continued repeat/libc cases,
  tunable scoping, all-pass exit zero and invalid-revision refusal.
- Actual selected-run metadata matches the workflow's default-branch,
  workflow-path, successful conclusion, event and source-revision requirements.
- Workflow syntax/permissions were checked against current official GitHub
  workflow documentation. No standalone YAML-parser execution is claimed.
- The new workflow has not run remotely. Local diagnostic success is not cloud
  reproduction, native compatibility approval, live acceptance or deployment.

## Final independent agreement

Final gate: **PASS for this bounded diagnostic publication only**. Commit this
record unchanged only after the alternate reviewer has inspected its exact
bytes, the final implementation/docs and explicitly agreed that all valid
blocking findings are resolved and no new blocking issue remains. The review
does not authorize a push and does not resolve T048 or any deployment gate.

## Round 2: controlled cross-runner candidate capture

Reviewer: the same independent read-only `gpt-6-astra` agent, 2026-10-20.
Remote/destination unchanged. Reviewed follow-up range:
`97b1faf..92484b180cb8af0838a7686c2e4159d9ffa5933e`.
Pending push: this implementation plus this unchanged review-record append.
The owner has explicitly authorized the diagnostic branch push/run; this
review supplies no additional external authority.

Actual first diagnostic39438298299 passed all three cases, failing to reproduce
the earlier deployment mismatch. The follow-up adds four independent runner
samples, CPU identity even on success, unique sample artifacts, and failure-only
collection of eight finite baseline PFMs with an explicitly unapproved manifest.
The published release, source inputs, approved references and deployment gates
remain unchanged. No runtime or production change is proposed.

The reviewer inspected the complete diff and collector, prior R1 binding,
provenance verification, unchanged exact/finite checks, trigger and credential
scope, candidate bounds and data source, tests and evidence limitations.
It explicitly reported no blocking finding. It confirmed all cases continue
after failure, candidate collection resets to baseline rather than inheriting
the final libc experiment, and collection cannot convert a failure to success.
R1 remains valid-and-fixed. No new finding or waiver is needed.

Implementer and independent reviewer both passed the diagnostic regression,
complete release workflow suite with the existing jq tool on PATH, CI contracts,
both Bash syntax checks and whitespace checks. Implementer additionally ran the
real collector with the exact trusted renderer locally: eight finite candidates,
142467264bytes, all matching the existing approved hashes. Windows-only path and
checksum CR adaptation applies to that local run. It is not Intel reproduction.

Independent area conclusions: no blocking functional/failure, security/privacy,
provenance, test/reliability, runner or documentation/scope issue. Existing pinned
actions and read-only permissions remain, with no added dependency, AWS/IAM,
reference acceptance or deployment capability. Fourteen-day artifacts contain
public-corpus outputs; a failing sample can add about143MB of candidates.

Final gate: **PASS for the bounded diagnostic follow-up only**, conditional on
the reviewer inspecting these exact record bytes and final committed range and
explicitly agreeing before this append is committed unchanged. Remote sample
results remain unverified; T048 and deployment qualification remain unresolved.
