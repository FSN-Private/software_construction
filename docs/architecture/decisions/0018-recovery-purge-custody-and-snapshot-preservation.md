# ADR-0018: Preserve recovery metadata while completing media purge

**Status**: Accepted for local implementation/tests by explicit owner approval on 2026-10-12; no deployment or live operations
**Date**: 2026-10-12
**Decision owner**: Product owner
**Related feature**: [011](../../features/011-backup-disaster-recovery/spec.md), T029/T034/T035

## Context and decision

The approved [data model](../../features/011-backup-disaster-recovery/data-model.md) preserves media-free database dumps after original/XMP purge. Current purge planning includes affected dumps, and its executor refuses deletion without a verified replacement, protecting the final graph/metadata/audit copy. Current hold history treats indefinite/recovery holds as active until an independent release protocol exists. Lost deletion responses and expiring approvals cannot safely be reconciled from local files.

Adopt only after specific approval the exact [purge-completion proposal](../../features/011-backup-disaster-recovery/reviews/purge-completion-proposal.md): independently validate pinned media-free snapshots and retain them; bind durable tombstone overlays into old-point restoration; use immutable per-version recovery-side admission/result journals; require explicit fresh MFA renewal and separately approved snapshot-fence release; bind independent hold-release custody to exact definitions and original recovery-observed UTC. Preserve current guards until validation succeeds. Add only the unattached proposal's narrow encrypted-table reads and isolated-target cleanup privileges.

## Options and consequences

- Keep the current guard indefinitely: preserves data but cannot complete eligible media purge or clear an independently released hold; fails FR-017.
- Delete all affected dumps: can remove the final graph/metadata/audit copy, contradicting FR-012 and the approved media-only policy.
- Rewrite every dump: adds costly native processing and new graph/audit identities although current v1 contains no original/XMP payload. A future payload-bearing schema still requires separately approved sanitization.
- Preserve validated media-free snapshots with independently authenticated exclusion custody: matches the approved policy and preserves unrelated recoverability, but requires complete schema classification, permanent exclusions and explicit journal/approval protocols. This is the proposed choice.

Historical paths/descriptive metadata deliberately remain private in retained snapshots; this is not metadata erasure. Journal/proof data stay in recovery storage. No primary role gains recovery authority. Validation must prove shared references and final copies remain protected and that old snapshots cannot reopen purged content. Operator uncertainty, new schema or missing evidence stays pending.

No production migration or deployment is authorized. Local implementation remains T034/T035; native restore-overlay coverage remains T029. Rollback stops deletion and retains fences, evidence, dumps and provider locks. Erased payload cannot be recreated by rollback. Account isolation, measured workload, operator MFA custody and real purge/drill qualification remain external gates in T024/T026.
