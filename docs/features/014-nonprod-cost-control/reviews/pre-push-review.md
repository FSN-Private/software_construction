# Feature 014 adversarial pre-push review

**Review date**: 2026-09-13

**Alternate model**: GPT-5.6 Sol, high reasoning

**Destination**: `origin/feature/014-nonprod-cost-control`

**Base**: `origin/master`
**Reviewed scope**: Complete tracked and untracked feature-014 working tree intended for one milestone commit and later push; no push is authorized by this review.

## Reviewer scope

The reviewer checked every feature requirement, acceptance criterion, NFR, and numbered task against the implementation; lifecycle state and failure paths; concurrency; IAM and OIDC boundaries; workflow syntax and action pins; secrets and privacy; compatibility; cost behavior; observability; rollout and rollback; tests; documentation; unrelated scope; and commit-history clarity.

## Round 1 — BLOCKED

### R1-1 — High, blocking: cold RDS start exhausted deployment recovery time

**Evidence**: The 60-minute deployment job added a 1,500-second database-start allowance before the existing 2,100-second deployment allowance. Their sum consumed the outer timeout before setup overhead or paired rollback. The reliability test subtracted only the deployment timeout and the runbook still promised a 25-minute reserve.

**Affected scope**: FR-001, AC-001, NFR-006/NFR-007, T002/T003/T005/T006.

**Disposition**: **Valid and fixed.** The nonprod job timeout is now 90 minutes. Reliability validation extracts the 1,500-second RDS allowance and proves at least 20 minutes remain after it and the 2,100-second deployment allowance; the actual configured reserve is 30 minutes. The runbook distinguishes this from the unchanged production timing.

### R1-2 — Medium, blocking: failures omitted or masked final state

**Evidence**: `set -e` exited before summary output on mutation/wait failures, no workflow step ran reporting under `always()`, and failed state probes inside `printf` command substitutions could be masked by `printf` success. Tests omitted AWS failures and the `stopping` transition.

**Affected scope**: FR-008, NFR-007/NFR-008/NFR-010, T001/T002/T004/T005/T006.

**Disposition**: **Valid and fixed.** State probes are captured and checked before formatting, retain explicit `unknown` values on failure, and return nonzero. The workflow always invokes the report action and preserves the earlier job failure. Tests now cover ECS update failure, RDS waiter failure, final-probe failure, explicit `unknown` reporting, and the `stopping` state.

### Round 1 non-blocking notes

- The Feature 004 pre-deploy checklist contains live Cognito, authentication, and CDK bootstrap evidence gathered during this authorized setup. Retained because it replaces stale prerequisite status needed for the upcoming foundation deployment; it is not product behavior.
- GitHub may replace an older pending concurrency run when another pending run arrives. AC-005 and the plan now state the guarantee only for the active run while documenting that platform behavior.

### Areas without a blocking finding

The reviewer found no blocking issue in lifecycle ordering and supported-state idempotence, schedule/manual action selection, active-run serialization, exact RDS IAM scope, OIDC/environment/token permissions, action pins and YAML structure, secrets/privacy, production isolation, cost calculation and cutoff, retained data, dependencies, or rollout/rollback apart from R1-1.

## Validation after remediation

- `npm --prefix packages/infrastructure run validate` — PASS
- `npm run test:ci-contract` — PASS
- `npm run test:release-evidence` — PASS
- `npm run test:reliability` — PASS
- `npm run validate:security` — PASS
- `bash -n scripts/manage-nonprod-lifecycle.sh` — PASS
- GitHub workflow YAML parsing — PASS
- `git diff --check` — PASS (line-ending conversion warnings only)
- Spec traceability — 33 unique FR/AC/NFR/SC IDs mapped; convergence found no actionable gap

## Round 2 — BLOCKED

The reviewer accepted the R1-2 disposition. It found R1-1 incomplete because the 90-minute runner still used one default one-hour OIDC role session: after the allowed 25-minute database start and 35-minute deploy attempt, credentials could expire as rollback began.

**R1-1 revised disposition**: **Valid and fixed.** The workflow now authenticates once for database startup, then refreshes OIDC credentials after the database is available and before the 35-minute deploy script. Release and reliability contracts require exactly two pinned credential steps in that order. Deployment and rollback therefore receive a fresh one-hour role session without widening the role's maximum duration.

No other new blocking finding was reported in round two.

## Round 3 — PASS

The reviewer inspected the exact staged 21-file diff and found no unresolved blocking issue. It confirmed that the second pinned OIDC authentication occurs after the bounded database start and immediately before the bounded deployment, giving deployment and rollback a fresh one-hour session. The 90-minute job retains 30 minutes after the maximum 25-minute database start and 35-minute deployment attempt, while contract tests enforce both credential steps and their order.

The reviewer also reconfirmed the R1-2 remediation and found no new blocking issue in state transitions, failure paths, active-run concurrency, IAM scope, OIDC and token permissions, action pins, workflow syntax, secrets and privacy, compatibility, cost behavior, observability, retained data, rollout and rollback, tests, documentation, scope, traceability, or history clarity. It verified that `git diff --cached --check` passes, the index contains all 21 intended files with no unstaged or untracked changes, and `HEAD` still matches `origin/master` before the milestone commit.

## Final gate

**PASS** — alternate-model review completed with no unresolved blocking findings. The feature is approved for its milestone commit; pushing remains separately authorized.
