# Feature Specification: Lineage and Merge Interface

**Feature Branch**: `feature/010-lineage-merge-interface`
**Created**: 2026-09-03
**Status**: Draft — awaiting product-owner approval
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)

## Problem and Scope

The photographer needs to understand how an image's edits evolved, compare selected XMP states visually, and safely bring work from one branch into another. Raw commit lists and opaque XMP conflicts do not meet that need.

This feature provides the version-control interface: lineage views, version comparison, merge preparation and resolution, branch/ref actions exposed to the owner, and presentation-only history hiding. The version engine remains feature 006's authority; rendering remains feature 008's authority. This feature does not edit original images, implement filesystem synchronization, or permit arbitrary XMP source editing.

## User Scenarios & Testing

### User Story 1 - Inspect image lineage (Priority: P1)

As a photographer, I can select an image and understand its branch and version lineage, including hidden-version status, so I can find the edit I need.

**Independent Test**: Create branching and merge fixtures, hide a non-head version, and verify the graph and accessible textual alternative both represent reachable lineage accurately.

**Acceptance Scenarios**:

1. **Given** an image with branch and merge history, **When** I open lineage, **Then** I can identify versions, parents, branch labels, commit metadata, and current selection.
2. **Given** a hidden version, **When** normal history is shown, **Then** it is omitted without altering stored history; I can opt in to reveal it.

### User Story 2 - Compare edits (Priority: P1)

As a photographer, I can select two versions and inspect their renderings and meaningful XMP differences before deciding whether to merge.

**Independent Test**: Compare fixture versions with known visual and metadata changes and verify both inputs, differences, and preview provenance.

### User Story 3 - Merge safely (Priority: P1)

As a photographer, I can merge a source branch into a target branch, resolve conflicts field by field when needed, and create the resulting commit deliberately.

**Independent Test**: Exercise clean merge, recognized-field conflict, unknown-module conflict, protected-default rejection, and explicit non-default rewrite confirmation.

### Edge Cases

- A selected version is hidden, unreachable after a permitted rewrite, or within the 90-day recovery window.
- Two versions have missing previews, incompatible renders, or no common ancestor.
- XMP includes unknown modules, masks, history entries, or simultaneous changes to the same recognized field.
- Branch heads move during comparison, merge preparation, or submission.
- A graph is too large for visual rendering or the user relies on keyboard/screen-reader interaction.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST present image-focused lineage with versions, parent relationships, branch labels, commit metadata, and selected-version context.
- **FR-002**: The system MUST offer an equivalent searchable textual or tabular lineage view.
- **FR-003**: The system MUST permit comparison of two selected versions that identifies both version IDs, their common ancestor when one exists, and their XMP differences.
- **FR-004**: The system MUST request and display selected-version previews with their provenance and accurately show pending, unavailable, or failed states.
- **FR-005**: The system MUST let the owner create a branch from an eligible selected version and name it under the version-control naming rules.
- **FR-006**: The system MUST let the owner create an explicit commit from eligible working changes with a required message.
- **FR-007**: The system MUST provide merge preparation that identifies the source, target, expected target head, common ancestor, and affected images before submission.
- **FR-008**: The system MUST automatically merge only disjoint changes to recognized metadata fields; all simultaneous scalar changes, unknown modules, masks, and history changes MUST require explicit resolution.
- **FR-009**: The system MUST show each conflict, its competing values, and the chosen resolution before creating a merge commit.
- **FR-010**: The system MUST reject a merge or branch action when its expected head has changed and offer a safe refresh-and-retry path.
- **FR-011**: The system MUST prevent rewrite of the protected default branch.
- **FR-012**: The system MUST require an explicit confirmation and display affected reachable history before rewriting a non-default branch.
- **FR-013**: The system MUST let the owner hide or reveal a version in normal lineage presentation without deleting, rewriting, or changing reachability.
- **FR-014**: The system MUST let the owner view and restore eligible displaced or hidden versions during the 90-day recovery period.
- **FR-015**: The system MUST make merge, branch, commit, rewrite, hide, reveal, and restore actions auditable.
- **FR-016**: The system MUST identify unavailable operations and reasons without exposing internal infrastructure details.
- **FR-017**: The system MUST display commit tags in lineage and let the owner create and remove tags for eligible commits under the version-control naming and protection rules.
- **FR-018**: The system MUST audit tag creation and removal with the acting owner, affected commit, tag name, and outcome.
- **FR-019**: The system MUST let the owner restore or revert an eligible reachable historical state by creating an explicit new version-control operation without deleting later history.
- **FR-020**: Before any merge commit is created, the system MUST show the complete proposed result, affected images, selected XMP revisions, preview/provenance availability, and require final confirmation.
- **FR-021**: The system MUST let the owner switch the active web branch context and MUST apply the same dirty-working-state block, preserve/stash, or explicitly confirmed-discard protection as checkout.

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

## Non-Functional Requirements

- **NFR-001 Performance**: A lineage view containing 500 relevant versions MUST become usable within 2 seconds for 95% of requests under nominal load.
- **NFR-002 Performance**: A two-version comparison excluding pending preview work MUST become usable within 3 seconds for 95% of requests.
- **NFR-003 Reliability**: Every mutating interface action MUST be bound to an expected version-control state and be all-or-nothing.
- **NFR-004 Security**: Only the authenticated library owner may view or mutate private lineage and branch information.
- **NFR-005 Privacy**: Commit messages and metadata differences MUST be treated as private library data.
- **NFR-006 Accessibility**: Graph functions MUST have keyboard operations, focus order, text alternatives, and a non-graph equivalent.
- **NFR-007 Compatibility**: The interface MUST support the documented current desktop browser baseline.
- **NFR-008 Observability**: Operators MUST measure graph size, compare/merge outcomes, conflicts, rejected stale operations, and rewrite attempts.
- **NFR-009 Maintainability**: The recognized-field merge policy MUST be documented, versioned, and testable independently of the interface.
- **NFR-010 Testability**: Automated tests MUST cover lineage projection, visibility state, comparison, merge categories, stale heads, rewrite policy, and recovery.
- **NFR-011 Operations**: Operators MUST be able to temporarily disable mutating controls while preserving read-only lineage.
- **NFR-012 Recovery**: Interrupted client actions MUST not leave a partial merge, rewrite, or visibility state.
- **NFR-013 Localization**: Commit and conflict-interface labels and messages MUST be localizable; immutable identifiers remain language-neutral.
- **NFR-014 Cost**: Large histories MUST use bounded presentation and on-demand expansion rather than unbounded initial loading.
- **NFR-015 Compliance**: Presentation hiding MUST not undermine required retention, audit, or recovery evidence.
- **NFR-016 Availability**: Loss of preview rendering MUST not prevent textual lineage and XMP-difference review.

## Assumptions and Dependencies

- Feature 006 owns commit, DAG, branch, protected-ref, hide, and recovery semantics; this feature invokes those contracts.
- Feature 008 supplies historical renders and provenance; unavailable previews never change version-control truth.
- The single owner may rewrite non-default branches only after explicit confirmation, as approved in the parent program.

## Success Criteria

- **SC-001**: A photographer can locate and compare either of two versions in a 500-version fixture history within two minutes.
- **SC-002**: All unsafe merge-fixture categories stop for explicit resolution; no test creates a silent unsafe merge.
- **SC-003**: 95% of 500-version lineage views become usable within two seconds under nominal load.
- **SC-004**: Every rewrite, merge, hide, reveal, and restore fixture action has an audit record and preserves the required recovery behavior.
