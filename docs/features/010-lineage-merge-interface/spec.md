# Feature Specification: Lineage and Merge Interface

**Feature Branch**: `feature/010-lineage-merge-interface`
**Created**: 2026-09-03
**Updated**: 2026-09-26
**Status**: Approved by project owner on 2026-09-26
**Owner**: Project owner
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)

## Problem and Scope

The photographer needs to understand how an image's edits evolved, compare selected XMP states visually, and safely bring work from one branch into another. Raw commit lists and opaque XMP conflicts do not meet that need.

This feature provides the version-control interface: lineage views, version comparison, merge preparation and resolution, branch/ref actions exposed to the owner, and presentation-only history hiding. The version engine remains feature 006's authority; rendering remains feature 008's authority. This feature does not edit original images, implement filesystem synchronization, or permit arbitrary XMP source editing.

The existing gallery (feature 009) is the entry point for an image's history and holds branch-specific uncommitted web metadata changes. The interface must keep that image and branch context visible while the owner moves between gallery, lineage, comparison, and history actions. This feature does not extend gallery search or change which metadata fields the gallery may edit.

### Goals and boundaries

- Let the owner find an image version, understand its ancestry, compare exact committed states, and complete a safe, reviewable history action.
- Keep library-wide commits and references authoritative even when a view is filtered to one image.
- Keep pending or failed previews separate from version-control truth; a preview failure must not erase textual history or differences.
- Exclude original-image edits, new Windows synchronization behavior, rendering execution, arbitrary XMP editing, and export-basket behavior.

**Terms**: A *version* is an immutable library commit; an *image state* is the selected image's content at that commit; *working changes* are uncommitted and belong to a branch and client context; a *hidden version* remains reachable but is collapsed in normal presentation; a *displaced version* is no longer reachable from its former branch after a permitted ref move but may remain recoverable.

## User Scenarios & Testing

### User Story 1 - Inspect image lineage (Priority: P1)

As a photographer, I can select an image and understand its branch and version lineage, including hidden-version status, so I can find the edit I need.

**Independent Test**: Create branching and merge fixtures, hide a non-head version, and verify the graph and accessible textual alternative both represent reachable lineage accurately.

**Acceptance Scenarios**:

1. **Given** an image with branch and merge history, **When** I open lineage, **Then** I can identify versions, parents, branch labels, commit metadata, and current selection.
2. **Given** a hidden version, **When** normal history is shown, **Then** it is omitted without altering stored history; I can opt in to reveal it.
3. **Given** an image opened from a filtered gallery on a selected branch, **When** I open and leave its lineage, **Then** the image, branch, and gallery browse context remain identifiable.

### User Story 2 - Compare edits (Priority: P1)

As a photographer, I can select two versions and inspect their renderings and meaningful XMP differences before deciding whether to merge.

**Independent Test**: Compare fixture versions with known visual and metadata changes and verify both inputs, differences, and preview provenance.

**Acceptance Scenarios**:

1. **Given** two committed image states, **When** I compare them, **Then** I see their exact commit and XMP identities, shared ancestor when present, differences, and each preview's state and provenance.
2. **Given** an unavailable preview, **When** I compare, **Then** I can still inspect the exact identities and textual differences without seeing a substitute image.

### User Story 3 - Merge safely (Priority: P1)

As a photographer, I can merge a source branch into a target branch, resolve conflicts field by field when needed, and create the resulting commit deliberately.

**Independent Test**: Exercise clean merge, recognized-field conflict, unknown-module conflict, protected-default rejection, and explicit non-default rewrite confirmation.

**Acceptance Scenarios**:

1. **Given** a source and target with safe disjoint changes, **When** I prepare a merge, **Then** I see the complete proposed library result and must confirm before a commit exists.
2. **Given** a conflicting edit or changed source or target head, **When** I submit, **Then** no merge commit is created and I receive the conflict or refresh path.

### Edge Cases

- A selected version is hidden, unreachable after a permitted rewrite, or within the 90-day recovery window.
- Two versions have missing previews, incompatible renders, or no common ancestor.
- XMP includes unknown modules, masks, history entries, or simultaneous changes to the same recognized field.
- Branch heads move during comparison, merge preparation, or submission.
- A graph is too large for visual rendering or the user relies on keyboard/screen-reader interaction.
- The selected image has no change in a library-wide commit, a branch has no commits yet, or a compare input is a different image or library.
- A gallery web working change or device conflict exists while the owner commits, switches branches, or prepares a merge.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST present image-focused lineage with exact commit IDs, parent relationships, branch and tag labels, commit metadata, and selected-version context; it MUST distinguish the filtered image view from the library-wide commit history.
- **FR-002**: The system MUST offer an equivalent searchable textual or tabular lineage view.
- **FR-003**: The system MUST permit comparison of two selected committed image states that identifies both commit and XMP identities, their common ancestor when one exists, and their XMP differences; absent image states and no-common-ancestor cases MUST be explicit.
- **FR-004**: The system MUST request and display selected-version previews with their provenance and accurately show pending, unavailable, or failed states.
- **FR-005**: The system MUST let the owner create a branch from an eligible selected version and name it under the version-control naming rules.
- **FR-006**: The system MUST let the owner create an explicit commit from eligible working changes with a required message.
- **FR-007**: The system MUST provide merge preparation that identifies the source and target branches, both expected heads, common ancestor or its absence, and all affected images before submission.
- **FR-008**: The system MUST automatically merge only disjoint changes to recognized metadata fields; all simultaneous scalar changes, unknown modules, masks, and history changes MUST require explicit resolution.
- **FR-009**: The system MUST show each conflict, its competing values, and the chosen resolution before creating a merge commit.
- **FR-010**: The system MUST reject a merge or branch action when any expected source, target, or working state has changed and offer a safe refresh-and-review path; stale proposals MUST not be silently resubmitted.
- **FR-011**: The system MUST prevent rewrite of the protected default branch.
- **FR-012**: The system MUST require an explicit confirmation and display affected reachable history before rewriting a non-default branch.
- **FR-013**: The system MUST let the owner hide or reveal a version in normal lineage presentation without deleting, rewriting, or changing reachability; collapsed history MUST remain visibly indicated and discoverable through show-hidden and direct references.
- **FR-014**: The system MUST let the owner view and restore eligible displaced versions during the 90-day recovery period and view hidden reachable versions without treating their visibility as an expiry condition.
- **FR-015**: The system MUST make merge, branch, commit, rewrite, hide, reveal, and restore actions auditable.
- **FR-016**: The system MUST identify unavailable operations and reasons without exposing internal infrastructure details.
- **FR-017**: The system MUST display commit tags in lineage and let the owner create and remove tags for eligible commits under the version-control naming and protection rules.
- **FR-018**: The system MUST audit tag creation and removal with the acting owner, affected commit, tag name, and outcome.
- **FR-019**: The system MUST let the owner restore or revert an eligible reachable historical state by creating an explicit new version-control operation without deleting later history.
- **FR-020**: Before any merge commit is created, the system MUST show the complete proposed library-wide result, affected images, selected XMP revisions, preview/provenance availability, and require final confirmation. Unavailable previews MUST be identified but MUST NOT be represented as valid render evidence.
- **FR-021**: The system MUST let the owner switch the active web branch context and MUST apply the same dirty-working-state block, preserve/stash, or explicitly confirmed-discard protection as checkout, including gallery web changes and device conflicts.
- **FR-022**: The system MUST provide an image-history entry from gallery detail and retain the selected image, branch, and recoverable gallery browse context during navigation back to the gallery.
- **FR-023**: Commit preparation MUST identify the branch, selected staged changes, and any uncommitted gallery web changes or device conflicts; it MUST not imply that saving gallery metadata has already created a commit.

### Key Entities

- **Lineage projection**: Image-specific view of immutable version relationships.
- **Comparison**: Two selected versions, common ancestor, XMP differences, and preview provenance.
- **Merge proposal**: A reviewable source-to-target operation bound to expected branch heads.
- **Conflict**: A change requiring the owner's selected XMP resolution.
- **Visibility preference**: Presentation-only hidden/revealed state for a version.

## Acceptance Criteria

- **AC-001**: A fixture with branches and merges is accurately represented in both graphical and textual lineage views.
- **AC-002**: Hiding a version removes it from normal presentation but leaves its immutable identity and reachability unchanged.
- **AC-003**: Comparing two versions always identifies exact input versions and preview provenance or a clear unavailable state.
- **AC-004**: Disjoint recognized-field changes merge without a conflict; all defined unsafe categories require a resolution.
- **AC-005**: A submitted merge whose target head moved is rejected without creating a commit.
- **AC-006**: Protected-default rewrite is rejected; non-default rewrite cannot proceed without confirmation.
- **AC-007**: Each merge resolution is reviewable before its merge commit is created.
- **AC-008**: Eligible displaced versions can be found and restored during the 90-day window.
- **AC-009**: Keyboard-only and assistive-technology users can complete lineage, comparison, and merge review tasks.
- **AC-010**: A tagged commit fixture displays its tag, and tag creation/removal creates the required audit evidence.
- **AC-011**: Restoring or reverting a reachable prior version creates a new explicit operation while retaining later commits.
- **AC-012**: A clean automatic merge and a resolved conflict merge both show the full proposed result and require confirmation before a merge commit exists.
- **AC-013**: Switching branch context with dirty working changes blocks or preserves them according to the explicit selected path and never silently loses them.
- **AC-014**: Opening lineage from a filtered gallery detail and returning preserves the selected image, branch, filters, sort, and page position; the history view never presents an uncommitted gallery edit as a commit.
- **AC-015**: A prepared merge is invalidated when either source or target head changes; a stale working or branch operation preserves all pending gallery and device changes and requires renewed review.
- **AC-016**: When a hidden commit lies between visible commits, normal lineage indicates collapsed ancestry; show-hidden and direct lookup recover its exact identity and parents.

## Non-Functional Requirements

- **NFR-001 Performance**: A lineage view containing 500 relevant versions MUST become usable within 2 seconds for 95% of requests under nominal load. Verification: representative browser and service load test.
- **NFR-002 Performance**: A two-version comparison excluding pending preview work MUST become usable within 3 seconds for 95% of requests. Verification: representative comparison load test.
- **NFR-003 Reliability**: Every mutating interface action MUST bind to expected source, target, and working state as applicable and be all-or-nothing. Verification: stale-state, duplicate-submit, and interrupted-request tests.
- **NFR-004 Security**: Only the authenticated library owner may view or mutate private lineage and branch information; privileged rewrite and recovery actions MUST follow the parent program's stronger re-authentication rule. Verification: owner and cross-library authorization tests.
- **NFR-005 Privacy**: Commit messages, metadata differences, and location values MUST remain private library data and MUST not appear in error bodies or operational logs. Verification: response and log inspection with sensitive fixtures.
- **NFR-006 Accessibility**: Lineage, comparison, and merge workflows MUST meet WCAG 2.2 AA; graph functions MUST have keyboard operations, visible focus, text alternatives, and a non-graph equivalent. Verification: automated checks and manual keyboard and screen-reader exercises.
- **NFR-007 Compatibility**: The interface MUST support current desktop Chrome, Edge, and Firefox and retain usable text history when previews fail. Verification: browser matrix against the documented release baseline.
- **NFR-008 Observability**: Operators MUST measure graph size, compare/merge outcomes, conflicts, rejected stale operations, and rewrite attempts with operation IDs and no private content; sustained failure or latency breaches MUST alert within 15 minutes. Verification: fault-injection and telemetry review.
- **NFR-009 Maintainability**: The recognized-field merge policy MUST be documented, versioned, and testable independently of presentation; an unknown field MUST default to manual resolution. Verification: policy contract and fixture review.
- **NFR-010 Testability**: Automated tests MUST cover lineage projection, visibility state, comparison, merge categories, stale heads, dirty working state, rewrite policy, and recovery. Verification: CI test evidence mapped to FR and AC IDs.
- **NFR-011 Operations**: Operators MUST be able to temporarily disable mutating controls while preserving read-only lineage and MUST have a documented recovery path for failed history operations. Verification: operational exercise.
- **NFR-012 Recovery**: Interrupted client actions MUST not leave a partial merge, rewrite, visibility, or checkout state; eligible displaced commits MUST remain recoverable for 90 days. Verification: interruption and day-90 boundary tests.
- **NFR-013 Localization**: English may be the initial interface language, but labels, dates, times, and conflict messages MUST be localizable and locale-aware; immutable IDs remain language-neutral. Verification: three locale/time-zone combinations.
- **NFR-014 Cost**: Initial lineage loading MUST be bounded to at most 100 versions, with on-demand expansion; no initial view may fetch the entire 1,000,000-revision reference library. Verification: request-count and payload inspection at reference scale.
- **NFR-015 Compliance**: Presentation hiding MUST not undermine required retention, audit, or recovery evidence; no separate certification is claimed for this feature. Verification: hide/reveal and recovery audit exercise.
- **NFR-016 Availability**: Loss of preview rendering MUST not prevent textual lineage and XMP-difference review. Verification: render-service fault injection.
- **NFR-017 Deployment**: A feature rollout or rollback MUST preserve committed history, branch references, and working changes; the interface MUST remain compatible with the deployed version-control contract during the rollout window. Verification: pre-production rollout and rollback exercise.

## Assumptions and Dependencies

- Feature 006 owns commit, DAG, branch, protected-ref, hide, and recovery semantics; this feature invokes those contracts.
- Feature 008 supplies historical renders and provenance; unavailable previews never change version-control truth.
- Feature 009 supplies gallery detail, branch-scoped web working changes, and URL-backed filter, sort, and page context. This feature supplies its history entry and version-control actions.
- The single owner may rewrite non-default branches only after explicit confirmation, as approved in the parent program.
- The first release has one library owner and uses the current desktop browser baseline; permissions do not expand to collaborators.

## Constraints, Risks, and Open Questions

- **Constraint**: Feature 006 owns the immutable library-wide graph and ref mutations. Feature 010 may prepare and present operations but must use those authoritative semantics.
- **Risk**: Darktable processing and mask data can be syntactically mergeable while producing an unintended image. Ambiguous content requires owner resolution and exact-result review.
- **Risk**: An image-focused graph can hide library-wide commits and make ancestry appear simpler than it is. The interface must label that projection and disclose collapsed or irrelevant ancestry.
- **Open questions**: None block this specification. Technical planning must define the recognized metadata-field policy, exact pagination behavior, preview request strategy, and re-authentication integration within the requirements above.

## Success Criteria

- **SC-001**: A photographer can locate and compare either of two versions in a 500-version fixture history within two minutes.
- **SC-002**: All unsafe merge-fixture categories stop for explicit resolution; no test creates a silent unsafe merge.
- **SC-003**: 95% of 500-version lineage views become usable within two seconds under nominal load.
- **SC-004**: Every rewrite, merge, hide, reveal, and restore fixture action has an audit record and preserves the required recovery behavior.
- **SC-005**: In a scripted gallery-to-lineage-to-gallery task, 100% of fixture runs retain the selected image, branch, filters, sort, and page position.
- **SC-006**: In stale-head, duplicate-submit, and interrupted-request exercises, zero operations silently discard working changes or create an unconfirmed merge commit.
