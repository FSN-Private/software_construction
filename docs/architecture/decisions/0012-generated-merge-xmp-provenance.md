# ADR-0012: Provenance for generated merge XMP

**Date**: 2026-09-26
**Status**: Accepted for feature 010 implementation under the project owner's instruction to proceed
**Decision owner**: Project owner
**Feature**: [010 lineage and merge interface](../../features/010-lineage-merge-interface/spec.md)

## Context and drivers

Feature 006 validates every XMP digest entering a commit against an authoritative source. Feature 010 must create a new XMP result when safe disjoint metadata edits are combined. That result is neither an original edit nor a client upload, and claiming either source would make provenance false. The result must remain bound to its asset/edit and exact base, target, and source inputs while feature 006 retains commit authority.

## Decision

The server generates merged XMP only from verified authoritative input objects under a versioned conservative policy. It publishes and verifies the resulting content-addressed object, then records one durable provenance row containing owner, library, asset/edit, result digest, exact input digests, policy version, and operation ID. Feature 006 accepts that result as authoritative only for the matching asset/edit and only while the content object is verified. The resulting commit still uses feature 006 staging, expected-head checks, audit, and atomic publication. No browser-provided raw XMP is accepted by this path.

## Options considered

| Option | Outcome |
|---|---|
| Reuse upload-session rows for generated content | Rejected: represents a server merge as a user upload. |
| Change the edit identity's current XMP digest | Rejected: mutates unrelated identity state before a commit. |
| Trust any existing content object digest | Rejected: crosses asset/edit and owner boundaries. |
| Record typed generated-content provenance | Chosen: narrow trust extension with auditable inputs. |

## Consequences and controls

- Positive: feature 006 can validate exact generated content without weakening its other provenance rules; reconstruction can identify how each merged XMP object was produced.
- Negative: one additive table and reconstruction/integrity coverage are required. A generated object may remain unreachable after a stale-head rejection and must be collected only by the established object lifecycle, never by an unsafe immediate delete.
- Security/privacy: rows store digests and identities, not XMP values or private metadata. Owner/library and asset/edit constraints must be checked before publication and readback. Logs and errors carry safe codes and operation IDs only.
- Operations: deploy migration and API before enabling the web merge action. A rollback retains the table and objects for already committed history. Remove this provenance type only after migration of every referenced commit and recovery proof.
- Follow-up: [T008–T010](../../features/010-lineage-merge-interface/tasks.md) implement authority, exact-head confirmation, tests, and reconstruction evidence.
