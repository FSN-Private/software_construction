# Feature 014 RDS stop polling hotfix review

**Review date**: 2026-09-13

**Branch**: `fix/014-rds-stop-polling`

**Base**: merged `master` at `62841aa52fbbe3dcc16985a91937d341fcc634b6`

**Destination**: `origin/fix/014-rds-stop-polling`

**Reviewed range**: `62841aa52fbbe3dcc16985a91937d341fcc634b6..` plus the exact staged hotfix

**Pending push scope**: `scripts/manage-nonprod-lifecycle.sh`, `tests/infrastructure/release/nonprod-lifecycle.mjs`, and this review record

**Alternate model**: GPT-5.6 Sol, high reasoning

## Live finding

AWS CLI v2.36.44 accepted the RDS stop request but rejected `aws rds wait db-instance-stopped` because that waiter does not exist. ECS reached desired and running count zero, and RDS subsequently reached `stopped`.

## Round 1 — BLOCKED

**Classification / severity**: Reliability and timing — Medium, blocking

**Affected scope**: NFR-001, FR-008, NFR-008, NFR-007, T001, T005

**Impact**: The lifecycle job could exceed its promised RDS bound, consume its recovery margin, and terminate before the final state reporter ran.

Replacing the waiter with 99 fixed sleeps did not bound AWS request latency, so the stop path could exceed the promised 1,500-second wall-clock limit and consume the lifecycle workflow's recovery margin.

**Remediation / disposition**: Valid and fixed. The complete polling subprocess now runs under `timeout --foreground 1500`, returns timeout status 124, and fails closed if RDS enters an unexpected state.

## Round 2 — PASS

The reviewer confirmed that the wall-clock timeout covers AWS calls and sleeps, status and timeout reporting are preserved, resource identifiers are not interpolated into the child-shell program, AWS arguments remain quoted, and terminal-state idempotence is unchanged. Regression tests reject the unsupported waiter and cover successful `stopping` polling, timeout exhaustion, and an unexpected state entered during polling.

Validation passed:

- `npm run test:release-evidence`
- `npm run test:reliability`
- `bash -n scripts/manage-nonprod-lifecycle.sh`
- `git diff --cached --check`
- Live terminal-state verification: `action=stop database=stopped api=0 worker=0`

## Final gate

**PASS** — no unresolved blocking findings. Pushing remains separately authorized.
