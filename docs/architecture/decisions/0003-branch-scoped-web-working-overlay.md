# ADR-0003: Branch-scoped web working overlay

**Date:** 2026-09-25
**Status:** Accepted by project owner on 2026-09-25
**Decision owner:** Project owner
**Feature:** [009 gallery metadata search](../../features/009-gallery-metadata-search/spec.md)

## Context and drivers

Feature 006 stores uncommitted XMP by client and branch. Feature 007's Windows namespace currently exposes committed branch projection. Feature 009 requires one web-originated uncommitted edit to become visible to every eligible device on that branch, while preserving device-local conflicts and unrelated XMP. The original and committed XMP must stay immutable.

## Decision

Use a reserved web client identity for each owner/library/branch and expose its current working sidecar as a shared overlay in gallery and Windows namespace reads and to owner-authorized staging/commit. The branch and source checksum identify the web edit. Synchronization compares this shared content with each device's local base; a divergent local edit is kept and reported as a conflict. Only an explicit version-control commit moves the edit into branch history. Edit publication uses existing verified content storage, a compare-and-swap working version, and idempotency.

## Alternatives

| Option | Benefit | Cost |
|---|---|---|
| Shared overlay (chosen) | One authoritative web change and bounded device reads | Requires changes to 006/007 read logic and conflict tests |
| Copy change into every device client | Fits existing client rows | Partial fanout, stale devices, duplicate version state, difficult revocation |
| Commit each web edit immediately | Simplifies namespace reads | Violates approved uncommitted-edit behavior |
| Do nothing | No integration work | Violates FR-018 and AC-010 |

## Consequences and exit

The shared overlay must be owner/branch scoped and never grant a device access to another branch. Namespace revisions must change when the overlay changes; cursor expiry is expected. Device-local edits remain recoverable on conflict. Audit and telemetry omit metadata values. Tests cover two devices, branch switch, revocation, retry, concurrent web/device edits, and a supported darktable round trip. The projection can be rebuilt from immutable content and working records; rollback disables overlay reads after confirming devices retain their local data. No schema contraction occurs in the first rollout. Tasks T003, T011, and T012 implement and verify the decision.
