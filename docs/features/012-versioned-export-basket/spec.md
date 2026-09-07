# Feature Specification: Versioned Export Basket

**Feature Branch**: `feature/012-versioned-export-basket`

**Created**: 2026-09-03

**Status**: Draft - awaiting product-owner approval

**Owner**: Project maintainer

**Parent Program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Related Features**: 005 image ingest/storage, 006 XMP version-control core, 008 darktable rendering, 009 gallery metadata search, 010 lineage and merge interface, 011 backup/disaster recovery

**Related Decision**: [ADR-0004](../../architecture/decisions/0004-versioned-export-snapshots.md)

**Input**: Add the ability to export any version of an image and provide a basket-style system for selecting images for export.

## Problem Statement

The gallery and lineage features let the owner find images and inspect edit history, but they do not yet provide a deliberate way to collect multiple images and export the exact selected edit states. A normal "download current image" action would be unsafe for this product because branches can move, commits can be hidden from normal views, displaced commits can remain recoverable for 90 days, and render output depends on darktable and preset provenance. The owner needs an export workflow that behaves like a photography lighttable basket while preserving Git-like version exactness.

## Users and Roles

- **Library owner**: the single authenticated owner of the private library; can select versions, manage baskets, prepare exports, download results, cancel jobs, and view audit evidence.
- **Operator**: can observe and operate export preparation infrastructure, but cannot view exported media or private manifest contents except through an approved owner-authorized recovery/support process outside this feature.

## Goals

- Let the owner collect images from gallery and lineage contexts into a persistent export basket.
- Allow any eligible current, historical, hidden, or recoverable XMP version to be exported when its required source bytes and retention policy allow access.
- Pin each basket item to an exact source-image content identity and XMP identity so later branch movement, hiding, or rewrites cannot change what is exported.
- Render exported derivatives with traceable provenance, predictable names, and a manifest suitable for later verification.
- Make batch export preparation private, resumable, cancellable, observable, and bounded by cost and retention policy.

## Non-Goals

- Public galleries, shared links, client delivery portals, print ordering, or ecommerce workflows.
- Replacing commit, branch, merge, hide/reveal, restore, or history-rewrite controls owned by feature 010.
- Editing XMP, creating commits, or modifying branch heads from the export workflow.
- Bulk archival export of the entire library or disaster-recovery media extraction; feature 011 owns recovery.
- Guaranteeing that a permanently purged source or XMP state remains exportable after purge eligibility and completion.
- Implementing browser-based RAW editing or darktable-compatible export presets beyond the approved rendering/export compatibility matrix.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Build an export basket from gallery results (Priority: P1)

As the library owner, I can add images from filtered gallery results into a persistent basket, review what is selected, remove mistakes, and preserve the basket while navigating elsewhere.

**Why this priority**: The basket is the core collection workflow; without it, batch export becomes repetitive one-off downloading.

**Independent Test**: Import a fixture library, apply gallery filters, add and remove multiple images, navigate away and back, and verify the basket contents, counts, selected version labels, and duplicate handling remain correct.

**Acceptance Scenarios**:

1. **Given** the owner is viewing filtered gallery results, **When** the owner adds selected images to the basket, **Then** each item appears in the basket with its selected version, display name, preview state, and readiness status.
2. **Given** the basket contains items from multiple gallery filters, **When** the owner navigates away, changes filters, and returns, **Then** the basket still contains the prior pinned selections.
3. **Given** the owner removes or clears basket items, **When** the basket is reopened, **Then** the removed items no longer appear and no export snapshot includes them.

---

### User Story 2 - Export exact historical versions (Priority: P1)

As the library owner, I can choose the current edit, a historical commit, a hidden version, or a recoverable displaced version of an image and export the exact rendition represented by that version.

**Why this priority**: The user explicitly needs "any version" export, and exact historical output is where this feature must preserve trust.

**Independent Test**: For one RAW fixture with current, hidden, displaced-within-retention, and at least three committed historical XMP states, add each to a basket, move the branch head, hide/reveal a version, prepare export, and verify every output provenance still references the selected state.

**Acceptance Scenarios**:

1. **Given** an image has multiple eligible XMP versions, **When** the owner adds more than one version of that image to the basket, **Then** each selected version is represented as a separate explicit basket item.
2. **Given** a basket item pins a branch head at selection time, **When** that branch later moves, **Then** the basket item continues to resolve to the original selected image content and XMP identity.
3. **Given** a hidden version is explicitly revealed or selected from an eligible advanced history view, **When** the owner exports it, **Then** the output uses that hidden version while preserving its presentation-hidden status.
4. **Given** a displaced version remains inside the 90-day recovery window and required source bytes are still held, **When** the owner exports it, **Then** the export succeeds or reports a precise unavailable reason without substituting another state.

---

### User Story 3 - Prepare and download a verified export package (Priority: P1)

As the library owner, I can choose an export preset, start preparation from a basket snapshot, monitor progress, and privately download a package containing completed outputs and a manifest.

**Why this priority**: Selection only matters if the owner can reliably get the rendered files out of the hosted library.

**Independent Test**: Prepare a mixed basket under a supported preset, force one render failure and one unavailable source, verify per-item status, download the completed package, and validate the manifest against the outputs.

**Acceptance Scenarios**:

1. **Given** a basket with ready items, **When** the owner chooses a supported preset and starts export, **Then** the system captures an immutable basket snapshot and begins preparing outputs for that snapshot only.
2. **Given** some items complete and others fail or are unavailable, **When** preparation reaches a terminal state, **Then** the owner sees every item outcome and can download completed outputs with an explicit failure summary.
3. **Given** a completed export, **When** the owner downloads it before expiry, **Then** access is owner-authorized, time-limited, audited, and includes a manifest mapping every output to its selected source and provenance.

---

### User Story 4 - Cancel, expire, and recover safely (Priority: P2)

As the library owner, I can cancel an export before completion, understand when prepared downloads expire, and see why an old basket item can no longer be exported after purge.

**Why this priority**: Export jobs can be expensive, large, and tied to retention policy; the owner needs predictable cleanup behavior.

**Independent Test**: Cancel an in-progress export, allow another completed export to expire, permanently purge an eligible source/version fixture, and verify basket item states, download denial, and audit records.

**Acceptance Scenarios**:

1. **Given** export preparation is not complete, **When** the owner cancels it, **Then** no further outputs are prepared for that snapshot and any already prepared temporary output loses download eligibility according to the cancellation policy.
2. **Given** a completed export has passed its download-retention interval, **When** the owner opens it, **Then** download is unavailable, audit evidence remains, and the owner can create a new export from still-eligible basket items.
3. **Given** a source image or XMP version has been permanently purged under the approved recovery policy, **When** an old basket item references it, **Then** the item is marked permanently unavailable and cannot be exported through normal, hidden, or recovery views.

### Edge Cases and Error Handling

- A basket references an image that is soft-deleted, restored to a conflicting path, recoverable deleted, or permanently purged.
- A basket references an XMP version that is hidden, revealed, displaced by a confirmed rewrite, restored through a recovery reference, or expired from all retention holds.
- A basket item continues to exist after its source or XMP lifecycle hold expires; identity remains visible, but the item becomes unavailable for export.
- The same logical asset is selected at multiple XMP versions or under multiple export presets.
- A branch head, tag, or working state changes while the owner is adding selections or starting export.
- An item is added from an uncommitted working state; the basket must either pin a durable working snapshot or require the owner to choose a committed version before export.
- Rendering is pending, failed, incompatible, cancelled, or unavailable for one item in a larger basket.
- Output filenames collide after sanitization, truncation, or locale conversion.
- The export package would exceed configured size, item-count, duration, or cost limits.
- The owner retries an export request after a timeout or duplicate submission.
- Download is interrupted, expired, or attempted without valid owner authorization.
- A manifest or output is incomplete, altered, or missing at packaging time.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The owner MUST be able to create, rename, inspect, clear, and delete private export baskets, including one default active basket.
- **FR-002**: The owner MUST be able to add and remove basket items from gallery asset views and lineage/version views without losing the current browse or lineage context.
- **FR-003**: Each basket item MUST pin exactly one logical image asset, original-image content identity, edit identity, and XMP state identity at selection time.
- **FR-004**: If the owner adds a version from a branch or tag, the basket item MUST resolve and store the exact commit/XMP identity selected at that moment; later ref movement MUST NOT change the basket item.
- **FR-005**: If the owner adds an uncommitted working state, the system MUST either create a durable export-only snapshot with explicit provenance or require the owner to select a committed version; it MUST NOT export an unstable mutable working file by path.
- **FR-006**: The owner MUST be able to retain the same image asset in a basket at multiple versions, and the system MUST make duplicates explicit rather than silently merging them.
- **FR-007**: Hidden versions MAY be added only through an explicit show-hidden or advanced-history selection; export MUST NOT reveal hidden versions in normal basket suggestions.
- **FR-008**: Displaced or soft-deleted versions inside the 90-day recovery window MUST remain exportable when required source bytes and XMP bytes are still under retention hold; otherwise the item MUST show a precise unavailable reason.
- **FR-009**: Permanently purged source-image or XMP bytes MUST make dependent basket items and export snapshots permanently unavailable for re-export, while retaining non-sensitive audit evidence.
- **FR-010**: The basket MUST show, per item, display name, selected version label, commit/reference context when available, preview state, availability, output-name preview, and any blocking issue.
- **FR-011**: The owner MUST be able to choose a supported export preset before preparation, including output format, size class, color/metadata treatment, and whether to include a manifest.
- **FR-012**: Export preparation MUST capture an immutable basket snapshot and use only that snapshot for all outputs in the export request.
- **FR-013**: Rendered exports MUST be produced from the exact pinned source-image content and XMP state through the feature 008 rendering/provenance contract.
- **FR-014**: The owner MUST be able to export exact original source bytes for eligible basket items when the selected preset is "originals"; this MUST return the selected original content identity and MUST NOT imply XMP rendering.
- **FR-015**: Export preparation MUST report item-level pending, completed, failed, unavailable, skipped, and cancelled states and MUST never substitute another version for a failed item.
- **FR-016**: Completed batch exports MUST provide a private downloadable package containing completed outputs and an explicit failure summary when any selected item did not complete.
- **FR-017**: Every export package MUST include a machine-readable manifest mapping each requested basket item to output filename, output byte length, output checksum, asset identity, original content checksum, XMP identity/checksum when applicable, commit/ref context when available, preset identity, darktable/render provenance when rendered, item status, and preparation time. When a package is produced, the system MUST also provide a separately verifiable package checksum for the exact downloadable package.
- **FR-018**: Output filenames MUST be deterministic, understandable, collision-free within a package, safe on supported Windows filesystems, and traceable through the manifest.
- **FR-019**: The owner MUST be able to cancel an export request before terminal completion; cancellation MUST produce an auditable terminal or draining state with no ambiguous partial success.
- **FR-020**: Prepared export downloads MUST expire after a documented default interval of 24 hours unless a later approved policy changes it; expired outputs MUST deny download while retaining required audit and manifest-summary evidence.
- **FR-021**: Repeated export-submission requests with the same idempotency identity MUST return the original export request and MUST NOT create duplicate preparation work.
- **FR-022**: Basket changes, export preparation, item failures, cancellation, package creation, download access, expiry, and permanent unavailability MUST produce attributable audit events without media bytes, XMP payloads, full private paths, geolocation, or secrets in audit/telemetry payloads.
- **FR-023**: Only the authenticated library owner MAY inspect baskets, start exports, read manifests, download packages, cancel jobs, or see failure details.
- **FR-024**: Operators MUST be able to pause new export preparation, drain or cancel pending work, and preserve owner-visible state for retry without corrupting baskets or completed packages.
- **FR-025**: Basket pins MUST be identity pins, not lifecycle-retention holds. A basket item MAY remain listed indefinitely, but it MUST be shown as exportable only while pre-existing source-image, XMP, commit, or recovery lifecycle holds make all required bytes eligible. Hidden or displaced presentation state MUST NOT by itself make an otherwise retained item unavailable; permanent purge MUST make every dependent item and snapshot unavailable for re-export.
- **FR-026**: Export presets MUST declare their metadata behavior before preparation. The default rendered-output preset MUST remove GPS coordinates, derived location labels, camera-owner/contact fields, and other configured sensitive descriptive metadata while retaining non-sensitive technical provenance needed for verification. Retaining location or contact metadata MUST require an explicit owner choice and confirmation before preparation.

### Acceptance Criteria

- **AC-001**: Adding, removing, clearing, and reopening a basket across at least three filtered gallery contexts preserves exactly the expected items, counts, and selected-version labels.
- **AC-002**: A fixture exports the current version plus at least three committed historical versions of one RAW image, and every output manifest entry identifies the selected XMP identity and matching provenance with zero substitutions.
- **AC-003**: Moving a branch head, hiding a commit, or rewriting a non-default branch after basket selection does not alter existing basket-item identity or export output.
- **AC-004**: The same image selected at three different versions appears as three distinguishable basket items and exports as three separately named outputs.
- **AC-005**: An uncommitted working-state fixture cannot be exported from a mutable path; it either creates a durable export-only snapshot with provenance or requires committed-version selection.
- **AC-006**: A hidden-version fixture is excluded from normal suggestions, can be selected only after show-hidden/advanced-history action, and exports without changing hidden presentation state.
- **AC-007**: A displaced-within-retention fixture exports when all required bytes are held; a permanently purged fixture is marked permanently unavailable and cannot be exported.
- **AC-008**: A mixed export with completed, failed, unavailable, and cancelled items reports every selected item outcome and never replaces a failed item with another version.
- **AC-009**: Every completed package contains collision-free Windows-safe filenames and a manifest that maps each requested item to exact source, version, preset, status, and render/original provenance.
- **AC-010**: Repeating the same export submission 50 times after a timeout produces one logical export request and no duplicate package.
- **AC-011**: Unauthorized basket, manifest, cancellation, and download attempts are denied and audited without exposing whether unrelated private assets exist.
- **AC-012**: Cancelling an in-progress export and waiting past a completed export's expiry both deny further download access while preserving audit evidence and visible retry guidance.
- **AC-013**: A basket pin does not prevent an otherwise eligible source/version purge; after purge the item remains visible with its pinned identity and becomes permanently unavailable, while clearing the basket never releases a lifecycle hold owned by history or recovery.
- **AC-014**: The default rendered-export preset removes fixture GPS coordinates, derived location labels, and contact metadata; retaining any of those fields requires explicit confirmation and the manifest records the selected metadata treatment.
- **AC-015**: An interrupted download can be verified against the separately provided package checksum, and every extracted output can be verified against the byte length and checksum recorded in the manifest.

### Key Entities

- **Export basket**: Owner-private named collection of pinned export selections; includes identity, name, lifecycle state, item count, creation/update times, and owner.
- **Basket item**: One selected image/version entry with logical asset identity, original content identity, edit identity, XMP identity or original-only mode, selection source, display labels, availability, and output-name preview. Its pin is an exact identity reference and does not change source/XMP lifecycle retention.
- **Export preset**: Versioned user-facing export choice describing output type, size class, color/metadata treatment, and compatibility constraints.
- **Export request**: Idempotent preparation command bound to one immutable basket snapshot and one export preset.
- **Export snapshot**: Immutable list of basket items and preset settings used for a single export request, unaffected by later basket edits.
- **Export package**: Private downloadable bundle of completed outputs plus manifest and failure summary.
- **Export manifest**: Machine-readable provenance record for every requested item and output, including per-output byte length and checksum.
- **Export audit event**: Attributable non-sensitive event for selection, preparation, cancellation, download, expiry, or unavailability.

## Non-Functional Requirements

- **NFR-001 Performance**: For a basket with up to 250 items, 95% of basket open/update interactions MUST become usable within 2 seconds under nominal load. Verification: browser workflow benchmark.
- **NFR-002 Performance**: Starting export preparation for a 250-item basket MUST acknowledge the immutable snapshot and show initial progress within 3 seconds for 95% of nominal requests, excluding render time. Verification: load test.
- **NFR-003 Availability**: Existing baskets and completed, unexpired packages MUST remain inspectable/downloadable during unrelated gallery-index degradation. Verification: dependency-failure test.
- **NFR-004 Reliability**: An export snapshot, package, and manifest MUST be internally consistent; if consistency cannot be proven, the package is unavailable and the owner sees a retryable or terminal reason. Verification: integrity fault injection.
- **NFR-005 Security**: Basket, manifest, package, and download operations MUST be owner-authorized; download locations or tokens MUST be unguessable, time-limited, and revocable. Verification: authorization and token tests.
- **NFR-006 Privacy**: Audit logs, telemetry, and operator views MUST omit media bytes, XMP payloads, full private paths, geolocation values, and descriptive metadata unless explicitly owner-authorized for support. Verification: log and telemetry scan.
- **NFR-007 Accessibility**: Basket management, version selection, preset choice, progress, item errors, cancellation, manifest availability, and download controls MUST conform to WCAG 2.2 AA, including keyboard operation and non-color status. Verification: automated and manual accessibility tests.
- **NFR-008 Compatibility**: Exported filenames MUST be valid on supported Windows filesystems; rendered exports MUST follow the approved darktable/render preset compatibility matrix; manifests MUST be forward-readable by version. Verification: compatibility fixtures.
- **NFR-009 Observability**: Operators MUST measure basket size, export queue age, preparation duration, item outcome counts, render failures, package size, download attempts, expiry, cancellation, and policy denials; sustained failures alert within 15 minutes. Verification: observability review and fault injection.
- **NFR-010 Maintainability**: Export presets, snapshot schema, manifest schema, filename rules, retention defaults, and availability reasons MUST be versioned and documented. Verification: design review and contract tests.
- **NFR-011 Testability**: Automated tests MUST cover pinned versions, branch movement, hidden versions, displaced/purged availability, working snapshots, duplicate selections, presets, render/original modes, mixed failures, manifest integrity, authorization, idempotency, cancellation, expiry, and operator pause/drain. Verification: CI evidence and traceability.
- **NFR-012 Localization**: User-visible labels, statuses, dates, preset names, and error messages MUST be localizable; manifest identifiers, checksums, and timestamps remain locale-neutral and unambiguous. Verification: locale tests.
- **NFR-013 Deployment**: Schema/storage changes for baskets, snapshots, packages, and manifests MUST be backward-compatible through rollout and rollback. Verification: migration rehearsal.
- **NFR-014 Operations**: Runbooks MUST cover stuck export jobs, render-provider outage, package integrity failure, authorization incident, storage quota/cost limit, cancellation backlog, and retention/purge conflicts. Verification: operational readiness review.
- **NFR-015 Cost**: Export preparation MUST enforce configurable item-count, package-size, render-duration, and retention limits; expensive or unusually large exports MUST show a pre-start warning. Verification: quota/cost simulation.
- **NFR-016 Compliance**: Export package retention, expiry, deletion, and purge interaction MUST honor the parent privacy/deletion policy and must not retain media derivatives after source purge obligations require removal. Verification: policy mapping and purge fixture.
- **NFR-017 Recovery**: After restart or worker loss, in-progress export requests MUST resolve to visible retryable, cancelled, or terminal states within 10 minutes without changing completed package provenance. Verification: restart/fault test.
- **NFR-018 Scalability**: The initial release MUST support at least 20 saved baskets per library, 250 active items per basket, and 10 retained export requests per basket while meeting the stated interaction targets. Verification: capacity test.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A photographer can create a 100-item basket from gallery results and start export preparation in under 2 minutes during usability testing.
- **SC-002**: A fixture containing current, historical, hidden, and recoverable displaced versions exports with 100% manifest-to-output provenance matches and zero version substitutions.
- **SC-003**: At least 95% of 250-item basket views become usable within 2 seconds and 95% of export submissions acknowledge a snapshot within 3 seconds under nominal load.
- **SC-004**: Authorization, cancellation, expiry, purge, and mixed-failure tests produce 100% of expected user-visible statuses and audit evidence.
- **SC-005**: 100% of completed export packages in the test corpus contain Windows-safe collision-free filenames and a complete manifest for every requested item.

## Constraints, Assumptions, and Dependencies

### Constraints

- This is a private single-owner feature; sharing and public links are outside v1 scope.
- Export truth is based on immutable original-image content and immutable XMP/content identities, not mutable branch names or file paths.
- Hidden versions remain normal retained history; hiding changes presentation only.
- Permanently purged source or XMP bytes are not recoverable or exportable.
- Prepared outputs are derivatives and do not replace original images, XMP history, or recovery records.

### Assumptions

- Feature 005 supplies stable asset/edit identity, immutable original bytes, verified retrieval, soft-delete/restore state, lifecycle holds, and purge eligibility.
- Feature 006 supplies exact XMP state identity, commit/ref resolution, working-state identity, hidden-state policy, reflog/recovery state, and retention holds.
- Feature 008 supplies rendered output, render provenance, compatibility matrix, retry/failure state, and render isolation.
- Feature 009 supplies gallery entry points and browse context preservation.
- Feature 010 supplies lineage/version selection, show-hidden behavior, and recovery-version discovery.
- Feature 011 supplies permanent purge and recovery-copy lifecycle rules.
- The default prepared-download retention interval is 24 hours because export packages can be regenerated from retained originals/XMP while they remain eligible.

### Dependencies

- [003 umbrella program](../003-darktable-version-control/spec.md).
- Feature 004 identity, authorization, audit, idempotency, telemetry, and CI/CD conventions.
- Feature 005 content, asset/edit identity, retrieval, lifecycle holds, and purge behavior.
- Feature 006 selected-version, branch/ref, hidden, working-state, and recovery contracts.
- Feature 008 export/render preset and provenance contracts.
- Feature 009 and 010 user entry points for selecting images and versions.
- Feature 011 backup, retention, purge, and recovery rules.

## Risks and Open Questions

- Exporting "any version" can conflict with permanent deletion; the spec resolves this by requiring exact export while retained and explicit permanent unavailability after authorized purge.
- Adding uncommitted working states is risky because they are mutable; the spec requires durable export-only snapshots or committed-version selection.
- Large RAW exports can create significant render, storage, and transfer cost; planning must choose quotas, warnings, and cancellation mechanics.
- Manifests can reveal sensitive filenames or metadata; planning must define a privacy-preserving manifest profile that still proves provenance.
- No material product ambiguity remains. Exact preset catalog, package format, retention cleanup mechanics, and data schema are planning decisions.
