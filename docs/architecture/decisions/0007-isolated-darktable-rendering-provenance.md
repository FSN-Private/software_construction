# ADR-0007: Isolated darktable rendering with immutable provenance

- **Status**: Proposed — implementation authorized; renderer/profile release approval remains pending
- **Date**: 2026-09-21
- **Owners**: Library product owner and platform maintainers
- **Feature**: [008 Darktable Rendering](../../features/008-darktable-rendering/spec.md)

## Context

Previews must faithfully represent one immutable original and one explicit XMP state. The native renderer processes untrusted media, so it cannot share production metadata, version-history, credentials, or writable source storage. Historical versions depend on feature 006, which is not implemented on this baseline.

## Decision

Use a dedicated, resource-bounded worker to invoke a digest-pinned `darktable-cli` release in a private per-library workspace. The API owns durable request, attempt, rendition, and provenance records. The worker may read only selected immutable input versions and write only private derivative objects; it receives neither production database credentials nor source/history write authority.

The rendition key contains source/XMP identities, selected version, rendition, profile, renderer artifact/release, and compatibility-matrix version. A derivative becomes visible only when it and a matching provenance record are atomically committed. Missing or unresolved historical versions are unavailable; no branch-head or current-state fallback is allowed.

## Alternatives

1. Render in the API process — rejected because native input handling would share API credentials and availability.
2. Use shared workspaces or the owner’s darktable database — rejected because state can leak or mutate between jobs.
3. Store a mutable cache key or defer provenance — rejected because it can return a visually plausible but incorrect rendition.
4. Wait to implement any rendering until feature 006 exists — rejected for current-preview capability, but historical rendering remains blocked.

## Consequences

- Adds a queue, private derivative storage, worker policy, corpus gate, and operational controls.
- Makes provenance and failure state available to features 009, 010, and 012 without making derivatives authoritative content.
- Requires explicit image/profile compatibility approval, artifact evidence, and a trusted Linux-native validation runner before serving a new renderer release.
- Rollback pauses new intake and drains/cancels pending attempts. Completed immutable derivative/provenance pairs remain readable; no source/history migration is required.

## Follow-up

The numbered tasks in [Feature 008](../../features/008-darktable-rendering/tasks.md) implement this decision. Feature 006 must deliver a selected-version resolver before the historical-preview slice can complete.
