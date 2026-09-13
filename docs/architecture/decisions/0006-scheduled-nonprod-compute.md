# ADR-0006: Schedule non-production compute through GitHub Actions

**Date**: 2026-09-13

**Status**: Accepted

**Decision owner**: Project maintainer
**Feature**: [Non-production Cost Control](../../features/014-nonprod-cost-control/spec.md)

## Context

The existing non-production RDS and ECS design costs more than the USD 20 monthly alert threshold when continuously active. The environment must retain PostgreSQL and object data but does not require continuous availability.

## Decision

Use the existing protected GitHub `nonprod` environment and OIDC deployment role to run one dependency-free lifecycle script. Provide manual start and stop actions, start RDS automatically before deployment, and run an idempotent stop every day at 17:00 UTC. Serialize lifecycle and deployment workflows with one concurrency group.

## Options Considered

- **GitHub Actions lifecycle**: selected because authenticated scheduling, audit logs, concurrency, and the AWS CLI already exist.
- **EventBridge and Lambda**: rejected because it adds infrastructure, code, permissions, and cost for behavior GitHub already supplies.
- **Aurora Serverless v2**: rejected because changing the database engine contract is disproportionate to nonprod scheduling.
- **Keep compute running**: rejected because it exceeds the approved alert threshold.

## Consequences

Typical cost can remain below USD 20 when active compute stays near 40 hours/month. Non-production is unavailable while stopped and RDS startup adds several minutes. GitHub schedule delays and outages can delay shutdown, so manual stop and budget alerts remain necessary. RDS and ECS data are preserved; rollback removes automation and permissions without migrating data.

The role gains only instance-scoped RDS describe/start/stop access. Production resources and workflows are unchanged. Follow-up work is tracked in [tasks.md](../../features/014-nonprod-cost-control/tasks.md).
