# Feature Specification: Gallery Metadata Search

**Feature Branch**: `feature/009-gallery-metadata-search`  
**Created**: 2026-09-03  
**Status**: Draft — awaiting product-owner approval  
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)

## Problem and Scope

Photographers need one private web gallery in which they can browse an entire library and find images using the metadata they already use in darktable. They must also be able to make the limited metadata edits previously agreed for the web, as uncommitted XMP working changes.

This feature delivers gallery browsing, metadata filters and sorts, asset details, and limited working-change edits. It consumes source identity (005), version working state (006), and preview renditions (008). It excludes original-image editing, commit/branch/merge controls, lineage visualization, rendering execution, and Windows synchronization.

## User Scenarios & Testing

### User Story 1 - Browse and find images (Priority: P1)

As a photographer, I can browse a thumbnail gallery and combine darktable-relevant metadata filters and sorting to locate an image.

**Independent Test**: Import a fixture library with varied metadata, apply each supported filter and sort in combination, and verify that every displayed asset matches.

**Acceptance Scenarios**:

1. **Given** a populated library, **When** I browse pages sorted by capture date, **Then** images are ordered consistently and no image repeats or disappears between adjacent pages.
2. **Given** matching metadata, **When** I combine filters, **Then** only assets satisfying every selected criterion appear.

### User Story 2 - Understand an asset (Priority: P1)

As a photographer, I can open an asset and see its image metadata, current working-state indicator, and preview provenance so I can decide what to do next.

**Independent Test**: Open fixtures with embedded-only and XMP-supplied values and confirm values and their source are accurately shown.

### User Story 3 - Make a metadata working change (Priority: P2)

As a photographer, I can change rating, rejection, color label, tags, title, description, geolocation, or creator details from the gallery, knowing the change is uncommitted until I create a commit elsewhere.

**Independent Test**: Edit each permitted field, reopen the asset, inspect working state, and verify an eventual version-control commit includes the XMP change.

### Edge Cases

- Metadata is absent, malformed, conflict-marked, or differs between embedded data and XMP.
- Geolocation is missing or deliberately hidden by the photographer.
- A filter changes while the user is traversing pages, or an asset is moved/deleted/restored.
- A requested thumbnail is pending or failed.
- An edit conflicts with a newer working state or a protected/history operation.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST present a paginated gallery of library assets with a stable ordering.
- **FR-002**: The system MUST show thumbnail availability, pending state, or failure state without substituting another asset's preview.
- **FR-003**: The system MUST support filtering by capture date, import date, filename or path, file type, camera, lens, exposure values, dimensions, orientation, rating, rejection, color label, tags, title, description, creator, and geolocation presence or location.
- **FR-004**: The system MUST support sorting by capture date, import date, filename, rating, and file dimensions in ascending or descending order where meaningful.
- **FR-005**: The system MUST allow multiple filters to be combined and show the active filters and result count.
- **FR-006**: The system MUST show an asset's identity, source metadata, current XMP working-state indicator, and preview provenance on its detail view.
- **FR-007**: The system MUST distinguish missing metadata from an intentionally cleared value.
- **FR-008**: The system MUST allow the owner to edit only rating, rejection, color label, tags, title, description, geolocation, and creator metadata.
- **FR-009**: The system MUST record every permitted web edit as an uncommitted XMP working change and MUST not change the original image.
- **FR-010**: The system MUST show that a web edit is uncommitted and identify the working branch/device context supplied by version control.
- **FR-011**: The system MUST prevent or safely resolve edits when the target working state changes before saving.
- **FR-012**: The system MUST not expose unsupported darktable editing controls as if they were editable in the gallery.
- **FR-013**: The system MUST permit the owner to suppress geolocation from normal gallery presentation without deleting it from XMP.
- **FR-014**: The system MUST preserve active browse context when opening and returning from an asset detail view.
- **FR-015**: The system MUST make metadata-source precedence and the timestamp used for sorting visible when a value is ambiguous.
- **FR-016**: The system MUST record authorized metadata edits and gallery-access failures in an audit trail.

### Key Entities

- **Gallery asset**: An image record displayed with current metadata and rendition state.
- **Metadata facet**: A searchable or sortable metadata field and its normalized value.
- **Working change**: An uncommitted XMP change in a specific branch/device context.
- **Browse context**: Active filters, sort, page position, and geolocation-visibility preference.

## Acceptance Criteria

- **AC-001**: A fixture library can be filtered by every supported facet and the results satisfy all combined filters.
- **AC-002**: Paging a stable result set produces no duplicate or skipped assets.
- **AC-003**: The asset detail view identifies whether important metadata originated in XMP or embedded image data.
- **AC-004**: Each permitted metadata field can be changed as a working change and appears as uncommitted.
- **AC-005**: A gallery edit never changes the original image binary.
- **AC-006**: A concurrent working-state change produces a clear refresh, retry, or conflict outcome rather than silently overwriting it.
- **AC-007**: Hidden geolocation is absent from normal gallery cards and search results while retained data remains recoverable to the owner.
- **AC-008**: Pending and failed previews are accurately labelled and accessible.

## Non-Functional Requirements

- **NFR-001 Performance**: For a 100,000-asset library under nominal load, 95% of initial filtered result pages MUST become usable within 2 seconds.
- **NFR-002 Performance**: 95% of cached thumbnails on a result page MUST become viewable within 500 ms.
- **NFR-003 Availability**: Gallery browsing MUST remain usable when a subset of preview derivatives is pending or failed.
- **NFR-004 Reliability**: Metadata filters and sort order MUST be repeatable for the same data and browse context.
- **NFR-005 Security**: Only the authenticated library owner may browse, search, inspect, or edit library metadata.
- **NFR-006 Privacy**: Geolocation visibility MUST default to the owner's explicit preference and never be exposed in unauthenticated content.
- **NFR-007 Accessibility**: Filtering, sorting, pagination, edit validation, and preview state MUST be operable with keyboard and exposed with text alternatives.
- **NFR-008 Compatibility**: The web experience MUST support the project's documented current desktop browser baseline.
- **NFR-009 Observability**: Operators MUST measure browse latency, search errors, preview state, edit conflicts, and facet use without recording sensitive values unnecessarily.
- **NFR-010 Maintainability**: Every searchable facet MUST have a documented source and normalization rule.
- **NFR-011 Testability**: Automated tests MUST cover all facets, combined filters, stable pagination, source precedence, permitted edits, conflicts, and privacy settings.
- **NFR-012 Localization**: Labels, validation messages, dates, and numeric exposure values MUST be localizable without changing stored metadata.
- **NFR-013 Operations**: Metadata indexing or refresh work MUST expose progress and recover safely from interruption.
- **NFR-014 Cost**: Normal browsing MUST use thumbnails and metadata; originals and full previews are retrieved only on explicit request.
- **NFR-015 Compliance**: Audit records and metadata retention MUST follow the parent program's deletion and recovery policy.
- **NFR-016 Recovery**: An interrupted metadata refresh MUST not expose a mixed, misleading asset record as complete.

## Assumptions and Dependencies

- The first release is a private single-owner library; sharing and public galleries are excluded.
- 005 supplies image identities and metadata extraction, 006 supplies working changes, and 008 supplies previews and provenance.
- The owner retains final commit, branch, merge, and history-rewrite actions in feature 010.

## Success Criteria

- **SC-001**: A photographer can locate a fixture image using any documented combination of metadata filters in under two minutes.
- **SC-002**: At least 95% of initial gallery pages for the reference 100,000-asset library become usable within two seconds under nominal load.
- **SC-003**: All permitted web edits are visibly uncommitted until an explicit version-control commit occurs.
- **SC-004**: No test that hides geolocation exposes it on normal gallery cards or filtered results.
