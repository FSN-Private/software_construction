# Feature 008 release plan

**Status**: Draft — no deployment authorized.

## Prerequisites

- All Feature 008 tasks, local tests, native corpus evidence, compatibility approval, and review gate pass.
- The image is digest pinned with SBOM, license, and vulnerability evidence.
- Database migration rehearsal, queue/DLQ permission review, derivative retention setting, and rollback drill pass.

## Rollout and rollback

1. Apply the additive migration and verify the migration ledger.
2. Deploy the worker with intake paused; verify it has no database/source-write credentials.
3. Enable a small owner-scoped current-preview canary and observe queue age, failure categories, cache reuse, duration, and private-object access for 30 minutes.
4. Enable normal intake only if cached and uncached targets hold and no unsafe failure data appears.

Abort on source mutation, cross-library access, provenance mismatch, unbounded queue age, or any critical/high dependency finding. Roll back by pausing intake and restoring the last approved matrix; do not delete completed derivatives or source/history data during rollback.
