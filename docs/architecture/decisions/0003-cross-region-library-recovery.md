# ADR-0003: Cross-region library recovery in an independent AWS account

**Status**: Proposed  
**Date**: 2026-09-03  
**Decision owner**: Product owner  
**Related feature**: [011 Backup and Disaster Recovery](../../features/011-backup-disaster-recovery/spec.md)

## Context

The darktable library contains irreplaceable originals and XMP version history. The approved program requires multi-AZ primary operation, a recovery copy in a second region, cold storage, a maximum one-hour recovery point objective, and a 24-hour regional-disaster recovery objective.

## Decision

Use a recovery copy in a separate AWS account and second AWS region. Keep long-retention recovery data in cold storage while retaining sufficient accessible recovery material to meet the stated objective. A recovery must be validated in isolation before promotion and must not depend on primary-region credentials.

## Options considered

- Keep backups only in the primary account and region: lower cost and complexity, but does not survive account or regional compromise.
- Use a second region in the same account: improves regional resilience but leaves a shared account trust boundary.
- Use an independent account and second region: meets the required isolation and regional-loss objectives with greater operational and retrieval cost.

## Consequences

This choice improves isolation and makes recovery drills meaningful, but adds account governance, cross-boundary access management, retrieval planning, and recurring verification work. Exact storage tiers, replication mechanics, encryption-key ownership, and runbook procedures remain design decisions for feature 011 planning. The decision may be superseded if measured drills cannot meet the binding RPO/RTO.
