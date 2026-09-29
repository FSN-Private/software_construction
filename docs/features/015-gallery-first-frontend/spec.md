# Feature Specification: Gallery-First Frontend

**Feature Branch**: `feature/015-gallery-first-frontend`

**Created**: 2026-09-29

**Status**: Draft — visual direction approved; requirements awaiting project-owner approval

**Owner**: Project owner

**Parent program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Related features**: [005 Image Ingest and Storage](../005-image-ingest-storage/spec.md), [006 XMP Version-Control Core](../006-xmp-version-control-core/spec.md), [008 Darktable Rendering](../008-darktable-rendering/spec.md), [009 Gallery Metadata Search](../009-gallery-metadata-search/spec.md), [010 Lineage and Merge Interface](../010-lineage-merge-interface/spec.md)

**Input**: “Modern aesthetic, gallery-first. Filters pop out from side. Metadata and version history for images when clicking on them. Ideally it should be simple and clean.” The project owner approved the generated three-view mockup and requested a new frontend feature specification accurately matching it.

**Visual reference**: [Approved mockup](references/approved-mockup.png), with [reference interpretation and measurable visual criteria](references/visual-reference.md).

## Problem Statement, Users, and Scope

The existing private web portal provides gallery, metadata, image lifecycle, and version-control functionality, but presents service information and extensive controls ahead of a cohesive photographic workspace. The owner needs a calm, modern frontend where photographs dominate, filters appear only when needed, and selecting an image reveals its metadata and edit history together.

The primary user is the existing authenticated single library owner. This feature redesigns the existing web experience; it does not establish a second library, a public gallery, or new version-control semantics. Operators retain existing diagnostic access without placing deployment details in the normal photography workflow.

### Goals

- Match the approved mockup's gallery, temporary left filter drawer, and image-detail composition.
- Make browsing, finding an image, and inspecting its metadata/history a connected workflow.
- Preserve existing approved capabilities and safety contracts behind progressive disclosure.
- Provide accessible, responsive presentation using real data and truthful preview states.

### Non-Goals

- Browser RAW editing, adjustment sliders, arbitrary XMP editing, or a darktable replacement.
- New storage, identity, search, rendering, authentication, merge, or retention semantics.
- Public sharing, collaboration, new roles, native mobile apps, dark mode, or a branding exercise.
- Implementing draft export-basket feature 012 or backup feature 011 through this redesign.
- Replacing full lineage graph/table, comparison, merge, recovery, or import workflows with the compact example in the mockup.

### Glossary

- **Gallery**: Paginated photographic contact sheet for the selected library and web branch.
- **Filter drawer**: Temporary panel opened from Filters; fields are drafts until applied.
- **Inspector**: Metadata/history area beside the selected image on desktop; below it on narrow screens.
- **Current version**: Committed image state resolved on the selected branch; distinct from uncommitted working changes.
- **Browse context**: Library, branch, search, applied filters, sort/direction, page, scroll position, and initiating image.
- **Reference fixture**: Deterministic non-private sample library for visual and functional verification.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse a photography-first library (Priority: P1)

As the owner, I see a compact header and an image-rich gallery immediately after sign-in, so I can explore photographs without navigating a technical dashboard.

**Why this priority**: The gallery is the primary surface and largest area of the approved mockup.

**Independent Test**: Sign in to a fixture library containing landscape, portrait, and panorama images; verify header, grid, counts, ratings, pagination, and preview states against the reference criteria.

**Acceptance Scenarios**:

1. **Given** a valid owner session and populated library, **When** the workspace opens, **Then** Gallery is active, the selected branch is visible, and photographs occupy the main content area.
2. **Given** paginated results, **When** sort or page changes, **Then** filenames, ratings, counts, and ordering reflect the selected results without duplicates or skipped records in a stable dataset.
3. **Given** missing/failed thumbnails, **When** the gallery loads, **Then** affected tiles identify their state while available images remain usable.

### User Story 2 - Find images using a side drawer (Priority: P1)

As the owner, I open filters, refine metadata criteria, and apply them without permanently sacrificing gallery space.

**Why this priority**: Temporary side filters are an explicit request and the mockup's second view.

**Independent Test**: Open the drawer, change date/rating/camera/tag criteria, cancel once, apply once, remove a chip, and reset; verify results, counts, focus, and context.

**Acceptance Scenarios**:

1. **Given** a closed drawer, **When** Filters is activated, **Then** a temporary left overlay opens, the gallery remains visible to its right on desktop, and focus enters the drawer.
2. **Given** applied filters, **When** drafts change and Close or Escape dismisses the drawer, **Then** results retain previously applied criteria.
3. **Given** valid drafts, **When** Show photos is activated, **Then** all criteria apply together, pagination starts at the first page, the drawer closes, and chips/counts update.
4. **Given** zero matches or an invalid range, **When** filtering is attempted, **Then** an actionable empty state or inline validation appears rather than a misleading empty library.

### User Story 3 - Inspect metadata and version history together (Priority: P1)

As the owner, I click a photo and see a large preview with metadata and image-specific history, so I can understand capture information and the evolution of edits.

**Why this priority**: This is the explicit image-click behavior and the mockup's third view.

**Independent Test**: Open an image with embedded/XMP metadata, multiple commits, and a working change; select a historical version and return to the same gallery position.

**Acceptance Scenarios**:

1. **Given** a gallery image, **When** its tile is activated, **Then** detail shows the same image, branch, metadata sources, and history summary without fetching the original automatically.
2. **Given** historical versions, **When** one is selected, **Then** the preview/version labels identify that exact state or unavailability; metadata clearly distinguishes capture, selected-version, and separate working values.
3. **Given** a pending historical preview, **When** history is inspected, **Then** textual history remains usable and no different version's image is represented as the selection.
4. **Given** detail or deeper history, **When** Back to gallery or browser Back is used, **Then** image, branch, filters, sort, page, and scroll position are recoverable.

### User Story 4 - Reach existing actions without clutter (Priority: P2)

As the owner, I can still import, download, organize metadata, and open full history tools while everyday browsing stays simple.

**Why this priority**: A visual redesign must not remove approved functionality or weaken confirmations.

**Independent Test**: Execute existing import/download/edit flows and navigate to branch, comparison, merge, and recovery controls; verify existing protections.

**Acceptance Scenarios**:

1. **Given** a valid session, **When** Import is activated, **Then** the existing resumable import workflow is reachable and returning restores gallery context.
2. **Given** a permitted edit, **When** it is saved, **Then** it is visibly an uncommitted branch working change; original image and committed history are unchanged.
3. **Given** dirty changes or device conflicts, **When** another branch is selected, **Then** existing block/preserve/confirmed-discard rules apply.
4. **Given** a selected image, **When** full history or comparison opens, **Then** exact-version selection, ancestry, provenance, and mutation confirmations remain available.

### Edge Cases

- Empty library versus zero results; all previews pending versus partial failure.
- Missing/cleared metadata, conflicting embedded/XMP values, hidden location, long names/tags, portrait/panorama framing.
- Invalid ranges, timezone boundaries, unknown facet values, stale counts, out-of-order search responses.
- Direct links, reload, deleted/unavailable assets, stale page cursors, interrupted navigation.
- No commits, repeated image states, merges/branches, hidden ancestry, displaced history, unavailable historical previews.
- Expired sessions with drafts, concurrent saves, moved branch heads, staged changes, device conflicts.
- Narrow screens, zoom, reduced motion, keyboard-only use, long histories.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Gallery MUST be the authenticated default workspace. A compact header MUST show Library identity, Gallery/History destinations, selected web branch, Import, and owner/session controls. Signed-out users MUST see sign-in without private content; deployment/service diagnostics MUST not precede ordinary browsing.
- **FR-002**: The frontend MUST follow the saved visual reference: warm neutral background, charcoal text, restrained olive emphasis, image-dominant contact sheet, thin dividers, modest control rounding, and no permanent wide sidebar. It MUST satisfy the visual-reference criteria; sample photographs/metadata are not production content.
- **FR-003**: Gallery MUST show actual branch/library results, filenames/ratings, selected/focus state, real result count/range, and accessible stable pagination. Thumbnails MUST preserve proportions without stretching and follow a consistent contact-sheet rhythm. Thumbnail framing MUST not modify the underlying image.
- **FR-004**: The toolbar MUST provide labelled search using existing filename/path, title, and description semantics; Filters; supported capture/import date, filename, rating, and dimensions sorts/directions; and at least two working density choices. Default sort is capture date descending. Density MUST affect presentation only, preserving results/order and browse context.
- **FR-005**: Filters MUST open as a temporary left drawer with heading/close control. Close, Escape, or backdrop dismissal MUST cancel unapplied changes and restore focus to the opener. Keyboard focus MUST remain inside while open; obscured background controls MUST not be operable.
- **FR-006**: All feature 009 facets MUST remain available: capture/import date, filename/path, type, camera, lens, exposure, dimensions, orientation, rating, rejection, color label, tags, title, description, creator, and authorized location criteria. Common date/rating/camera/type/tag/color controls MUST follow the mockup; other facets MUST be discoverable in labelled expandable groups. Preserve existing multi-value/composition semantics and explain ambiguity.
- **FR-007**: Filter edits MUST remain drafts until explicit application. Reset MUST clear drafts; applying that draft MUST clear applied filters. Invalid ranges MUST prevent application with field guidance. Applied filters MUST have removable labelled chips; removal MUST change only that criterion. Applying/removing filters or changing search/sort MUST reset pagination and show authoritative results. Show count MUST refer to the exact current draft; otherwise use Show photos without a speculative count.
- **FR-008**: Recoverable browse context MUST survive detail, full history, comparison, import, and Back/Forward. Applied search/filter/sort/page/branch MUST survive reload using existing owner direct-link semantics. Restore valid scroll/initiating-image context; invalid/deleted destinations MUST offer return to retained results.
- **FR-009**: Tile activation MUST open a dedicated detail workspace with Back to gallery, close, filename/title, branch, large contained preview, and right inspector on desktop. Close MUST return to gallery context. Metadata followed by compact image history MUST appear within the same detail workspace.
- **FR-010**: Detail MUST show available camera, lens, capture time, exposure, dimensions, identity/type, rating, tags, title/description, creator, and permitted location. Identify embedded/XMP sources, distinguish missing/cleared values, respect location privacy, and progressively disclose less-used fields. Labels/units MUST be understandable without raw internal records as the primary UI.
- **FR-011**: The history summary MUST show actual image-affecting committed states on the selected branch, exact commit identities available, messages, timestamps, current marker, and applicable branch/tag labels. Explain the image projection of library-wide commits, disclose hidden/collapsed ancestry and additional history, and expose full history. A linear-looking summary MUST not imply branched/merged ancestry is linear. No-history images MUST show an accurate empty state.
- **FR-012**: Selecting history MUST identify the exact committed state and bind displayed renditions to that image/commit/XMP with provenance. Selected-version metadata MUST be separate from current-working metadata; missing historical metadata MUST not be silently replaced. Pending/failed/unavailable previews MUST have explicit states and MUST not block textual inspection.
- **FR-013**: Detail MUST expose Compare versions/full image history through existing feature 010 selection flows, retaining image, branch, applicable selected version, and return context. Global History MUST indicate library-wide scope; detail history MUST be image-specific.
- **FR-014**: Existing import, verified original download, explicit full-preview request, move/rename, recoverable delete/restore, and permitted organizational edits MUST remain reachable through contextual actions/dedicated views. Preserve progress, validation, deadlines, and safeguards. Metadata edits MUST remain visibly uncommitted and separate from committed history.
- **FR-015**: Branch control/deeper history MUST preserve existing stage/commit, branch/tag, merge/review, hide/reveal, restore/revert, protected-ref, rewrite/recovery, and recent-authentication safeguards. The redesign MUST not automatically commit, switch through conflicts, discard changes, rewrite history, or skip confirmations.
- **FR-016**: Screens MUST provide concise loading, empty, error, session-expiry, and retry states. Stale/out-of-order responses MUST not replace newer selections; failed/cancelled actions MUST not appear complete. Unsaved metadata MUST retain existing safe navigation/stale-save protections; session expiry MUST not silently lose drafts or resubmit mutations.
- **FR-017**: On narrow screens detail MUST stack preview/inspector; the drawer MAY fill the viewport. All controls MUST remain reachable. Keyboard users MUST complete browse/filter/image/version/return/comparison flows with usable focus, names, landmarks, announcements, and text equivalents for color state.
- **FR-018**: Ordinary browsing MUST use thumbnails/metadata. Original downloads, full previews, and new historical renders MUST require explicit owner action; an already available authorized rendition may be displayed. Do not eagerly fetch originals/full previews/all history images. Owner controls MUST offer sign-out and MUST not invent a profile photo if none exists.

### Key Entities *(include if feature involves data)*

- **Gallery result**: Existing asset, branch context, capture/import information, filename/rating, and thumbnail state in a paginated match.
- **Applied browse state**: Search, combined filters, order, page, branch, and recoverable navigation position.
- **Filter draft**: Temporary unapplied values discarded on cancellation.
- **Inspector state**: Asset, selected committed/current state, metadata/source/provenance labels, and separate working-change indication.
- **History summary**: Image projection of authoritative commits, ancestry disclosure, and full-history access.
- **Visual reference**: Approved mockup/interpretation, not a production photo collection or new data model.

### Acceptance Criteria and Requirement Coverage

| ID | Observable acceptance and validation | Requirements |
|---|---|---|
| AC-001 | Sign-in lands on Gallery; signed-out/expired fixtures expose no private content; header destinations work; diagnostics are secondary. | FR-001, FR-018 |
| AC-002 | All three deterministic reference screenshots satisfy visual criteria at 1440 × 960 and 1280 × 800, including image area, palette/spacing, and desktop split. | FR-002, FR-003, FR-009 |
| AC-003 | Stable pagination has no duplicates/omissions; all sorts and two density settings retain correct results/count/range. Search locates known filename/path, title, and description fixtures using existing semantics. | FR-003, FR-004 |
| AC-004 | Open/traverse/close/Escape/backdrop/cancel/reset/apply/remove-chip/invalid-range cases produce specified state and focus outcomes. | FR-005, FR-007, FR-017 |
| AC-005 | Every existing facet remains reachable and verified, with at least three combined-filter cases; stale draft counts never label a newer draft authoritative. | FR-006, FR-007 |
| AC-006 | Gallery→detail→history/comparison→gallery and gallery→import→gallery restore asset, branch, filters, sort, page, scroll. Reload/Back/Forward restore applied state; deleted links return safely. | FR-008, FR-013 |
| AC-007 | Landscape/portrait/panorama detail shows the correct contained preview, desktop split/narrow stack, metadata sources, missing/cleared distinctions, and location privacy. | FR-009, FR-010, FR-017 |
| AC-008 | No-history, linear, merged, hidden, and repeated-state fixtures show truthful summaries/projection disclosure and full graph/table access with exact identities. | FR-011, FR-013 |
| AC-009 | Two selected historical states yield their exact preview/provenance or labelled unavailability. Current-working/selected-version metadata is never interchangeable; rendering failure leaves text usable. | FR-012 |
| AC-010 | Existing import/download/move/delete/restore/edit checks pass through redesigned entry points. Saves stay uncommitted; original bytes/commits do not change outside explicit existing operations. | FR-014 |
| AC-011 | Dirty-state/device-conflict, stale-head, protected-ref, final merge-review, hide/reveal, recovery, and recent-authentication cases retain safeguards; no unconfirmed destructive action succeeds. | FR-015 |
| AC-012 | Empty/zero-match/partial-failure/out-of-order/session-expiry/unsaved/stale-edit cases show truthful recoverable states without unintended mutations or lost drafts. | FR-016 |
| AC-013 | Keyboard/assistive-technology primary journeys and narrow/zoom/reduced-motion/focus/contrast checks satisfy NFR-006–007. | FR-005, FR-017 |
| AC-014 | Normal gallery/detail/history-summary navigation fetches no original and starts no automatic full/historical render. Sign-out clears private UI and returns to sign-in. | FR-018 |

### Non-Functional Requirements

| ID | Category and target | Verification |
|---|---|---|
| NFR-001 | Performance: preserve feature 009's 100,000-asset nominal-load targets: 95% of initial filtered pages usable within 2 seconds; 95% of cached page thumbnails viewable within 500 ms. | Existing capacity exercise through redesigned UI. |
| NFR-002 | Interaction: drawer response within 200 ms at p95 with cached data on the reference desktop; preserve feature 010's p95 2-second usability for 500-version histories. | Interaction/history measurements, including unavailable rendering. |
| NFR-003 | Availability: partial preview/render-service failure never blocks authorized metadata/filter/history navigation. | Failure injection across all three states. |
| NFR-004 | Security: retain authenticated-owner authorization and all confirmation/state/recent-authentication checks. | Unauthorized/session-expiry and mutation regression fixtures. |
| NFR-005 | Privacy: hidden location absent from cards/chips/suggestions/inspector/unauthorized pages. Telemetry excludes private filenames, searches, tags, location, and bytes; sign-out clears private UI. | Privacy fixtures and telemetry/sign-out review. |
| NFR-006 | Accessibility: primary journeys meet repository WCAG 2.2 AA: 4.5:1 normal-text and 3:1 large-text/required non-text contrast, names, focus, keyboard alternatives, status/error announcements, target sizing. Respect reduced motion. | Automated checks plus documented keyboard/screen-reader/contrast/motion review. |
| NFR-007 | Compatibility: support documented current desktop browsers. Match desktop at 1440 × 960/1280 × 800; controls reachable at 768 × 1024/390 × 844. At 320 CSS pixels and 200% text zoom, no horizontal page scrolling or lost functionality. | Browser/viewport/reflow/zoom matrix; planning records exact tested versions. |
| NFR-008 | Reliability: 100% of scripted navigation/stale-response cases preserve intended context; zero cases misidentify preview versions. | Deterministic concurrency/navigation/provenance fixtures. |
| NFR-009 | Observability: retain browse/search/preview/edit-failure and mutation audit visibility; errors provide safe operation references where available, not private payloads/infrastructure dumps. | Error/audit/telemetry exercises. |
| NFR-010 | Maintainability: all primary views use one documented color/type/spacing/control/focus/state vocabulary with consistent meanings. | Visual and frontend documentation review. |
| NFR-011 | Testability: deterministic coverage for all AC cases, three screenshots, retained facets, unavailable/stale/privacy/accessibility states. Map every FR/AC/NFR/SC to numbered implementation/verification tasks before coding. | Traceability/test-plan review in tasks/analyze phases. |
| NFR-012 | Localization: initial labels English; dates/numbers/exposure units/timezones follow existing metadata rules. 30% label expansion causes no overlap or lost controls and never changes stored metadata. | Locale/timezone fixtures and expanded-label screenshots. |
| NFR-013 | Deployment: rollout/rollback preserves source/XMP/commits/refs/working changes and existing owner links. No migration intended; a required contract change must be specified/approved before coding. | Pre-production rollout/rollback/direct-link checks. |
| NFR-014 | Operations: existing service/release/operation diagnostics remain in a secondary destination. Document frontend rollback and preview/session failure paths with actionable retry guidance. | Guide review and failure exercises. |
| NFR-015 | Cost: no automatic original/full/historical rendering for browsing, no required paid third-party service, no additional infrastructure service. | AC-014 network/render evidence and dependency/infrastructure diff review. |
| NFR-016 | Compliance: preserve parent-program audit/privacy/deletion/90-day recovery rules and actual retention deadlines. | Lifecycle/recovery/audit regression fixtures. |

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All three deterministic views satisfy AC-002; the owner reviews the implemented views beside the saved approved reference before visual acceptance.
- **SC-002**: The owner completes a scripted filter-by-rating-and-camera/tag → image → capture metadata/history → return journey in under two minutes without opening diagnostics.
- **SC-003**: Every AC-006 navigation run recovers valid browse context; every AC-009 preview run identifies exact selected state or explicit unavailability.
- **SC-004**: All scoped existing feature 005/009/010 regression scenarios pass, including supported facets and uncommitted-edit/destructive-action safeguards.
- **SC-005**: Primary journeys complete by keyboard and supported screen-reader walkthrough with zero unresolved blocking accessibility findings.
- **SC-006**: Gallery retains NFR-001 performance and every normal-browsing trace meets AC-014 fetch/render limits.

## Assumptions

- Mockup approval applies to visual direction. This new requirements spec is a draft, not retroactively approved implementation authority.
- Redesign integrates the existing frontend/services; existing approved contracts take precedence over illustrative omissions.
- Light appearance and desktop-first composition are approved direction; responsive reflow is required, native mobile apps/second theme excluded.
- Draft-and-apply filters, capture-date-descending default, and progressive disclosure are reasonable defaults for behavior absent from the raster reference.
- At least two density choices change presentation; cross-device appearance-preference synchronization is outside scope.
- Photos, filenames, avatar, counts, dates, camera data, three-entry history, and presentation captions are illustrative; production uses actual data and complete history.

## Dependencies, Constraints, Risks, and Open Questions

- 005 owns identity/integrity/lifecycle; 006 owns commits/refs/working changes; 008 owns renditions/provenance; 009 owns metadata/filter/sort; 010 owns lineage/comparison/branch/mutation protections. Reuse authoritative contracts.
- Draft feature 012 is separate and not a prerequisite; this feature does not create an export basket.
- **Risk**: Compact timelines hide ancestry; FR-011 requires disclosure/full history. Low-contrast neutrals undermine accessibility; NFR-006 overrides exact illustrated secondary colors.
- **Risk**: All facets at once recreate clutter; FR-006 requires advanced groups without capability loss. Eager previews add cost; FR-018/NFR-015 forbid that.
- **Risk**: Existing count/search/historical-metadata contracts may lack illustrated conveniences. Planning must verify capabilities, provide truthful fallbacks, and seek approval for material contract changes before coding.
- **Open questions**: None block specification. Planning must record exact layouts, tested browser/device/load baseline, filter grouping, and compatibility strategy; material changes require owner approval.

## Approval and Delivery Boundary

This request completes specification only: `spec.md`, requirements-quality checklist, and durable visual reference. No implementation, infrastructure change, deployment, or push is part of this phase. Ready for clarification if assumptions change, otherwise technical planning. `plan.md`, the sole numbered execution source `tasks.md`, and requirement-to-task/test traceability must be generated and approved before implementation under the repository constitution.
