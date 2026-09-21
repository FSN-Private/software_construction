# Feature 014 ECS task-definition IAM hotfix review

**Review date**: 2026-09-14

**Branch**: `fix/014-ecs-describe-policy`

**Base**: merged `origin/master` at `384e57f4c432e7ba45093d390c7159c92ddaa717`

**Destination**: `origin/fix/014-ecs-describe-policy`

**Alternate model**: GPT-5.6 Sol, high reasoning

## Live finding

Nonproduction deployment run `34807329802` passed immutable release verification, both OIDC authentications, RDS startup, image loading, ECR login, and the repaired Bash initializer. AWS then denied `ecs:DescribeTaskDefinition` because the deployment role allowed that action only on three task-family ARN patterns while AWS evaluated the request against resource `*`.

The repository correction changes only the read-only `ecs:DescribeTaskDefinition` statement to `Resource: "*"`. Existing `ecs:RunTask`, `ecs:UpdateService`, `iam:PassRole`, ECR, and RDS permissions retain their narrower resources and conditions. Applying the equivalent live IAM update remains pending explicit approval.

## Pending-push scope

- `packages/infrastructure/src/platform-foundation-stack.ts`
- `packages/infrastructure/scripts/assert-template.mjs`
- `docs/features/014-nonprod-cost-control/reviews/ecs-describe-task-definition-policy.md`

Validation passed:

- TypeScript infrastructure check
- offline CDK synthesis
- synthesized-template assertions, including exact `Resource: "*"` for `ecs:DescribeTaskDefinition`
- infrastructure secret scan
- dependency-tree and license validation
- `npm run test:release-evidence`
- `git diff --cached --check`

The local npm advisory request could not reach the registry through the sandbox. No dependency changed, and required infrastructure CI retains the authoritative networked vulnerability check.

## Review rounds

### Round 1 — PASS

The reviewer found no blocking correctness, security, or reliability issue in the exact staged two-file implementation diff. AWS documents no resource type or condition key for `DescribeTaskDefinition`, so the isolated read-only wildcard is required. The new synthesized-template assertion prevents regression while adjacent mutating permissions remain scoped.

### Round 2 — BLOCKED

The reviewer found one Medium traceability issue: the record still said the final gate was pending, so it did not contain final reviewer agreement for the exact three-file scope. The finding was valid and fixed by adding an explicit final disposition.

### Round 3 — BLOCKED

The reviewer confirmed the code and test remained technically sound but found that the record had incorrectly rewritten Round 2 as a PASS. The finding was valid and fixed by preserving Round 2 as BLOCKED, its disposition, and this Round 3 result.

### Round 4 — PASS

The reviewer found no blocking issue in the exact three-file scope. The implementation confines the required wildcard to read-only `ecs:DescribeTaskDefinition`, adjacent mutating permissions remain scoped, all prior findings and dispositions are preserved accurately, and the deferred advisory check remains non-blocking because dependency files are unchanged and required CI runs it.

## Final gate

**PASS** — alternate-model review completed on the exact three-file staged scope with no unresolved blocking findings. Pushing remains separately authorized.
