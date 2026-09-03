# Research: Image Ingest and Durable Storage

**Feature**: [spec.md](spec.md) | **Date**: 2026-09-03 | **Status**: Complete for planning

1. **S3 plus PostgreSQL** — S3 holds immutable, checksum-addressed bytes; PostgreSQL holds logical identity, associations, lifecycle, paths, audit links, and repair state. Database blobs and path-as-identity were rejected for streaming, lifecycle, and move correctness.
2. **Repairable publication, not fictional distributed atomicity** — record intent/outbox, verify and promote content, then atomically publish metadata; reconciliation makes failures idempotently repairable. Acknowledgement waits for both authorities. This is preferable to unsafe best-effort dual writes.
3. **Internal deduplication, distinct logical intent** — same immutable bytes reuse one content object but can have multiple assets/edit identities and independent path/audit state.
4. **Conservative association** — only darktable base/numbered naming is automatic. Unknown, ambiguous, malformed, or unsupported XMP is private quarantine and has no downstream authority.
5. **Hold-aware recovery** — 90-day soft deletion changes discoverability, not immutable object authority. Physical purge and cold-copy lifecycle belong to feature 011.

The inherited AWS/S3 and recovery evidence is in [program research](../003-darktable-version-control/research.md#decision-7-use-postgresql-for-searchable-metadata-and-s3-for-immutable-content).
