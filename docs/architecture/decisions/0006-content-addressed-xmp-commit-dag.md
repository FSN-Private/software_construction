# ADR-0006: Content-Addressed XMP Commit DAG

**Date**: 2026-09-03
**Status**: Accepted — 2026-09-10
**Owners**: Project maintainer
**Feature**: [006 XMP Version-Control Core](../../features/006-xmp-version-control-core/spec.md)

## Decision

Use a PostgreSQL-backed immutable library-wide DAG whose commit ID is the SHA-256 digest of a versioned canonical manifest. Publish commit, expected-head ref CAS, reflog, idempotency outcome, and content lifecycle holds in one serializable transaction. Maintain rebuildable projections, retain ref-displaced commits for 90 days, and keep hiding as separate presentation metadata. The core preserves divergence and publishes only resolver-supplied merge content.

## Alternatives and consequences

Git/LFS, mutable rows, last-writer-wins, automatic textual XMP merge, and authoritative projections are rejected because they cannot meet the custom atomicity/recovery contract. This adds canonicalization, graph-verification, and repair complexity, but provides explicit evidence and stable downstream contracts. No user content or deployment is authorized by this proposed decision.
