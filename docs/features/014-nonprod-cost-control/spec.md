# Feature Specification: Non-production Cost Control

**Feature Branch**: `feature/014-nonprod-cost-control`

**Created**: 2026-09-13

**Status**: Approved — the maintainer authorized the recommended start/test/automatic-stop lifecycle

**Owner**: Project maintainer

## Problem Statement

The non-production foundation costs about USD 23/month while idle and about USD 45/month after deployment because RDS and two ECS tasks remain running. The approved budget alert is USD 20/month. Non-production must preserve durable data while stopping compute outside test sessions.

## Users, Goals, and Non-Goals

The user is the project maintainer operating the AWS non-production environment.

Goals:

- Start the database before deployment and allow the maintainer to start or stop non-production manually.
- Stop ECS tasks and RDS automatically every day as a safety net.
- Keep lifecycle operations idempotent, serialized with deployment, observable, and protected by GitHub OIDC.
- Keep typical non-production spend below the USD 20 alert threshold when active compute is limited to about 40 hours/month.

Non-goals:

- Production scheduling or production infrastructure changes.
- Replacing PostgreSQL, ECS, or the existing deployment architecture.
- Guaranteeing a hard AWS spending cap.
- Keeping non-production continuously available.

## User Scenarios & Testing

### User Story 1 — Deploy into a stopped environment (Priority: P1)

When a verified release deploys, the workflow starts or waits for the database before running migrations and starting ECS services.

**Independent Test**: A mocked lifecycle test proves `stopped`, `starting`, and `available` database states converge to `available` without duplicate start requests.

### User Story 2 — Control a test session (Priority: P1)

The maintainer can dispatch a protected workflow with `start` or `stop`. Start makes the database and both ECS services available; stop scales both services to zero before stopping the database.

**Independent Test**: Contract and mocked-command tests verify action validation, ordering, target counts, waits, and idempotent terminal states.

### User Story 3 — Bound unattended cost (Priority: P1)

A daily scheduled run invokes the same stop path at 17:00 UTC. It queues behind an active deployment and never targets production.

**Independent Test**: Workflow contract validation verifies the cron, shared concurrency group, nonprod environment, OIDC permissions, and scheduled stop selection.

### Edge Cases

- Repeating start while resources are already running succeeds without another RDS start request.
- Repeating stop while resources are already stopped succeeds.
- A transitional RDS state is waited when safe (`starting` or `stopping`) and rejected otherwise.
- Stop scales ECS to zero before requesting database shutdown.
- A lifecycle run cannot cancel or overlap an active deployment.
- Manual start before the first successful image deployment can fail because bootstrap task definitions have no runnable image; the runbook must state this prerequisite.
- RDS automatically restarts after seven stopped days; the daily stop retries on the following schedule.

## Requirements

### Functional Requirements

- **FR-001**: The non-production deployment workflow MUST make the RDS instance available before migration.
- **FR-002**: A protected manual workflow MUST accept only `start` or `stop` and MUST target only configured non-production resources.
- **FR-003**: Start MUST make RDS available before setting the API and worker desired counts to one and waiting for ECS stability.
- **FR-004**: Stop MUST set both ECS desired counts to zero and wait for stability before stopping RDS.
- **FR-005**: Lifecycle actions MUST be idempotent for already-running or already-stopped resources and MUST fail on unsupported database states.
- **FR-006**: A daily schedule at `17:00 UTC` MUST invoke stop and MUST share the deployment concurrency group without cancellation.
- **FR-007**: The CDK stack MUST expose the database identifier and grant the existing nonprod deployment role only the RDS describe/start/stop permissions needed for that instance.
- **FR-008**: Lifecycle workflows MUST expose command failure through their run conclusion and a final resource-state summary.

### Acceptance Criteria

- **AC-001**: A deployment beginning with a stopped database reaches migration only after the database waiter succeeds.
- **AC-002**: Manual start results in database `available` and desired count one for both services.
- **AC-003**: Manual or scheduled stop results in desired count zero for both services and database `stopped`.
- **AC-004**: Repeated start and stop invocations issue no invalid duplicate RDS transition.
- **AC-005**: A lifecycle or deployment run queues behind an active run in the same concurrency group rather than overlapping or cancelling it; GitHub may replace an older pending run when another pending run arrives.
- **AC-006**: No lifecycle workflow or IAM policy references production resources.
- **AC-007**: At 40 active hours/month, metered RDS and ECS compute remains below USD 3/month; fixed and usage-dependent services remain monitored by the USD 20 budget alerts.

## Non-Functional Requirements

- **NFR-001 Performance**: Start and stop complete within 45 minutes or fail visibly; verification uses workflow timeouts, bounded AWS waiters, and at least 10 minutes of runner margin after the sequential wait bounds.
- **NFR-002 Availability**: Non-production may be unavailable while stopped; a successful manual start restores service without redeployment.
- **NFR-003 Security**: Lifecycle runs use the protected `nonprod` environment, GitHub OIDC, least-privilege token permissions, and resource-scoped AWS permissions; no static AWS key is added.
- **NFR-004 Privacy**: Lifecycle scripts process resource identifiers and status only and MUST NOT print application secrets or user content.
- **NFR-005 Accessibility**: GitHub workflow inputs use clear action labels; no application UI changes are made.
- **NFR-006 Compatibility**: Existing release evidence, migration, smoke, rollback, API, worker, and production workflows retain their contracts.
- **NFR-007 Reliability**: Operations are ordered, idempotent, serialized, and fail closed on unexpected resource state.
- **NFR-008 Observability**: Every lifecycle run reports requested action and final RDS/ECS state in GitHub Actions output.
- **NFR-009 Maintainability**: One dependency-free shell script owns lifecycle behavior for scheduled, manual, and deployment callers.
- **NFR-010 Testability**: Deterministic mocked AWS tests cover both actions, terminal states, transition states, invalid input, and operation order.
- **NFR-011 Localization**: Not applicable — operator commands and AWS resource states are repository-standard English.
- **NFR-012 Deployment**: The feature deploys through the existing protected release path after merge; no new credentials are required.
- **NFR-013 Operations**: The runbook documents start, stop, schedule, failure recovery, first-deployment prerequisite, and the RDS seven-day automatic restart behavior.
- **NFR-014 Cost**: No new paid AWS service is introduced; scheduled GitHub runner use is bounded to 45 minutes and typical active compute is limited operationally to about 40 hours/month.
- **NFR-015 Compliance**: Not applicable — the feature changes resource runtime state and stores no regulated data.

## Success Criteria

- **SC-001**: Local contract tests prove all lifecycle state transitions and workflow protections.
- **SC-002**: The synthesized template grants only the target RDS instance lifecycle permissions and exports its identifier.
- **SC-003**: A live scheduled or manual stop can leave both ECS services at zero and RDS stopped without deleting retained data.

## Constraints, Assumptions, and Dependencies

- The daily cutoff is 17:00 UTC (00:00 Asia/Bangkok); manual start is available afterward.
- The existing nonprod OIDC role and GitHub environment remain the trust boundary.
- The first manual start occurs only after an initial successful release has registered runnable task definitions.
- AWS budgets alert but do not enforce a hard limit.
- No material open question remains for the authorized scope.
