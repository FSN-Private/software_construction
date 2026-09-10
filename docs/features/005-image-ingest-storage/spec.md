# Feature Specification: Image Ingest and Durable Storage

**Feature Branch**: `feature/005-image-ingest-storage`

**Created**: 2026-09-02

**Status**: Approved — owner authorization recorded 2026-09-07

**Owner**: Project maintainer

**Parent Program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Depends On**: Feature 004 platform foundation contracts

**Input**: Import original photographs and darktable XMP sidecars safely, assign stable identity, preserve exact bytes and checksums, support retrieval/move/soft-delete/restore, and establish authoritative storage and lifecycle behavior for later version-control, synchronization, rendering, and gallery features.

## Problem Statement

Every later product capability depends on trustworthy image and XMP objects. The system needs resumable ingest, deterministic association, immutable originals, byte-exact sidecars, stable identity across moves, safe deletion, and visible integrity state before it can publish commits or render previews.

## Goals

- Import supported images and associated darktable XMP sidecars without modifying source or original bytes.
- Detect duplicates, corruption, unsupported content, and ambiguous sidecar association before publication.
- Provide stable logical identities independent of file path and upload retry.
- Support verified download, move/rename, recoverable deletion, and restoration.
- Establish authoritative versus quarantine/temporary/derivative lifecycle boundaries.

## Non-Goals

- Commits, branches, tags, merges, reflog, or historical lineage.
- Windows placeholders, offline synchronization, or device branch checkout.
- darktable rendering, thumbnails, or preview caches.
- Full gallery search/filter UX or browser metadata editing.
- Permanent purge from cold recovery copies; feature 011 owns final recovery lifecycle.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Import a verified photographic batch (Priority: P1)

As the library owner, I can import a folder/batch of supported images and XMP sidecars, see per-item progress, and trust that acknowledged objects are complete, associated, checksummed, and recoverable.

**Why this priority**: No other domain feature can operate without durable authoritative content.

**Independent Test**: Import a representative batch containing RAW, JPEG, PNG, TIFF, XMP duplicates, a retry, a malformed sidecar, and one corrupt upload; verify only valid objects become active and every input has a deterministic result.

**Acceptance Scenarios**:

1. **Given** a supported image and matching darktable XMP, **When** upload and verification complete, **Then** the library records stable image identity, exact object checksums, logical path, association, size/format, and active integrity state without changing source bytes.
2. **Given** an interrupted upload, **When** the owner resumes it, **Then** already verified data is not duplicated and completion produces one logical asset.
3. **Given** corrupt, unsupported, malformed, or ambiguously associated input, **When** verification runs, **Then** the input is preserved in quarantine where safe, excluded from active assets, and reported with a corrective reason.

---

### User Story 2 - Retrieve and organize stored assets (Priority: P2)

As the library owner, I can download exact originals/sidecars, move or rename logical files without changing identity, and understand when multiple darktable sidecars represent duplicates of one source image.

**Why this priority**: Stable identity and reliable retrieval are prerequisites for synchronization and history.

**Independent Test**: Download and checksum imported bytes, perform folder and case-only moves, register a numbered darktable duplicate sidecar, and verify identity/association remain correct.

**Acceptance Scenarios**:

1. **Given** an active asset, **When** the owner downloads its original or XMP, **Then** the returned bytes match the recorded checksum and declared content identity.
2. **Given** an image moved or renamed within the library, **When** the operation completes, **Then** its stable asset identity and sidecar relationships remain unchanged and the prior logical path is auditable.
3. **Given** darktable duplicate sidecars following supported naming rules, **When** they are imported, **Then** each edit identity is associated with the same source image without conflating its XMP bytes.

---

### User Story 3 - Delete and restore safely (Priority: P3)

As the library owner, I can soft-delete an image or sidecar, see the recovery deadline, and restore it with its identity and relationships intact.

**Why this priority**: Storage management must not turn an ordinary mistake into irreversible loss.

**Independent Test**: Delete an asset, verify normal listings exclude it, restore on day 90, and confirm checksum, path history, sidecar association, and audit state are intact.

**Acceptance Scenarios**:

1. **Given** an active asset, **When** the owner deletes it, **Then** it enters recoverable state for 90 days, normal active access is removed, and the deadline is displayed.
2. **Given** a recoverable asset, **When** the owner restores it before expiry, **Then** the same logical identity and immutable content return to active state and path conflicts require explicit resolution.
3. **Given** an object referenced by a protected or retained state, **When** deletion is requested, **Then** the system preserves the object and reports the blocking reference rather than corrupting it.

### Edge Cases and Error Handling

- The same bytes arrive under different names or the same path receives different bytes; deduplication preserves distinct logical intent without duplicating immutable payload unnecessarily.
- An XMP arrives before its image, has no match, matches multiple candidates, or uses Lightroom naming; it remains unassociated/quarantined until a deterministic owner choice or supported rule applies.
- A darktable duplicate suffix collides or has a gap; identities follow the file's declared duplicate semantics and do not renumber silently.
- Upload completion is delivered twice or after cancellation; idempotency returns the original outcome and never publishes two assets.
- Declared size/hash differs from received bytes; publication fails and active state remains unchanged.
- A move targets an existing case-insensitive path or a Windows-invalid name; the operation reports the collision and offers no silent overwrite.
- Deletion occurs during upload, verification, move, or download; state transitions serialize and return a retryable conflict.
- Storage is unavailable or quota is exceeded; temporary data is not acknowledged as authoritative and the user receives safe retry guidance.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The owner MUST be able to create a resumable batch import containing supported image files, XMP sidecars, or both and see batch/item state and errors.
- **FR-002**: Each received byte stream MUST be verified against declared size and SHA-256 before it becomes authoritative.
- **FR-003**: Original image bytes MUST be immutable after publication; different replacement bytes create a distinct content identity and require an explicit logical-asset decision.
- **FR-004**: Every logical image asset MUST have a stable identifier independent of path, filename, upload session, device, and later branch state.
- **FR-005**: The system MUST preserve each accepted XMP byte stream exactly and associate it with exactly one source asset/edit identity; an unassociated XMP state is quarantined, non-authoritative, and unavailable to versioning, rendering, gallery, or synchronization until deterministic association succeeds.
- **FR-006**: Association MUST recognize the supported darktable base and numbered-duplicate sidecar naming conventions without conflating multiple edits of one source image.
- **FR-007**: The system MUST detect identical content, path collisions, ambiguous associations, corrupt bytes, malformed XMP, unsupported formats, quota excess, and incomplete upload before active publication.
- **FR-008**: Unsafe or unresolved input MUST remain outside active library state while retaining enough safe evidence/content for correction or retry.
- **FR-009**: Publishing a multi-item batch MUST report each item's durable outcome; an operation declared atomic by its caller MUST publish all selected items or none.
- **FR-010**: The owner MUST be able to retrieve original and XMP bytes with checksum and content provenance.
- **FR-011**: Move and rename operations MUST preserve asset/content identity, XMP association, and path history and MUST reject silent overwrite.
- **FR-012**: The owner MUST be able to soft-delete an asset or uncommitted sidecar state and restore it for 90 days with identity and relationships intact.
- **FR-013**: Content referenced by retained/protected history, a legal/operational hold, or another active asset MUST NOT be physically removed by an ordinary delete.
- **FR-014**: All ingest, association, move, deletion, restoration, and integrity decisions MUST produce attributable audit events without logging media/XMP payloads.
- **FR-015**: The system MUST expose active, uploading, verifying, quarantined, failed, recoverable-deleted, restored, and integrity-error states with stable error codes and safe retry behavior.
- **FR-016**: A reconstruction manifest MUST enumerate authoritative object identity, checksum, size, asset association and provenance, path history, lifecycle holds/state, audit-event identifiers, version-control handoff identifiers, and required metadata so feature 011 can verify recovery independently.

### Acceptance Criteria

- **AC-001**: A representative supported batch imports with 100% checksum equality between source, authoritative storage, and downloaded results.
- **AC-002**: Interrupted and duplicate completion requests produce one logical asset and one authoritative content object per unique checksum.
- **AC-003**: Malformed, corrupt, unsupported, and ambiguous inputs never appear as active assets and each returns a stable corrective result.
- **AC-004**: Base and numbered darktable sidecars associate with the intended source asset while retaining distinct byte identities.
- **AC-005**: Moving/renaming 10,000 test assets preserves every asset identifier and association and produces no undetected collision.
- **AC-006**: A day-90 restoration returns the original identity, bytes, associations, and audit history; a conflicting destination requires an explicit alternate path.
- **AC-007**: A fault between object upload and database publication is repaired or rolled back idempotently and never leaves acknowledged but unreachable content.
- **AC-008**: Reconstruction from exported manifests detects every intentionally missing, altered, extra, or mis-associated sampled object.
- **AC-009**: An unassociated XMP fixture remains quarantined and cannot be rendered, committed, searched as active edit metadata, or synchronized until it has exactly one deterministic source association.
- **AC-010**: Reconstruction detects a missing or altered path history, lifecycle hold, audit linkage, association provenance, or version-control handoff identifier in sampled recovery data.

### Key Entities

- **Import batch**: Owner request, item set, atomicity choice, progress, counts, errors, and completion state.
- **Upload session**: Resumable transfer authorization, expected size/hash, expiry, received state, and idempotency key.
- **Content object**: Immutable bytes identified by checksum, size, media category, authoritative/quarantine location, and integrity state.
- **Image asset**: Stable logical photograph identity, current logical path, format, lifecycle, and original content reference.
- **Edit identity**: Base or numbered darktable duplicate associated with one image asset.
- **XMP object**: Exact sidecar content, parse/compatibility state, candidate association, and provenance.
- **Path record**: Current/historical normalized path, display path, collision key, and effective interval.
- **Lifecycle hold**: Reference/retention reason preventing physical removal.
- **Reconstruction manifest**: Checksummed inventory sufficient to verify authoritative storage relationships.

## Non-Functional Requirements

- **NFR-001 Performance**: A 1,000-item batch begins visible progress within 2 seconds; 95% of verification events complete within 60 seconds after upload under the reference workload, excluding transfer time. Verification: batch benchmark.
- **NFR-002 Availability**: Ingest/status/retrieval meets 99.9% monthly availability; acknowledged content remains durable during application restart. Verification: service monitoring and restart tests.
- **NFR-003 Security**: Upload/download authorization is short-lived, owner-scoped, size/hash constrained, encrypted, and cannot address another object's storage location. Verification: authorization and abuse tests.
- **NFR-004 Privacy**: Media/XMP bytes, paths, and geolocation are absent from logs and diagnostics; quarantine is private and access-audited. Verification: log scanning.
- **NFR-005 Accessibility**: Import progress, errors, collisions, deletion, and restore controls conform to WCAG 2.2 AA and do not rely on color alone. Verification: accessibility tests.
- **NFR-006 Compatibility**: Initial accepted formats are a documented representative darktable RAW corpus plus JPEG, PNG, TIFF, and darktable XMP naming/duplicate conventions. Verification: compatibility corpus.
- **NFR-007 Observability**: Metrics cover batch age/rate, bytes, verification failures, quarantine, orphan repair, storage errors, and integrity drift; sustained failures alert within 15 minutes. Verification: fault injection.
- **NFR-008 Maintainability**: Format detection, association rules, lifecycle states, and manifest versions are explicit and regression-tested. Verification: contract review.
- **NFR-009 Testability**: Automated tests cover content/address identity, retries, corruption, association, atomic publication, move collisions, delete/restore, and manifest reconstruction. Verification: CI traceability.
- **NFR-010 Localization**: Paths remain byte-safe and display-safe; messages and dates are locale-aware while identifiers/hashes remain invariant. Verification: Unicode and locale corpus.
- **NFR-011 Deployment**: Schema/storage changes use backward-compatible rollout and do not reinterpret existing content silently. Verification: migration rehearsal.
- **NFR-012 Operations**: Runbooks cover stuck uploads, quarantine review, orphan repair, checksum drift, quota, storage outage, and manifest verification. Verification: operational exercise.
- **NFR-013 Cost**: Import reports stored bytes/object counts; temporary/quarantine data has explicit expiry; duplicate payloads are reused safely. Verification: storage inventory and cost report.
- **NFR-014 Compliance**: Accepted formats and test data have documented provenance/licenses; owner deletion remains subject to approved 90-day and backup lifecycle. Verification: policy review.
- **NFR-015 Reliability**: No acknowledged original/XMP content is lost, altered, or silently mis-associated across retry, cancellation, duplicate delivery, restart, or partial failure. Verification: fault matrix.
- **NFR-016 Scalability**: The model supports 100,000 active images and at least 1,000,000 XMP/content records without violating retrieval/integrity targets. Verification: capacity test.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The owner can import and verify a 1,000-item reference batch with zero silent failures and a deterministic result for every item.
- **SC-002**: 100% of sampled downloaded originals and XMP objects match their source SHA-256.
- **SC-003**: Retry, interruption, and duplicate-delivery tests create zero duplicate logical assets or acknowledged orphan objects.
- **SC-004**: The owner can identify and correct an association/path error in under 2 minutes using the reported state.
- **SC-005**: A reconstruction audit identifies 100% of seeded missing, changed, extra, or mis-associated objects.

## Constraints, Assumptions, and Dependencies

### Constraints

- Original image bytes are immutable; XMP bytes are preserved exactly.
- This feature exposes storage/lifecycle contracts but does not implement version history, rendering, gallery, or Windows placeholders.
- The 90-day recoverability policy comes from the approved umbrella program.

### Assumptions

- Source files remain the owner's responsibility until import reports verified completion.
- Content deduplication is internal and never merges logical asset identity or access/audit records.
- Minimal embedded/XMP fields may be parsed for validation/association; feature 009 owns the searchable metadata model.
- Physical purge is deferred to feature 011 and cannot precede reference/retention and recovery-copy checks.

### Dependencies

- [003 umbrella program](../003-darktable-version-control/spec.md).
- Feature 004 identity, error, audit, telemetry, environment, and CI/CD conventions.
- A representative legally usable format/XMP corpus.

## Risks and Open Questions

- Camera RAW diversity can cause false format confidence; only corpus-verified formats enter the support matrix.
- XMP naming and case behavior differ across filesystems; logical identity must not depend solely on a case-sensitive path.
- Object/database publication is a dual-system boundary; planning must select an idempotent repairable transaction pattern.
- No material product ambiguity remains. Exact upload protocol, storage layout, database schema, checksum enforcement, and quarantine duration are planning decisions.
