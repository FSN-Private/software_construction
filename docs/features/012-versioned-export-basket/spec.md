# Feature Specification: Versioned Export Basket

**Feature Branch**: `feature/012-versioned-export-basket`
**Created**: 2026-09-03
**Status**: Draft — awaiting product-owner approval
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)

## Problem and Scope

The owner needs to collect gallery images, pin the current or a historical XMP version of each, and export the exact rendered selections together. This feature provides an owner-private basket, exact-version selection, preparation, download, and audit. It depends on 006 version identity, 008 rendering, and 009 gallery; it excludes public sharing, original/XMP bulk download, and editing or committing XMP.

## User Scenarios & Testing

### User Story 1 - Collect export selections (Priority: P1)

As an owner, I can add/remove images from gallery views to a persistent basket and see selected version, count, and readiness.

**Independent Test**: Add, remove, and re-add fixture assets from filtered views and verify unique pinned selections persist.

### User Story 2 - Export selected revisions (Priority: P1)

As an owner, I can choose the current or any eligible historical version of an image and export exactly that rendering.

**Independent Test**: Export a fixture's current plus three historical versions and verify every result's provenance.

### User Story 3 - Retrieve a prepared package (Priority: P2)

As an owner, I can review a mixed-result export and download the completed results privately.

**Independent Test**: Include an unavailable rendition and verify completed and failed items are explicit.

### Edge Cases

- A selected revision becomes unavailable, hidden, or displaced after basket selection.
- The same asset is selected at multiple versions; the branch head moves after selection.
- Preparation fails, is cancelled, expires, or has interrupted download.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST let the authenticated owner add, remove, inspect, clear, and persist a private export basket across sessions.
- **FR-002**: Each basket item MUST pin one immutable asset and explicit current or eligible historical XMP version at selection time.
- **FR-003**: The system MUST allow the same asset at different versions only when the owner explicitly retains both entries.
- **FR-004**: The system MUST show selected version, preview/provenance, output name, and readiness or failure state per basket item.
- **FR-005**: The system MUST prepare exports from the exact pinned asset/version pair and never substitute a newer branch head.
- **FR-006**: The system MUST request required renditions through feature 008 and report pending, failed, unavailable, and completed items individually.
- **FR-007**: The system MUST let the owner choose documented export presets and show resulting format, dimensions, and metadata treatment before preparation.
- **FR-008**: The system MUST create one private downloadable package or an explicit per-item failure summary for a basket snapshot.
- **FR-009**: The system MUST use collision-free understandable output names and include a machine-readable manifest mapping every output to asset, selected version, and provenance.
- **FR-010**: The system MUST permit cancellation before completion and expire prepared downloads after the documented retention interval.
- **FR-011**: The system MUST audit basket changes, export requests, cancellation, completion, download, expiry, and failure without media/XMP payloads.
- **FR-012**: The system MUST prevent unauthorized inspection, preparation, or download of baskets, exports, or manifests.

### Key Entities

- **Export basket**: Owner-private collection of pinned asset/version entries.
- **Basket item**: Requested rendition of a selected immutable asset/version pair.
- **Export request**: Bounded request applying one preset to a basket snapshot.
- **Export manifest**: Evidence mapping each output to its asset, version, and provenance.

## Acceptance Criteria

- **AC-001**: Adding/removing from multiple gallery views retains an accurate persistent basket.
- **AC-002**: Current plus three historical versions of one RAW fixture export only their selected versions, verified by provenance.
- **AC-003**: Moving a branch head after selection does not alter an existing basket item.
- **AC-004**: Mixed export reports every completed, pending, unavailable, and failed item without omission or substitution.
- **AC-005**: Each package manifest maps output filename to exact asset/version/provenance.
- **AC-006**: Unauthorized requests cannot inspect, prepare, or download another owner's export.
- **AC-007**: Cancellation and expiry remove download access while retaining required audit evidence.

## Non-Functional Requirements

- **NFR-001 Performance**: A 100-item basket MUST become usable within 2 seconds for 95% of nominal requests.
- **NFR-002 Performance**: A completed-export download MUST begin within 2 seconds for 95% of nominal requests.
- **NFR-003 Reliability**: Export snapshot and manifest MUST be internally consistent or unavailable; partial success is explicit per item.
- **NFR-004 Security**: Downloads MUST be owner-authorized, time-limited, and inaccessible through guessable locations.
- **NFR-005 Privacy**: Basket contents, names, and manifests MUST not appear in telemetry payloads.
- **NFR-006 Accessibility**: Selection, version choice, progress, errors, cancellation, and download MUST be keyboard-operable with text alternatives.
- **NFR-007 Observability**: Operators MUST measure basket size, preparation, render/download failure, expiry, and cancellation without sensitive payloads.
- **NFR-008 Compatibility**: Exports MUST use formats and presets supported by the rendering compatibility matrix.
- **NFR-009 Testability**: Automated tests MUST cover pins, duplicates, head movement, failures, manifest, authorization, cancellation, and expiry.
- **NFR-010 Cost**: Prepared exports MUST have bounded retention and avoid unnecessary duplicate derivatives.
- **NFR-011 Operations**: Operators MUST be able to pause new preparation and safely drain or cancel pending requests.
- **NFR-012 Localization**: Labels/statuses are localizable; identifiers/manifests remain locale-neutral.
- **NFR-013 Compliance**: Export retention/deletion MUST honor the parent recovery policy.
- **NFR-014 Availability**: Completed exports remain downloadable during unrelated gallery-search degradation.
- **NFR-015 Maintainability**: Export presets and manifest format MUST be versioned and documented.
- **NFR-016 Recovery**: Restart resolves in-progress exports to a visible retryable, cancelled, or terminal state within 10 minutes.

## Assumptions and Dependencies

- Initial scope is private owner-only export; sharing and public links are excluded.
- Prepared exports expire after 24 hours unless a later approved policy changes that default.

## Success Criteria

- **SC-001**: A photographer can create a 100-item basket and start preparation within two minutes.
- **SC-002**: Every output in a historical-version fixture export is traceable to its selected version, with zero substitutions.
- **SC-003**: 95% of 100-item basket views become usable within two seconds under nominal load.
- **SC-004**: Authorization, cancellation, expiry, and mixed-failure fixtures produce the documented result and audit evidence.
