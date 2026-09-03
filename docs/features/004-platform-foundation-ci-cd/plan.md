# Implementation Plan: Platform Foundation and CI/CD

**Branch**: `feature/004-platform-foundation-ci-cd` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)  
**Status**: Proposed — pending approval with the feature specification

## Summary

Establish a build-once, promote-by-digest platform foundation: an ASP.NET Core 10 API and React 19 web shell, AWS CDK infrastructure, GitHub Actions validation and artifact evidence, and a private Cognito owner boundary. The feature publishes conventions and a minimal authenticated health/version service only; photographic-domain behavior remains out of scope.

The durable platform choice is recorded in proposed [ADR-0003](../../architecture/decisions/0003-platform-foundation-and-delivery.md). It adopts the approved program research: .NET 10/C# 14, Node 24/TypeScript 6, CDK v2, ECS Fargate, RDS PostgreSQL, Cognito, and GitHub OIDC with immutable artifact promotion.

## Technical Context

| Area | Decision |
|---|---|
| Languages | C# 14/.NET 10 LTS; TypeScript 6/Node 24 LTS |
| Dependencies | ASP.NET Core, React 19, AWS CDK v2, OpenTelemetry; exact package versions locked during implementation |
| Storage | RDS PostgreSQL Multi-AZ for foundation state; S3/ECR for immutable release artifacts; no photographic storage |
| Testing | `dotnet test`, Node lint/typecheck/test/build scripts, CDK synth/assertions, contract fixtures, security and IaC scans |
| Platforms | Linux ECS Fargate; current Chrome/Edge/Firefox; Windows 11 CI lane reserved for feature 007 |
| Performance | 95% PR runs <15 minutes; first failure <5 minutes; non-prod <20 minutes; promotion/rollback <30 minutes |
| Environments | Separate non-production, production, and recovery accounts; primary `ap-southeast-1`, recovery `ap-southeast-2`, revalidated before provisioning |

## Constitution Check

- **PASS — authority and traceability**: this plan implements only [spec.md](spec.md); `tasks.md` will map every requirement.
- **PASS — feature isolation**: planning is on `feature/004-platform-foundation-ci-cd`, 46 commits ahead and 0 behind `master`; no reconciliation occurs.
- **PASS — security and supply chain**: least-privilege Actions, OIDC, full-SHA action pins, locked packages, SBOMs, provenance, and release gates are designed first.
- **PASS — reversible operations**: deploy by immutable digest; use expand/migrate/contract and forward recovery when rollback is unsafe.
- **GATE**: this plan authorizes no AWS/GitHub mutation, deployment, paid service, or production release.

## Design

### Structure

```text
apps/api/                       # ASP.NET Core: /live, /ready, /v1/session, /v1/version
apps/web/                       # React authenticated placeholder/status view
packages/contracts/             # versioned error, audit, telemetry, health, idempotency fixtures
packages/infrastructure/        # AWS CDK application and environment stacks
packages/release-tools/         # manifest, checksum, evidence verification
tests/{contract,integration,unit,infrastructure}/
.github/workflows/              # CI, build, deploy, release, rollback, recovery drill
docs/runbooks/
```

The API is the canonical source for status and identity context. The web shell consumes its versioned contract. Configuration is runtime-injected and does not alter artifact identity.

### Delivery contract

| Workflow | Event | Stable checks/output | Trust boundary |
|---|---|---|---|
| `ci.yml` | PR and protected-branch push | `ci / backend`, `ci / web`, `ci / windows`, `ci / infrastructure`, `ci / security`, `ci / required` | PRs are read-only: no secrets, OIDC, protected environment, trusted publication, or privileged runner |
| `build-release.yml` | successful protected-default revision | API/worker images, web bundle, CDK assembly, checksums, SPDX SBOM, provenance | narrowly scoped artifact/attestation permissions |
| `deploy-nonprod.yml` | verified manifest | exact digests and smoke evidence | OIDC role scoped to non-production account |
| `release.yml` | matching `v*` tag | same digests after protected `production` approval | OIDC production role after approval only |
| `rollback.yml` | authorized dispatch | named compatible manifest restored without rebuild | protected environment |
| `recovery-drill.yml` | scheduled/manual | isolated recovery evidence | recovery account only |

Workflows use top-level `contents: read`, explicit job permissions, timeouts, and cancellation for obsolete non-deployment work. No workflow uses `pull_request_target` to check out untrusted code. Third-party actions use reviewed full commit SHAs and readable version comments. Protection settings are bootstrap evidence and drift-checked.

### Lifecycle, security, and operations

1. CI binds every check to the revision and aggregate result.
2. A protected-default build publishes one manifest containing source SHA, digests, checksums, SBOMs, provenance, dependency/security evidence, and compatibility state.
3. Non-production verifies evidence and deploys listed digests through OIDC; smoke checks record health.
4. A matching tag and protected approval promote identical digests to production.
5. A failed health gate restores the last schema-compatible healthy manifest; incompatible state uses forward recovery.

Each environment has its own account, KMS keys, OIDC role, VPC, private data stores, budget, telemetry, and audit boundary. Cognito permits only an administratively provisioned owner. `/live` is unauthenticated and detail-free; `/ready`, `/v1/session`, and `/v1/version` follow [health-and-status-v1.md](contracts/health-and-status-v1.md). Every request has an operation ID; structured logs are redacted before export.

Data entities and lifecycle appear in [data-model.md](data-model.md). No foundation record contains image/XMP payloads or geolocation.

Validation is local-first and deterministic. CI distinguishes product failures from runner, registry, or AWS outages while preserving source and retry evidence. Versioned errors exclude secrets. Redaction fixtures cover tokens, credentials, location, and image/XMP-shaped values. The UI meets WCAG 2.2 AA and current Chrome/Edge/Firefox; English is acceptable but dates/times/numbers are locale-aware.

## Migration, rollout, and risks

Migrations use expand/migrate/contract. Rollback is offered only while schema compatible; otherwise promotion aborts or runs an approved forward-recovery procedure. Runbooks cover CI outage, credential compromise, failed deployment, rollback/forward recovery, alert failure, and cost anomaly with owner and threshold.

Kubernetes, Lambda-only, long-lived AWS keys, rebuild-on-deploy, public registration, and automatic production promotion are rejected as inconsistent with scale or security. Account IDs, DNS, budgets, action SHAs, patch versions, quotas, pricing, and code-signing custody are execution inputs that must be reverified before external mutation.

## Post-design Constitution Check

**PASS.** No material design ambiguity remains. Deployment inputs are explicitly unauthorized pending approval.
