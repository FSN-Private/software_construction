# ADR-0004: Pin export baskets through immutable snapshots and manifests

- Status: Proposed
- Date: 2026-09-03
- Decision owners: Project maintainer
- Related feature: [Versioned Export Basket](../../features/012-versioned-export-basket/spec.md)

## Context

The product lets the owner browse current images, inspect historical darktable XMP versions, hide noisy commits from normal lineage, rewrite non-default branches with recoverable displaced commits, and eventually purge data after the approved retention window. Exporting from this system is not equivalent to downloading "whatever the path points to now." A branch, tag, working state, filename, or gallery filter can change after the owner adds an item to a basket.

The export feature must therefore make a durable choice about what a basket item means and what evidence accompanies the exported result.

## Decision

Represent each basket item as a pinned selection of immutable identities: logical asset, original-image content, edit identity, and exact XMP state when the export is rendered. Resolving a branch, tag, hidden version, or displaced recovery reference happens at selection time. Later ref movement, hiding/revealing, or normal basket navigation does not change the selected identities.

When export preparation starts, create an immutable export snapshot from the basket and selected preset. All outputs for that request come only from the snapshot. Rendered outputs use the rendering/provenance contract; original exports use the selected original content identity. Every package includes a versioned manifest mapping each requested item to output filename, byte length, source identities, checksums, preset, render provenance when applicable, item status, and preparation time. The exact downloadable package also receives a separately verifiable package checksum.

Uncommitted working states are not exported from mutable paths. They must either be captured as durable export-only snapshots with explicit provenance or the owner must choose a committed version.

Basket pins are exact identity references, not source/XMP lifecycle-retention holds. A basket can keep showing a pinned item after its bytes age out, but availability is true only while existing history, recovery, or storage policy keeps the required source and XMP bytes eligible. Hidden versions remain hidden from normal suggestions, but can be exported when the owner explicitly reveals/selects them. Displaced versions can be exported while their bytes remain retained. Permanently purged bytes make dependent basket items unavailable for re-export.

## Alternatives considered

1. Store only image paths and current branch names in the basket. Rejected because branch movement, rename, or checkout changes could export the wrong version.
2. Store commit IDs only. Rejected because original content identity, edit identity, working snapshots, and export preset/provenance are also required to reproduce or audit output.
3. Copy rendered files into the basket immediately. Rejected because it creates unnecessary derivatives, cost, and stale outputs before the owner chooses a preset.
4. Require all exportable versions to be visible in normal history. Rejected because it weakens the approved hidden-version presentation model.
5. Promise export of every historical reference forever. Rejected because it conflicts with approved permanent purge and privacy/deletion policy.

## Consequences

Positive consequences:

- Export behavior is stable even when refs move or history presentation changes.
- The owner can prove which image/XMP/preset produced each exported output and verify package/download integrity.
- Export snapshots provide a clean idempotency and retry boundary.
- Hidden and displaced versions respect existing version-control semantics.

Negative consequences and risks:

- Snapshot and manifest schemas become long-lived contracts that need versioning.
- Baskets can contain stale unavailable items after lifecycle expiry or purge, so the UI must explain availability clearly.
- Working-state export requires extra care to avoid turning mutable files into ambiguous outputs.
- Manifests must balance provenance with privacy so they do not leak sensitive paths, metadata, or XMP content.

## Security and privacy

Basket contents, snapshots, manifests, and packages are private library data. Download access must be owner-authorized, time-limited, revocable, and unguessable. Audit and operator telemetry must identify operations without media bytes, XMP payloads, full private paths, geolocation values, or secrets.

## Operations

Export preparation must support cancellation, retry, expiry, operator pause/drain, package integrity verification, and cleanup after the configured retention interval. Permanently purged sources must invalidate dependent re-export while retaining non-sensitive audit evidence.

## Migration, rollback, and exit

Initial rollout has no legacy export baskets to migrate. Future schema changes must keep existing snapshots and manifests readable until their export-retention and audit obligations expire. If this decision is superseded, retain old snapshot readers long enough to verify or expire historical packages and record the replacement ADR rather than rewriting this one.
