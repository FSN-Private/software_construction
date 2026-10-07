# ADR-0015: Isolate recovery verification writes from copy authority

**Status**: Proposed; scoped local IAM authorization pending
**Date**: 2026-10-07
**Decision owner**: Product owner
**Related feature**: [011 Backup and Disaster Recovery](../../features/011-backup-disaster-recovery/spec.md), T010/T012/T013
**Related decision**: [ADR-0003](0003-cross-region-library-recovery.md)

## Context

The ordinary verifier needs durable point receipts and a monotonic signed audit-head binding, alongside publication to the configured primary observation queue. The recovery control table already contains copy pointers and the append-only owner-point membership index. DynamoDB IAM `LeadingKeys` constrains partition keys, not sort keys. Granting `PutItem` on `POINT#*` therefore permits replacing a `COPY` row even when the worker intends to write only `VERIFY`. An attribute allowlist does not prevent replacement of the existing item.

[AWS documents the partition-key scope of LeadingKeys](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/specifying-conditions.html). [Transactional writes use their underlying PutItem/UpdateItem permissions](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis-iam.html); a transaction does not supply a separate sort-key authority boundary.

## Proposed decision

Keep the verifier read-only on the control table: strongly consistent reads of the configured owner/library index and exact indexed copy pointers, without Scan. Store `POINT#<id>/VERIFY`, the recovery-only signed historical proof at `POINT#<id>/AUDIT_VERIFY`, and `OWNER#<scope>/VERIFIED` records in a separate recovery-owned verification table. Write grants target only that table. Advance the audit sequence/hash conditionally after proving the previous trusted pair and externally pinned minimum occur in the validated chain. The table uses retained storage, encryption and point-in-time recovery. The verifier cannot change copy pointers, membership, writer intents or purge fences.

The separate verifier task role may read exact recovery versions, use the existing evidence signer, access the recovery database credential and publish the specifically approved signed catalog to the exact observation queue. It receives no owner-approval signing, primary data reads, deletion, deployment or task-launch authority. Its concrete policy proposal remains unattached until its scoped privileges are authorized. The drill role remains without primary permissions.

## Options and consequences

- Shared-table writes have fewer resources but fail to enforce the required boundary at IAM level; rejected.
- Attribute-restricted updates avoid some mutations but couple verifier privileges to copy/index fields and future schema changes; rejected for the durable boundary.
- A separate table adds a small resource and operational configuration, while IAM denies copy-authority mutations regardless of a worker defect; proposed.
- No persistent verifier receipt leaves publication retries and audit rollback protection incomplete; it cannot satisfy T012/T013.

No existing table data is rewritten. Provision the new table before enabling the verifier, with an independent audit minimum; never infer a trusted minimum from an untrusted restored database. Rollback disables the verifier schedule and retains the table and signed evidence. Local policy tests must prove the role has no control-table writes; actual IAM/database isolation remains an external qualification requirement. This decision supplements ADR-0003 and does not authorize deployment.
