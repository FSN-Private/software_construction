# Feature Specification: Platform Foundation and CI/CD

**Feature Branch**: `feature/004-platform-foundation-ci-cd`

**Created**: 2026-09-02

**Status**: Draft — awaiting product-owner approval

**Owner**: Project maintainer

**Parent Program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Input**: Establish the secure, reproducible application and AWS delivery foundation needed by all later child features, with complete GitHub Actions CI/CD, private owner authentication, environment separation, telemetry, and rollback.

## Problem Statement

The product cannot safely add storage, version-control, rendering, or synchronization behavior until contributors have repeatable local validation, protected integration checks, authenticated runtime boundaries, observable non-production deployment, and a controlled production release path. This feature supplies that foundation without implementing photographic domain behavior.

## Goals

- Give contributors one documented way to validate every project component locally and in GitHub Actions.
- Establish stable required checks that protect the default branch and do not expose deployment authority to untrusted changes.
- Deploy a minimal authenticated, observable service skeleton to isolated AWS environments.
- Build each release once, retain its evidence, and promote or roll back that exact artifact.
- Establish shared security, configuration, audit, telemetry, cost, and operational conventions for child features.

## Non-Goals

- Importing, storing, rendering, searching, synchronizing, or versioning user photographs or XMP files.
- Choosing domain-specific database schemas or APIs beyond health, identity, version, and operational contracts.
- Enabling public registration, team membership, billing, or multi-tenant administration.
- Automatically promoting unreviewed changes to production.
- Creating production resources merely because this specification is approved.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Validate a proposed change (Priority: P1)

As a contributor, I can run the same validation categories locally and in GitHub Actions and receive stable, actionable results before a change can merge.

**Why this priority**: Every later feature depends on trustworthy feedback and branch protection.

**Independent Test**: Submit a harmless change and then an intentionally failing change; verify required checks report the exact revision, the failure identifies an owner/action, and the failing revision cannot merge.

**Acceptance Scenarios**:

1. **Given** a pull request from a trusted branch, **When** all required validation succeeds, **Then** every stable required check reports success for the exact head revision.
2. **Given** a pull request with a failing test, validation, build, or security gate, **When** checks complete, **Then** merge is blocked and the failing category supplies actionable diagnostics.
3. **Given** an untrusted fork, **When** validation runs, **Then** it receives no deployment identity, protected secret, trusted artifact publication permission, or privileged runner access.

---

### User Story 2 - Promote and roll back one release artifact (Priority: P2)

As an operator, I can automatically deploy a validated default-branch artifact to non-production, approve the same versioned artifact for production, and restore the last compatible healthy release.

**Why this priority**: Repeatable delivery and rollback are required before domain data is entrusted to the system.

**Independent Test**: Build one release candidate, deploy it to non-production, approve production promotion, compare artifact identities, inject a failed health check, and complete rollback without rebuilding.

**Acceptance Scenarios**:

1. **Given** a validated default-branch revision, **When** delivery begins, **Then** one immutable release set and its evidence are produced and automatically deployed to non-production.
2. **Given** a healthy non-production release, **When** an authorized operator approves a matching versioned release, **Then** production receives the same artifact identities and records the approval and evidence.
3. **Given** failed deployment validation, **When** rollback is invoked, **Then** the last compatible healthy application release is restored and persistent state is neither erased nor falsely reported as rolled back.

---

### User Story 3 - Operate an authenticated observable skeleton (Priority: P3)

As the library owner or operator, I can sign in to a private placeholder portal, verify service health/version, and observe failures, costs, and deployment status without seeing secrets or personal metadata in telemetry.

**Why this priority**: Later features need a proven identity and operations boundary.

**Independent Test**: Provision non-production, sign in as the sole owner, deny an unauthenticated request, trace a synthetic request, trigger a threshold alert, and verify budget and deployment dashboards.

**Acceptance Scenarios**:

1. **Given** the provisioned owner, **When** valid authentication completes, **Then** the portal can call the authenticated service health/version contract.
2. **Given** an unauthenticated or unauthorized caller, **When** it requests a protected operation, **Then** access is denied and an audit-safe event is recorded.
3. **Given** a synthetic service failure or budget threshold, **When** monitoring evaluates it, **Then** the named owner receives an alert within the defined interval with a traceable operation or cost context.

### Edge Cases and Error Handling

- A check is cancelled, skipped, duplicated, or reports against an obsolete revision; it cannot satisfy the required aggregate check.
- A workflow is re-run after a newer commit exists; artifact and check results remain bound to their original revision.
- A dependency registry, runner, or AWS control plane is unavailable; delivery stops safely and distinguishes transient infrastructure failure from a product failure.
- A migration is not backward compatible; production promotion stops before traffic changes and requires an approved forward-recovery plan.
- A release tag does not identify a previously attested artifact; promotion is rejected.
- An environment approval is missing, stale, or performed by an unauthorized identity; production credentials remain unavailable.
- Telemetry contains a token, credential, or prohibited personal field; validation blocks release and the captured material is treated as a security incident.
- Rollback targets an artifact incompatible with current persistent state; the workflow refuses and identifies the allowed recovery path.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST provide documented, deterministic local commands for formatting/linting, static/type validation, unit tests, integration tests, builds/packages, infrastructure validation, dependency/security review, and contract validation as applicable.
- **FR-002**: GitHub Actions MUST run the complete applicable validation suite for pull requests and protected-branch changes and report stable component check names plus one required aggregate result.
- **FR-003**: Protected-branch policy MUST reject a change when any required check is missing, cancelled, skipped, stale, or failing.
- **FR-004**: Untrusted pull-request code MUST be isolated from deployment credentials, protected secrets, trusted release publication, protected environments, and privileged self-hosted runners.
- **FR-005**: The delivery process MUST create one immutable, checksummed release set for a releasable revision and retain its source, dependency, test, security, license, software-bill-of-materials, and provenance evidence.
- **FR-006**: A validated protected-default-branch release MUST deploy automatically to non-production and run post-deployment smoke and health checks.
- **FR-007**: Production promotion MUST require a versioned release, successful evidence verification, and explicit authorization through a protected environment.
- **FR-008**: Non-production and production MUST receive the same immutable release artifact identities; environment-specific configuration MUST be supplied separately and audited.
- **FR-009**: Operators MUST be able to restore the last named compatible healthy release without a rebuild; irreversible persistent-state changes MUST use an approved forward-recovery path.
- **FR-010**: AWS deployment authentication from GitHub Actions MUST use short-lived federated identity scoped to a repository, workflow/ref or environment, audience, account, and least-privilege role.
- **FR-011**: The platform MUST establish isolated non-production, production, and recovery administration boundaries with no public data-store endpoint and no shared long-lived deployment credential.
- **FR-012**: The platform MUST authenticate a single provisioned library owner and reject public self-registration in v1.
- **FR-013**: Runtime components MUST expose authenticated identity/version context, unauthenticated liveness, dependency-aware readiness, structured operation identifiers, metrics, traces, and secret-safe logs.
- **FR-014**: Operators MUST receive actionable alerts for service-level failure, deployment failure, backup-age placeholders, authentication anomalies, CI health, and cost thresholds, with an identified owner and runbook link.
- **FR-015**: Third-party actions, packages, build tools, and base images MUST be pinned or locked through immutable identities/integrity data, reviewed for source, maintenance, license, advisories, permissions, install behavior, and transitive impact, and updated through reviewable changes.
- **FR-016**: The foundation MUST publish versioned shared conventions for configuration, errors, idempotency, audit events, telemetry redaction, health, release manifests, migrations, and environment promotion for child features to consume.
- **FR-017**: The foundation MUST represent required repository branch protections, deployment-environment protections, and external delivery settings as versioned configuration or as versioned verifiable bootstrap evidence, and MUST detect or report drift.

### Acceptance Criteria

- **AC-001**: An intentional failure in each required validation category produces its stable failing check and blocks merge; the corrected revision passes without renaming checks.
- **AC-002**: A fork-originated pull request cannot obtain any AWS identity, protected environment, protected secret, trusted artifact, or privileged runner access.
- **AC-003**: Non-production and production deployment records reference identical artifact digests, checksums, SBOM, provenance, source revision, and release manifest.
- **AC-004**: A failed health check blocks or reverses promotion to the last compatible healthy release within 30 minutes and preserves persistent state.
- **AC-005**: The provisioned owner can authenticate and an unauthenticated caller receives a stable denial response without sensitive detail.
- **AC-006**: A synthetic request can be traced across the placeholder web/service boundary by one operation ID, while logs contain no test credential or prohibited personal value.
- **AC-007**: Alert tests notify the named owner within 15 minutes and link to the relevant dashboard and runbook.
- **AC-008**: Every third-party GitHub Action is pinned to a reviewed full commit identity; every package ecosystem has a committed integrity/lock mechanism; release evidence lists accepted licenses and unresolved risk.
- **AC-009**: The foundation can be destroyed and recreated in non-production from repository configuration and documented prerequisites without manual console-only drift.
- **AC-010**: A verification run detects or reports drift in required repository and deployment-environment protection settings and identifies the approved corrective evidence.
- **AC-011**: At least one child-feature placeholder consumes the published conventions through contract fixtures, and CI validates the conventions' version, error, idempotency, audit, health, and telemetry shapes.

### Key Entities

- **Environment**: An isolated deployment boundary with account/region, configuration, identity role, protection, health, and cost state.
- **Validation check**: A stable required or advisory result bound to an exact revision and evidence set.
- **Release set**: Immutable artifact identities plus source, dependency, SBOM, provenance, test, security, and compatibility evidence.
- **Deployment**: Promotion of a release set into one environment with approvals, timestamps, health, and outcome.
- **Rollback record**: The source and target release sets, compatibility decision, actor, reason, and validation outcome.
- **Owner identity**: The sole provisioned v1 account and its authentication/audit state.
- **Operation signal**: Correlated logs, metrics, traces, alert state, and redaction classification.
- **Dependency record**: Exact identity, source, license, risk, permissions, update policy, and validation evidence.

## Non-Functional Requirements

- **NFR-001 Performance**: At least 95% of ordinary pull-request validation runs complete within 15 minutes; first actionable failure appears within 5 minutes. Verification: 30-day workflow telemetry.
- **NFR-002 Availability**: The deployed skeleton achieves 99.9% monthly availability excluding announced maintenance. Verification: external health monitoring and SLO report.
- **NFR-003 Security**: Network traffic and stored configuration/evidence are encrypted; runtime and workflow permissions are least-privilege; critical/high exploitable findings block release. Verification: threat review, access tests, scans, and permission audit.
- **NFR-004 Privacy**: Telemetry, artifacts, and diagnostics contain no credentials, tokens, image/XMP payloads, geolocation, or unnecessary personal identifiers. Verification: redaction tests and sample review.
- **NFR-005 Accessibility**: The sign-in, service-status, deployment-status, and error experiences conform to WCAG 2.2 AA. Verification: automated and manual keyboard/screen-reader tests.
- **NFR-006 Compatibility**: Local validation supports documented contributor platforms; deployed browser shell supports current stable Chrome, Edge, and Firefox. Verification: compatibility matrix.
- **NFR-007 Observability**: Alerts for sustained availability/latency errors, failed delivery, and budget thresholds reach the named owner within 15 minutes. Verification: fault injection.
- **NFR-008 Maintainability**: Shared conventions and contracts are versioned, tested, and owned; generated files identify their source. Verification: architecture and contract review.
- **NFR-009 Testability**: Every required check has a local equivalent or a documented environment-only reason and reproducible remote verification. Verification: clean-machine quickstart and CI evidence.
- **NFR-010 Localization**: Initial UI text may be English only; time, number, and timezone presentation is locale-aware. Verification: three locale/timezone tests.
- **NFR-011 Deployment**: A validated default-branch release reaches non-production within 20 minutes; approved production promotion completes or safely rolls back within 30 minutes. Verification: delivery telemetry.
- **NFR-012 Operations**: Runbooks cover CI outage, credential compromise, failed deployment, rollback/forward recovery, alert failure, and cost anomaly with owner and abort threshold. Verification: readiness review and exercise.
- **NFR-013 Cost**: Non-production supports scheduled idle shutdown; budgets alert at 80% and 100% of an approved monthly envelope; CI runner/cache/artifact cost is reported. Verification: cost report and alert test.
- **NFR-014 Compliance**: No certification is claimed; dependency licenses, audit retention, privacy jurisdiction, and release-evidence retention require owner review before production. Verification: approval record.
- **NFR-015 Reliability**: Retry, cancellation, duplicate delivery, runner loss, and superseding revisions cannot create conflicting production deployments or unattributed partial releases. Verification: fault-injection exercise.
- **NFR-016 Supply chain**: Deployable artifacts have verifiable checksums, SBOMs, provenance, and source linkage; mutable or unverifiable inputs block promotion. Verification: offline/online artifact verification.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new contributor can run the documented local validation and understand a seeded failure within 15 minutes.
- **SC-002**: 100% of required checks, release artifacts, deployments, approvals, and rollbacks are attributable to an exact source revision.
- **SC-003**: No untrusted pull-request test can access protected credentials, identities, environments, artifacts, or runners.
- **SC-004**: Quarterly rollback exercises restore the last compatible healthy skeleton within 30 minutes without loss of persistent test state.
- **SC-005**: The non-production skeleton can be recreated from repository-controlled definitions and documented bootstrap inputs with zero unexplained manual configuration.

## Constraints, Assumptions, and Dependencies

### Constraints

- GitHub Actions is the required CI/CD orchestrator and AWS is the target hosting environment.
- This feature establishes foundations only; domain behavior belongs to features 005–011.
- No push, repository-setting change, AWS provisioning, paid-feature enablement, or deployment occurs without explicit authorization at execution time.

### Assumptions

- Production uses protected manual approval; non-production deploys automatically from the protected default branch.
- A single owner is provisioned administratively for v1; account recovery is documented but public signup is absent.
- Later child features add their validation to the stable aggregate without renaming established checks.
- Persistent-state migrations use expand/migrate/contract and may require forward recovery instead of destructive down migrations.

### Dependencies

- [003 umbrella program](../003-darktable-version-control/spec.md) and its delivery roadmap.
- GitHub repository support for Actions, environments, branch protection, OIDC, artifacts, and security evidence appropriate to the repository plan.
- AWS accounts, approved region pair, DNS/TLS ownership, budget, and bootstrap authority supplied before deployment.
- A trusted Windows code-signing strategy and dedicated Windows 11 test host are later prerequisites for feature 007, not blockers for this foundation specification.

## Risks and Open Questions

- Foundational scope can expand indefinitely; acceptance is limited to an authenticated observable skeleton and delivery controls.
- Paid GitHub security capabilities may not be available; planning must identify free/equivalent controls and request approval before enabling paid features.
- AWS bootstrap and GitHub environment settings are external mutations and remain separately authorized tasks.
- No material product ambiguity remains. Exact stack, action identities, runner matrix, account/region layout, evidence retention, and cost estimate are planning decisions.
