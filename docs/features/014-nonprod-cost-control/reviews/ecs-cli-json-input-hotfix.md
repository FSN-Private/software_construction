# Feature 014 ECS CLI JSON input hotfix review

**Review date**: 2026-09-14

**Branch**: `fix/014-cli-input-json`

**Base**: merged `origin/master` at `916559116a555ae2c8349aaef418f502aa2346b9`

**Destination**: `origin/fix/014-cli-input-json`

**Alternate model**: GPT-5.6 Sol, high reasoning

## Live finding

Nonproduction deployment run `34809297526` passed release verification, OIDC authentication, RDS startup, immutable image publication, and ECS task-definition reads. The first registration then failed with AWS CLI exit 252 because `--cli-input-json file:///dev/stdin` treated stdin as an input file and reported `Invalid JSON received`.

The correction passes each `jq`-generated API, worker, and migration task definition as one quoted literal `--cli-input-json` argument. It adds a release contract that rejects the failed stdin-file form and requires all three generated JSON variables to be passed directly.

## Pending-push scope

- `scripts/deploy-ecs-release.sh`
- `tests/infrastructure/release/release-workflows.mjs`
- `docs/features/014-nonprod-cost-control/reviews/ecs-cli-json-input-hotfix.md`

Validation passed:

- `npm run test:release-evidence`
- `npm run test:reliability`
- Git Bash syntax checking
- `git diff --cached --check`

## Review rounds

### Round 1 — PASS

The reviewer found no significant blocking correctness, security, or reliability issue in the exact staged two-file implementation diff. Bash preserves each expanded JSON document as one argument, AWS CLI accepts a literal JSON string, failures continue to propagate under `set -e`, and no secret value is introduced into shell source or logs. The regression contract covers all three registration calls.

## Final gate

**PASS** — no unresolved blocking findings remain. An unchanged review of this administrative record and the exact three-file staged scope is required before push. Pushing remains separately authorized.
