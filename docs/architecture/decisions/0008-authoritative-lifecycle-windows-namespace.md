# ADR-0008: Authoritative Lifecycle in the Windows Namespace

**Date**: 2026-09-20

**Status**: Accepted

**Decision owners**: Project maintainer

**Feature**: [007 Windows Cloud Files Synchronization](../../features/007-windows-cloud-files-sync/spec.md)

**Extends**: [ADR-0007](0007-windows-cloud-files-provider-and-journal.md)

## Context and drivers

Feature 005 owns asset/edit lifecycle state and current asset paths. Feature 006 branch projections own versioned content identity. Windows Sync originally enumerated path and lifecycle only from the branch projection, so a successful global move, delete, or restore did not change the namespace until an unrelated commit advanced the branch head. A terminal branch cursor could hide the lifecycle change indefinitely and let another device expose stale paths or deleted placeholders.

The fix must retain immutable commit/projection history, make lifecycle-only changes observable without scanning a 100,000-item namespace on every poll, preserve owner isolation, and keep delete recoverable rather than deleting content.

## Decision

The Windows namespace composes branch-projected content identity with current authoritative lifecycle metadata:

- `vc_materialized_state` selects the branch-specific asset/edit and content hash.
- `assets`, `edit_identities`, and the active `asset_paths` row determine current visibility, lifecycle version/state, and asset path.
- Sidecar paths are derived deterministically from the authoritative asset path and the edit's darktable duplicate ordinal.
- Deleted assets and edits are omitted from enumeration but their immutable content and branch history remain intact for authorized restore.

An additive database migration maintains an owner-scoped lifecycle revision through triggers on the three authoritative lifecycle tables. The opaque namespace cursor binds the branch head and owner revision into a SHA-256 snapshot token. A lifecycle-only change therefore expires existing cursors and causes verified full reconciliation, while an unchanged terminal cursor still performs one empty page request.

Global lifecycle remains global across branches and devices; branch checkout and content versions remain independent. This follows the existing Feature 005 authority instead of turning lifecycle operations into device-private Feature 006 working changes.

## Options considered

- Mutating every `vc_materialized_state` row on lifecycle changes would duplicate authority and rewrite branch projections that are intended to represent commit state.
- Recording lifecycle only as a per-device working change would preserve branch locality but contradict the existing global asset/edit lifecycle endpoints and would not converge on other devices without an additional publish workflow.
- Hashing all lifecycle rows on every poll avoids stored revision state but makes idle polling proportional to the owner's full library.
- Using a single global revision is simpler but makes one owner's lifecycle operation invalidate every other owner's cursors.
- Doing nothing leaves accepted lifecycle operations invisible and can revert local paths during reconciliation.

## Consequences and controls

The new table, function, and triggers are additive and owner-scoped. Runtime permissions are inherited through the migration provisioner; the revision contains no path, content, or credential data. The cursor exposes only a digest, not a branch head or revision. Integration coverage uses two registered devices and verifies move, edit delete/restore, asset delete/restore, cursor expiry, deterministic sidecar paths, and the unchanged terminal poll.

Lifecycle activity invalidates every branch cursor for the affected owner because lifecycle is global. That can cause extra full reconciliation after a lifecycle operation, but not during idle polling; this is preferred to stale or cross-device-inconsistent namespace state. If lifecycle later becomes branch-scoped, a new ADR and migration can replace the owner revision with branch-scoped working-state revisions.

## Migration, rollback, and follow-up

Migration 008 creates and seeds the owner revision table and installs the triggers without changing the checksum-pinned migration 007. A prior server can coexist with the additive objects, but it does not understand lifecycle-aware cursors and must not serve Windows Sync traffic during rollback. Stop synchronization, roll back the service, and use the documented compatible-client/state recovery procedure. Remove the triggers/table only in a later migration after all lifecycle-aware servers and clients are retired.

Task T025 and the Feature 007 validation/review records carry the implementation and evidence. Trusted Windows, reliability, and release-signing gates remain unchanged.
