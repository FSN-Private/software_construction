# Implementation Plan: Non-production Cost Control

**Branch**: `feature/014-nonprod-cost-control` | **Date**: 2026-09-13 | **Spec**: [spec.md](spec.md)

## Summary

Add one dependency-free lifecycle script and one protected GitHub Actions workflow. Reuse the script from deployment to start RDS before migration. Extend the CDK output and existing OIDC role policy with only the database identifier and instance-scoped lifecycle permissions. Daily shutdown and manual actions share `deploy-nonprod` concurrency.

## Technical Context

**Languages**: Bash, GitHub Actions YAML, TypeScript/CDK, Node.js contract tests, Markdown

**Dependencies**: Existing AWS CLI, GitHub OIDC action, AWS CDK; no new package or action

**Testing**: Mocked AWS CLI state-transition test, workflow contract tests, CDK synth/assert, repository infrastructure validation

**Target**: AWS account `344531454551`, `ap-southeast-1`, protected GitHub `nonprod` environment

**Constraints**: Preserve PostgreSQL data, no production changes, no static credentials, USD 20 alert threshold, 45-minute lifecycle bound

## Constitution Check

- **I**: Pass. Specification, design, and numbered tasks are under the matching feature directory.
- **II**: Pass. Every requirement and criterion maps to code, tests, documentation, or live validation.
- **III**: Pass. No dependency is added; security, reliability, operations, and cost have measurable checks.
- **IV**: Pass. Work began on the clean `feature/014-nonprod-cost-control` branch at `origin/master` (0 behind, 0 ahead).
- **V**: Pass for implementation. Alternate-model review and explicit push authorization remain required before push.

## Proposed Approach

1. Add `scripts/manage-nonprod-lifecycle.sh` with `start-database`, `start`, and `stop` operations. It validates its action and required environment, describes RDS state, uses bounded waiters, updates both ECS services, and prints final state.
2. Add `.github/workflows/nonprod-lifecycle.yml` with manual `start`/`stop` selection and daily `17:00 UTC` scheduled shutdown. Use the protected environment, OIDC, a 45-minute timeout, immutable action pins, and `deploy-nonprod` concurrency.
3. Update deployment to validate the database identifier and invoke `start-database` after AWS authentication but before migration/deployment.
4. Export the RDS identifier from CDK, configure it as a GitHub environment variable, and grant the existing deployment role `rds:DescribeDBInstances`, `rds:StartDBInstance`, and `rds:StopDBInstance` for the single nonprod database.
5. Add deterministic contract tests to the stable infrastructure CI job and update the bootstrap/deployment recovery runbooks.

## Interfaces and State Lifecycle

`manage-nonprod-lifecycle.sh <start-database|start|stop>` reads `DATABASE_INSTANCE`, plus `ECS_CLUSTER`, `ECS_SERVICE`, and `WORKER_SERVICE` for full start/stop.

- `start-database`: `stopped → starting → available`; `starting → available`; `available → no-op`; other states fail.
- `start`: run `start-database`, set both ECS services to one, wait stable, report states.
- `stop`: set both ECS services to zero, wait stable, then `available → stopping → stopped`; `stopping → stopped`; `stopped → no-op`; other states fail.

The workflow dispatch input enters the script through an environment variable and is validated as a fixed allowlist. Scheduled events always select `stop`. Shared concurrency prevents state races with an active deployment; GitHub can replace an older pending run with a newer pending run in the same group.

## Security, Compatibility, and Operations

OIDC remains bound to `repo:FSN-Private/software_construction:environment:nonprod`. GitHub token permissions remain `contents: read` and `id-token: write`. RDS permissions are scoped to the generated DB ARN; ECS permissions already target the two services. No secrets are logged or added.

Rollback is a commit revert before infrastructure update. After deployment, rollback removes the lifecycle workflow and RDS permissions; it does not alter retained database contents. If shutdown fails after ECS reaches zero, rerun `stop`; if start fails, leave services stopped and inspect the reported RDS state.

## Alternatives

- Aurora Serverless v2: rejected for this slice because it changes the database engine contract and migration risk.
- EventBridge plus Lambda: rejected because GitHub Actions already supplies authenticated scheduling and would add code and paid resources.
- Leave RDS running and stop only ECS: rejected because RDS alone exceeds the USD 20 threshold.
- Destroy and recreate nonprod: rejected because retained state and bootstrap time are required.

## Risks

- GitHub scheduled workflows can be delayed; manual stop remains available and budget alarms detect drift.
- RDS automatically restarts after seven stopped days; the daily idempotent stop handles the next schedule, but a delayed workflow can briefly incur compute cost.
- Manual start before initial deployment can select bootstrap task definitions without images; the workflow documentation will state the prerequisite.

## Milestones

1. Specify and implement the lifecycle contract, infrastructure permissions, workflow, tests, and runbook updates.
2. Validate, converge, perform independent pre-push review, and commit the completed feature. No push is authorized yet.
