# Feature Specification: Darktable Rendering and Preview Provenance

**Feature Branch**: `feature/008-darktable-rendering`  
**Created**: 2026-09-03  
**Status**: Approved for implementation by product-owner request on 2026-09-21
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)

## Problem and Scope

Photographers need trustworthy gallery thumbnails and previews for both the current edit and a selected historical XMP version. A preview must be clearly tied to the exact original image, XMP sidecar, and supported darktable release that produced it.

This feature provides managed, headless darktable rendering, derivative caching, failure reporting, and preview provenance. It depends on feature 004 for the operating foundation, feature 005 for immutable source files, and feature 006 for version identifiers. It does not build gallery screens, change XMP history, edit photos, or provide Windows synchronization.

## User Scenarios & Testing

### User Story 1 - View a current edit preview (Priority: P1)

As a photographer, I can request a thumbnail or larger preview for an imported image so I can browse its current appearance without downloading the original.

**Why this priority**: Browsing is not useful until images have visual representations.

**Independent Test**: Import a supported image and XMP sidecar, request each configured derivative size, and confirm the returned derivative matches a reference render.

**Acceptance Scenarios**:

1. **Given** an image with a current XMP state, **When** a thumbnail is requested, **Then** the service returns a derivative or reports that it is being prepared.
2. **Given** a completed derivative, **When** the same rendition is requested again, **Then** it is returned without a new render and identifies its source version.

### User Story 2 - Inspect a historical version (Priority: P1)

As a photographer, I can select a committed XMP version and see the image as that version rendered it, so I can compare past edits reliably.

**Why this priority**: Historical comparison is central to version control for image edits.

**Independent Test**: Select two committed XMP versions of one image, request a preview for each, and verify that each provenance record names the selected version and correct input hashes.

**Acceptance Scenarios**:

1. **Given** a selected reachable XMP version, **When** I request its preview, **Then** the rendition uses that version rather than the branch head.
2. **Given** an unavailable or incompatible historical input, **When** a preview is requested, **Then** I receive an actionable failure state and no misleading derivative.

### User Story 3 - Understand rendering failures (Priority: P2)

As a photographer, I can see why a preview failed and retry after correcting a source problem, so broken files do not appear silently successful.

**Why this priority**: Clear failures preserve confidence in historical comparisons.

**Independent Test**: Submit malformed sidecar and unsupported-image fixtures, verify their visible failure reasons, correct a fixture, and successfully retry.

**Acceptance Scenarios**:

1. **Given** a failed render, **When** I view its status, **Then** I see a safe reason, attempted input version, and retry availability.
2. **Given** the underlying input is corrected, **When** I retry, **Then** a new attempt is tracked separately and can succeed.

### Edge Cases

- The original, sidecar, or selected revision is missing, malformed, unreadable, or changes identity during preparation.
- A request is duplicated while an identical render is in progress.
- A rendition was made by a darktable release outside the currently supported compatibility matrix.
- A render exceeds its resource or time limit, or its temporary workspace cannot be cleaned up.
- A source image uses unsupported raw-camera data or an unavailable darktable resource.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST support the approved darktable compatibility matrix, initially darktable 5.6.x with 5.6.1 as the reference release and 5.4.x as a regression-compatibility release.
- **FR-002**: The system MUST render only from the immutable original image and the explicitly selected XMP state.
- **FR-003**: The system MUST provide configured thumbnail renditions and an on-demand larger preview rendition for supported image types.
- **FR-004**: The system MUST use darktable's non-interactive rendering capability for all managed renditions.
- **FR-005**: The system MUST retain a provenance record for every completed rendition, including source-image hash, XMP hash or version identifier, darktable release, render profile, and completion time.
- **FR-006**: The system MUST make provenance available wherever a rendition is presented to a user or another feature.
- **FR-007**: The system MUST key reusable derivatives by all render-affecting inputs and MUST not return a derivative whose provenance differs from the request.
- **FR-008**: The system MUST coalesce duplicate pending requests for the same rendition into one observable render attempt.
- **FR-009**: The system MUST keep render work isolated from production metadata and version-history state; rendering MUST not modify an original, XMP object, commit, or branch.
- **FR-010**: The system MUST enforce configurable execution time, memory, storage, and concurrency limits for each render attempt.
- **FR-011**: The system MUST use private temporary input and output areas and remove temporary material after each attempt according to the retention policy.
- **FR-012**: The system MUST surface safe, actionable failure categories and permit an authorized retry without exposing internal paths or credentials.
- **FR-013**: The system MUST mark a rendition unavailable when its selected XMP version or original image is unavailable, rather than substituting a different version.
- **FR-014**: The system MUST record operational events for request, completion, failure, retry, cache use, and compatibility rejection.
- **FR-015**: The system MUST maintain a representative image-and-XMP regression corpus and compare supported-release output against approved references before a release is accepted.
- **FR-016**: The system MUST require an explicit compatibility approval before changing the reference darktable release or render profile used for new derivatives.
- **FR-017**: The system MUST contain untrusted image and XMP rendering so a compromised or malformed input cannot access production metadata, credentials, other libraries, originals, XMP history, or write-capable source storage.

### Key Entities

- **Render request**: A request for one rendition of one image and exact XMP state.
- **Rendition**: A generated thumbnail or preview with a size and render profile.
- **Render attempt**: The observable execution record and its outcome.
- **Preview provenance**: The evidence linking a rendition to its immutable inputs and approved rendering environment.
- **Compatibility matrix**: Approved darktable releases and the regression evidence supporting them.

## Acceptance Criteria

- **AC-001**: A supported image with valid XMP produces each configured thumbnail rendition and an on-demand preview.
- **AC-002**: Selecting a historical XMP version produces a rendition whose provenance names that exact version and its XMP hash.
- **AC-003**: Repeating an identical request reuses the existing valid rendition and preserves its provenance.
- **AC-004**: Concurrent identical requests result in one render attempt visible to every requester.
- **AC-005**: Malformed, missing, unsupported, or incompatible inputs produce a safe failure state without a substitute image.
- **AC-006**: A retry after a correctable failure creates a distinct attempt and can complete successfully.
- **AC-007**: Render execution cannot alter source objects, working changes, commits, or branch references.
- **AC-008**: Every supported-release change passes the approved regression corpus before use for new requests.
- **AC-009**: One RAW fixture with its current XMP state and at least three distinct historical XMP revisions renders with traceable provenance for every rendition.

## Non-Functional Requirements

- **NFR-001 Performance**: For an already available rendition, 95% of requests MUST become viewable within 500 ms under the approved nominal load.
- **NFR-002 Performance**: For a supported image at or below the documented reference size, 95% of uncached preview requests MUST start within 2 seconds and finish within 30 seconds under nominal load.
- **NFR-003 Availability**: Failed render workers MUST not make existing valid renditions unavailable.
- **NFR-004 Reliability**: A completed rendition MUST be atomically associated with its provenance or be treated as unavailable.
- **NFR-005 Security**: Render inputs, outputs, temporary areas, and logs MUST be inaccessible to unauthenticated users and isolated per library.
- **NFR-006 Privacy**: Failure messages and telemetry MUST not disclose local paths, access tokens, or embedded image metadata beyond the authorized library.
- **NFR-007 Compatibility**: Supported inputs and darktable releases MUST be published as a versioned compatibility matrix.
- **NFR-008 Accessibility**: A failed, pending, or complete preview state and its provenance MUST have text alternatives for assistive technologies.
- **NFR-009 Observability**: Operators MUST be able to measure queue age, duration, failure category, retry count, cache reuse, and compatibility version.
- **NFR-010 Maintainability**: Render-profile changes MUST be versioned and traceable to their regression evidence.
- **NFR-011 Testability**: Automated tests MUST cover successful renders, historical versions, duplicate requests, failures, retries, provenance, and resource-limit outcomes.
- **NFR-012 Cost**: Expensive previews MUST be generated on demand; reusable derivatives MUST have an owner-defined retention policy.
- **NFR-013 Operations**: Operators MUST be able to disable new render intake and drain or cancel pending work without corrupting completed derivatives.
- **NFR-014 Recovery**: After restart, in-progress work MUST resolve to a visible retryable or terminal state within 10 minutes.
- **NFR-015 Localization**: User-visible statuses and failure messages MUST be localizable; stored provenance identifiers remain language-neutral.
- **NFR-016 Compliance**: Preview retention and deletion MUST honor the parent program's library deletion and recovery policy.
- **NFR-017 Freshness**: Under the reference 100,000-image workload, 99% of supported images MUST have a usable gallery thumbnail within 5 minutes of completed upload, excluding visible retryable failures.

## Assumptions and Dependencies

- The private single-owner library and protected default-history policy are defined by the parent program.
- Features 004, 005, and 006 provide authenticated operation, immutable source identity, and selected-version resolution respectively.
- Feature 006 is not implemented on the current baseline. Current-edit rendering may proceed through feature 005; historical-version rendering remains blocked until feature 006 supplies its approved selected-version resolution contract. This feature MUST not substitute a branch head or synthetic history while that dependency is unavailable.
- Gallery and lineage interfaces in features 009 and 010 consume this feature's rendition and provenance contract.
- Rendering derivatives are reproducible aids, not authoritative replacements for originals or XMP history.

## Success Criteria

- **SC-001**: A photographer can see a requested supported-image thumbnail in at least 95% of already-rendered cases within 500 ms.
- **SC-002**: A photographer can select either of two committed XMP versions and distinguish their provenance without downloading either original.
- **SC-003**: All malformed or unsupported test fixtures show a clear failure state and never show a derivative from another input.
- **SC-004**: Every approved darktable-release update passes the complete rendering regression corpus before it serves new derivatives.
- **SC-005**: A current XMP state and at least three historical revisions of the same RAW fixture render with distinguishable, traceable provenance.
- **SC-006**: 99% of supported images in the reference workload have usable thumbnails within five minutes of completed upload, excluding visible retryable failures.
