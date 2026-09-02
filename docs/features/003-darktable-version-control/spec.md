# Program Specification: Darktable Version-Controlled Image Library

**Feature Branch**: `feature/003-darktable-version-control`

**Created**: 2026-09-02

**Status**: Approved

**Approved**: 2026-09-02 by project owner

**Owner**: Project maintainer

**Input**: A web portal hosted on AWS that stores photographs and darktable XMP sidecars, provides Git-like XMP version control and image lineage, renders current and historical previews with darktable, supports darktable-compatible metadata browsing, backs the library up to cold storage, exposes a Windows-accessible working folder for darktable, and uses GitHub Actions for complete continuous integration and delivery.

**Delivery model**: This is the approved umbrella product specification. It defines shared outcomes and constraints but MUST NOT be implemented as one feature-sized change. Delivery is split into child features 004–011, each with its own matching branch, specification, plan, tasks, tests, documentation, and approval gates. Child specifications may refine their allocated scope but may not weaken this program's requirements without an approved amendment here.

## Problem Statement

Photographers who edit with darktable need durable off-device storage, searchable access to their photographs, and a reliable history of non-destructive XMP edits. Ordinary cloud drives can synchronize files but do not present photographic metadata, render historical XMP states, or provide understandable commit, branch, merge, and lineage workflows. The product must make a remotely hosted library usable both from a browser and from darktable on Windows without losing edit history or silently overwriting concurrent work.

## Clarifications

### Session 2026-09-02

- Q: Should users be able to edit photographic metadata from the web gallery, or only browse metadata written by darktable? → A: Users can edit selected organizational and descriptive metadata in the web gallery; each edit becomes an uncommitted XMP working change.
- Q: Should each Windows device have its own checked-out branch, or should one active branch apply to the entire library? → A: Each Windows device independently checks out a branch.
- Q: How much Git-style history rewriting should the version-control interface permit? → A: Non-default branches may be rewritten after confirmation; the default branch is protected, displaced commits remain recoverable, and users may reversibly hide selected versions from normal history views.
- Q: How long should deleted images and commits displaced by a branch rewrite remain recoverable before permanent purge? → A: Retain them for 90 days after deletion or displacement.
- Q: What level of AWS regional disaster protection should the first release provide? → A: Run the primary service across availability zones in one AWS region and store cold recovery copies in a second AWS region.

## Goals

- Preserve original image files and every committed darktable XMP state.
- Let a photographer browse, sort, and filter a web gallery using darktable-equivalent metadata.
- Provide Git-like commits, branches, diffs, merges, tags, history, and conflict handling for XMP sidecars.
- Render thumbnails and selected current or historical edits consistently with darktable.
- Make a synchronized Windows working folder usable as a darktable library location.
- Recover the library after accidental deletion, corruption, or loss of the primary AWS environment.
- Validate every change and provide reproducible, secure, observable deployment and rollback through GitHub Actions.

## Non-Goals

- Replacing darktable's editing interface or implementing a browser-based RAW editor.
- Versioning arbitrary changes to large image binaries in the same manner as XMP sidecars.
- Promising byte-identical rendering across unsupported darktable versions or missing external resources.
- General-purpose source-code hosting or compatibility with every Git client and protocol.
- Mobile-native applications in the initial release.
- Deploying unreviewed pull-request code or rebuilding an artifact differently for each environment.
- Implementing the entire program through one branch, plan, task list, pull request, or release milestone.

## Delivery Decomposition

| Child feature | Primary responsibility | Program requirements allocated |
|---|---|---|
| `004-platform-foundation-ci-cd` | Repository/toolchain foundation, identity boundary, AWS environment skeleton, observability baseline, and secure GitHub Actions delivery | FR-023–024, FR-030–039; cross-cutting NFR-005, NFR-009–020 |
| `005-image-ingest-storage` | Original/XMP import, integrity, identity, paths, durable object storage, working-object intake, and lifecycle foundation | FR-001–003, FR-011, FR-018–019; AC-001, AC-005 |
| `006-xmp-version-control-core` | Content-addressed XMP revisions, working state, commits, branches, tags, protected refs, rewriting, reflog, and atomicity | FR-002, FR-007–013, FR-027–028; AC-004–005, AC-011–013 |
| `007-windows-cloud-files-sync` | Windows virtual folder, placeholders, hydration, pinning, per-device checkout, offline journal, upload, and conflict preservation | FR-012–018, FR-026; AC-001, AC-006, AC-010 |
| `008-darktable-rendering` | Isolated darktable CLI workers, thumbnails, historical previews, cache, provenance, and compatibility corpus | FR-005–006; AC-003; NFR-001, NFR-008 |
| `009-gallery-metadata-search` | Gallery browse/filter/sort, normalized metadata/provenance, asset details, and web-originated XMP metadata changes | FR-003–004, FR-019, FR-025; AC-002, AC-009 |
| `010-lineage-merge-interface` | Image-focused graph, semantic diff, merge/conflict UX, hidden versions, restoration, and history operations | FR-007–010, FR-014, FR-027; AC-003–004, AC-011–012 |
| `011-backup-disaster-recovery` | Cross-account/cross-region cold copies, backup health, deletion lifecycle, isolated restore, regional recovery, and operational drills | FR-020–022, FR-028–029; AC-007, AC-013–014; NFR-004, NFR-006 |

Requirements intentionally overlap when one feature establishes a contract and another consumes or presents it. Each child plan must trace its local requirements and the referenced program requirements; program-level acceptance is achieved only after the integration and recovery validations across all children pass.

## Users and Roles

- **Library owner**: imports and organizes images, edits through darktable, manages history and recovery, and controls access.
- **Additional users**: The first release is a private single-user library. Household/team collaboration and a multi-tenant commercial service are outside v1 scope.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Safely synchronize a darktable library (Priority: P1)

As a photographer, I can import photographs and XMP sidecars into a Windows working folder, edit them with darktable, synchronize the changes, and create a named commit so my originals and editing history are durable.

**Why this priority**: Durable storage and capture of darktable edits are the product's core value; the gallery and lineage views depend on them.

**Independent Test**: Import a supported RAW/JPEG image and XMP sidecar, synchronize them from Windows, edit the image in darktable, create a commit, and verify that both the original and committed XMP can be restored on a second Windows installation.

**Acceptance Scenarios**:

1. **Given** a signed-in owner and an empty library, **When** the owner adds a supported image and XMP sidecar to the synchronized folder, **Then** the portal records the image, its metadata, synchronization state, and initial XMP state without modifying the original image.
2. **Given** a synchronized image whose XMP changes locally, **When** synchronization completes and the owner commits the change with a message, **Then** the new XMP state, author, timestamp, parent, and message appear in history and the prior state remains recoverable.
3. **Given** local and remote XMP changes descended from the same committed state, **When** synchronization detects both, **Then** neither change is silently overwritten and the user receives an actionable conflict state.
4. **Given** two authorized Windows devices, **When** each device checks out a different branch, **Then** each working folder reflects only its selected branch and changing one device's checkout does not modify the other device's files.

---

### User Story 2 - Browse and find photographs (Priority: P2)

As a photographer, I can browse thumbnails and narrow the gallery using the metadata concepts I use in darktable, so I can locate photographs without opening the desktop editor.

**Why this priority**: A remote library is useful only when a photographer can efficiently find and inspect its contents.

**Independent Test**: Import a varied test collection, filter and sort it by defined metadata fields, open an image, and verify that the displayed metadata and rendered preview reflect its selected XMP state.

**Acceptance Scenarios**:

1. **Given** indexed images with different ratings, color labels, tags, capture times, cameras, lenses, and file properties, **When** the user combines filters and sorting, **Then** the gallery returns only matching images in the requested deterministic order.
2. **Given** an image with a committed XMP state, **When** the user opens it, **Then** the portal shows its metadata, synchronization status, branch and commit context, and a darktable-rendered preview.
3. **Given** an unavailable or failed preview, **When** the gallery loads, **Then** it shows a clear placeholder and retry status without blocking metadata browsing.
4. **Given** an image visible in the gallery, **When** the owner changes its rating, rejected state, color label, tags, title, description, or geolocation, **Then** the portal creates a reviewable uncommitted XMP working change and does not alter the selected branch until that change is committed.

---

### User Story 3 - Explore and restore edit lineage (Priority: P3)

As a photographer, I can inspect the branch and commit graph for an image, compare XMP revisions, render a selected historical revision, and restore or branch from it without destroying later work.

**Why this priority**: Visible, recoverable lineage distinguishes this product from ordinary file synchronization.

**Independent Test**: Create divergent edits, browse their lineage, compare them, render both versions, merge or select a resolution, and restore an older version while proving all pre-existing commits remain reachable.

**Acceptance Scenarios**:

1. **Given** an image with commits on multiple branches, **When** the user opens the version-control interface, **Then** it shows each reachable commit, parent relationship, branch and tag reference, author, timestamp, and message.
2. **Given** two XMP revisions, **When** the user compares them, **Then** the interface identifies meaningful metadata/edit differences and allows each revision to be rendered against the same source image.
3. **Given** a selected historical commit, **When** the user restores or branches from it, **Then** a new recoverable history state is created and no existing commit is deleted.
4. **Given** a noisy history containing intermediate versions, **When** the owner hides selected commits, **Then** normal history and lineage views collapse those commits while preserving descendant results, and an explicit control can reveal and unhide them.

---

### User Story 4 - Protect and recover the library (Priority: P4)

As a library owner, I can see backup health and perform a documented recovery so a primary-system failure does not destroy images or XMP history.

**Why this priority**: Cold storage is valuable only when coverage and restoration are observable and regularly verified.

**Independent Test**: Back up a representative library snapshot, simulate loss of primary data, restore into an isolated environment, and verify original-image checksums, commit graph integrity, XMP content, metadata, and representative previews.

**Acceptance Scenarios**:

1. **Given** newly durable image and history data, **When** the backup objective elapses, **Then** the portal reports a recoverable cold-storage copy and its last verified time.
2. **Given** an authorized recovery request, **When** a chosen recovery point is restored, **Then** the system reconstructs the library and reports any missing or corrupt object before making it active.
3. **Given** the primary AWS region is unavailable, **When** the recovery runbook is invoked, **Then** operators can retrieve the independent cold copy from the secondary region and restore the library within the stated recovery objective.

---

### User Story 5 - Deliver changes safely and repeatedly (Priority: P5)

As an operator, I can rely on GitHub Actions to validate each proposed change, produce a traceable release artifact, deploy it through protected environments, and roll it back without losing library data.

**Why this priority**: The system stores irreplaceable photographs and history, so repeatable validation, controlled delivery, and recovery are necessary before production operation.

**Independent Test**: Submit a representative pull request, observe every required check, merge it, verify automatic non-production deployment, approve production promotion of the same artifact, inject a failed health check, and verify rollback preserves data and produces an auditable result.

**Acceptance Scenarios**:

1. **Given** a pull request, **When** a required test, security check, or build fails, **Then** GitHub reports a stable failing check and protected-branch rules prevent merge.
2. **Given** all required checks pass and a change reaches the protected default branch, **When** the delivery workflow runs, **Then** it builds or selects one immutable artifact and deploys it automatically to the non-production environment.
3. **Given** a verified non-production deployment, **When** an authorized operator approves a versioned production release, **Then** the identical artifact is promoted using protected production credentials and the release records its source revision and evidence.
4. **Given** deployment validation fails, **When** automatic or operator-initiated rollback runs, **Then** the prior healthy application version is restored without reversing or losing compatible persistent data.

### Edge Cases and Error Handling

- An image arrives before, after, or without its XMP sidecar; the library keeps the image usable and updates the edit state when a valid sidecar arrives.
- A sidecar is malformed, refers to a different image, or uses unsupported darktable features; the system preserves it, marks the issue, and does not replace the last valid committed state.
- Two devices make concurrent XMP edits or rename/move the same image; synchronization preserves both inputs and requires an explicit resolution when automatic reconciliation is unsafe.
- A commit covers multiple images and only part of the upload succeeds; the commit is not published as complete until every referenced XMP object is durable.
- A branch or tag name collides, a commit message is empty, or an unauthorized history rewrite is attempted; the action is rejected with a corrective message.
- The source image, XMP revision, darktable runtime, or rendering dependency is unavailable; the requested preview is marked unavailable without corrupting history.
- Metadata values differ between embedded image data and XMP; the UI applies a documented precedence rule and exposes the source of the displayed value.
- A file is deleted locally, remotely, or from the portal; deletion is recoverable for 90 days and does not immediately remove cold-storage copies.
- A backup is delayed, incomplete, or fails verification; status becomes visibly degraded and an operational alert is raised.
- A cold restore takes hours; the user sees its estimated stage and can continue read-only access to unaffected online data.
- A workflow is re-run, superseded by a newer commit, triggered from a fork, or interrupted midway; required results remain attributable to the exact revision, obsolete deployments are cancelled safely, and untrusted code receives no deployment credentials.
- A schema or storage migration cannot be rolled back safely; deployment stops before promotion or follows an approved forward-recovery procedure instead of pretending application rollback also reverses data.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST ingest supported original image files and associated darktable XMP sidecars while preserving the original image bytes and recording integrity checksums.
- **FR-002**: The system MUST associate each XMP sidecar with exactly one image identity and preserve XMP content byte-for-byte for every committed revision.
- **FR-003**: The system MUST extract and index darktable-relevant metadata including, at minimum, capture date/time, import date, filename/path, file type, camera, lens, focal length, aperture, shutter speed, ISO, dimensions, orientation, rating, rejected state, color labels, tags, title, description, creator, and geolocation when present.
- **FR-004**: The web gallery MUST support combined filtering and deterministic sorting over the indexed metadata, with active filters visible and removable.
- **FR-005**: The system MUST generate thumbnails and on-demand previews by applying a selected XMP revision to its source image using a supported darktable command-line runtime.
- **FR-006**: The system MUST cache rendered results while retaining enough provenance to determine the source image, XMP revision, darktable version, rendering settings, and generation time.
- **FR-007**: The system MUST provide a dedicated lineage view that represents commits, parent relationships, branches, tags, merges, authors, timestamps, and messages for the selected scope.
- **FR-008**: Commits and branches MUST operate across the library so one atomic commit can contain XMP changes for multiple images; an image-focused lineage projection MUST show only the commits and branch relationships relevant to a selected image.
- **FR-009**: Authorized users MUST be able to stage XMP changes, review the staged set, create commits with messages, create and switch branches, tag commits, compare revisions, merge branches, resolve conflicts, and restore or revert a prior state through the web interface.
- **FR-010**: The default branch MUST reject amend, rebase, reset, force-move, and other history-rewriting operations. A non-default branch MAY be amended, rebased, reset, or force-moved only after showing the affected commits and receiving explicit confirmation; displaced commits MUST remain recoverable and the operation MUST be audited.
- **FR-011**: A commit MUST either durably reference every selected XMP revision or fail without publishing a partial commit.
- **FR-012**: The system MUST keep uncommitted synchronized XMP changes distinguishable from the branch's committed state and show added, modified, deleted, conflicted, and clean status.
- **FR-013**: The system MUST detect divergence from a shared base revision and MUST NOT silently overwrite concurrent XMP edits.
- **FR-014**: For automatically mergeable XMP changes, the system MUST preview the proposed result before commit; for conflicting edits, it MUST retain both versions and require an explicit user resolution.
- **FR-015**: The Windows integration MUST expose authorized images and XMP sidecars at stable local paths that darktable can read and write.
- **FR-016**: The Windows integration MUST present a connected virtual folder, fetch unpinned files on demand, and allow users to pin individual files or folders as durable local working copies for offline reading and editing. It MUST synchronize pinned changes after reconnection and clearly distinguish online-only, downloading, locally available, pinned, pending-upload, synchronized, and conflicted states.
- **FR-017**: The Windows integration MUST surface synchronization progress, last successful synchronization, pending changes, conflicts, unavailable files, and quota or storage errors.
- **FR-018**: The system MUST preserve file moves and renames without losing image identity or XMP history.
- **FR-019**: The portal MUST permit authorized import, download, non-destructive deletion, and restoration of images and XMP sidecars.
- **FR-020**: The system MUST create independent cold-storage recovery copies of original images, committed XMP objects, commit graph data, and metadata needed to reconstruct the library.
- **FR-021**: The portal MUST show backup coverage, most recent successful backup, verification result, and recoverability status.
- **FR-022**: Authorized operators MUST be able to select a recovery point, restore it into an isolated location, verify integrity, and deliberately promote it without overwriting the active library by default.
- **FR-023**: The system MUST authenticate every user, authorize every library and history operation, and record security-sensitive actions including access changes, imports, commits, merges, restores, deletes, and recovery operations.
- **FR-024**: The system MUST provide actionable failure states and safe retries for uploads, synchronization, indexing, rendering, commits, merges, and backup/restore operations without duplicating or corrupting durable data.
- **FR-025**: The web gallery MUST allow the owner to edit ratings, rejected state, color labels, tags, title, description, and geolocation. Each web edit MUST create an uncommitted XMP working change subject to the same staging, diff, commit, conflict, synchronization, authorization, and audit rules as a Windows-originated XMP change.
- **FR-026**: Each Windows sync device MUST maintain its own checked-out branch and working state. Switching one device's branch MUST update only that device's working folder, MUST preserve or explicitly resolve its uncommitted changes before checkout, and MUST NOT change another device's checked-out branch or files.
- **FR-027**: The owner MUST be able to hide and unhide selected commits without deleting or rewriting them. Hidden commits MUST be omitted or collapsed in normal history and lineage views, MUST NOT change descendant content or parentage, and MUST remain discoverable through an explicit show-hidden control, direct references, recovery workflows, and audit records.
- **FR-028**: Deleted image assets and commits displaced by an authorized non-default-branch rewrite MUST remain recoverable for 90 days from the recorded deletion or displacement time. After that window, an authorized permanent-purge process MUST remove eligible primary and backup copies according to documented storage-lifecycle timing and MUST produce an audit record. Hidden commits are retained history and MUST NOT enter this purge lifecycle merely because they are hidden.
- **FR-029**: The production service MUST keep its primary workload resilient across multiple availability zones within one AWS region and MUST maintain independently recoverable cold copies in a second AWS region without requiring the primary region to perform a restore.
- **FR-030**: GitHub Actions MUST run the complete applicable validation suite for every pull request and protected-branch change, including formatting or linting, static/type checks, unit tests, integration tests, build/package verification, infrastructure validation, and security and dependency checks.
- **FR-031**: Required GitHub Actions checks MUST use stable names and protected-branch rules MUST prevent merge when a required check is missing, cancelled, or failing.
- **FR-032**: The delivery process MUST produce one immutable, checksummed release artifact per releasable revision and MUST promote that same artifact through non-production and production rather than rebuilding it per environment.
- **FR-033**: Each release artifact MUST be traceable to its source revision, workflow run, dependency lock state, software bill of materials, provenance evidence, test results, and security results.
- **FR-034**: A successful protected-default-branch change MUST deploy automatically to a non-production environment and run post-deployment health and smoke validation.
- **FR-035**: Production deployment MUST require a versioned release, successful required evidence, and approval through a protected GitHub environment by an authorized operator; direct deployment from pull-request workflows MUST be prohibited.
- **FR-036**: Deployment workflows MUST authenticate to AWS using short-lived identity federation, use least-privilege permissions, isolate environments, and prevent untrusted pull-request code from accessing deployment identities or secrets.
- **FR-037**: Third-party GitHub Actions and build dependencies MUST be reviewed, locked through the ecosystem's integrity mechanism, and pinned to immutable versions; action permissions, maintenance, provenance, licenses, advisories, and transitive impact MUST be recorded or automatically verifiable.
- **FR-038**: Delivery workflows MUST provide concurrency control, timeouts, deliberate retry behavior, artifact retention, deployment status, failure ownership, and auditable approval and rollback events.
- **FR-039**: Operators MUST be able to roll back an application or infrastructure release to the last compatible healthy artifact through GitHub Actions; database or persistent-state changes MUST use an approved backward-compatible rollback or forward-recovery strategy.

### Acceptance Criteria

- **AC-001**: A test collection containing supported RAW and rendered formats can be imported from Windows, committed, retrieved on another Windows installation, and verified byte-for-byte for originals and XMP revisions.
- **AC-002**: Gallery results match expected combinations of rating, color label, tag, date, camera, lens, exposure, filename, and location filters and remain deterministically sorted across repeated requests.
- **AC-003**: A current revision and at least three historical XMP revisions of the same RAW file can be selected and rendered, and each output is traceable to its exact inputs and darktable version.
- **AC-004**: A divergent-history test preserves both branches, displays the correct graph, prevents silent overwrite, supports conflict resolution, and produces a merge commit or explicit non-merge resolution.
- **AC-005**: Staging and committing changes to multiple images either publishes one complete commit or publishes none when any referenced upload fails.
- **AC-006**: Deleting or restoring a local file, moving it, and editing it while disconnected produces documented, non-destructive synchronization outcomes after reconnection.
- **AC-007**: A recovery exercise reconstructs all sampled originals, XMP objects, commit relationships, metadata, and permissions from cold storage and reports checksum or graph-integrity failures.
- **AC-008**: An unauthorized user cannot view, download, render, alter, or recover library data, and each attempted sensitive action produces an auditable event without exposing secrets.
- **AC-009**: Each supported gallery metadata edit produces a valid XMP working change that is visible on Windows after synchronization, remains absent from branch history until committed, and round-trips through the supported darktable version without losing unrelated XMP content.
- **AC-010**: Two Windows devices can check out different branches, synchronize branch-specific XMP states, and switch independently; a device with uncommitted changes cannot lose or silently move those changes during checkout.
- **AC-011**: Attempts to rewrite the default branch are rejected; a confirmed rewrite of a non-default branch reports the affected commits, updates the intended branch reference, retains displaced commits for recovery, and produces an audit event.
- **AC-012**: Hiding an intermediate commit removes it from the normal history list and collapses it in the normal lineage graph without changing any descendant render or XMP state; show-hidden and unhide restore its complete presentation and audit context.
- **AC-013**: A deleted image and a commit displaced by branch rewriting can each be restored with intact identity and history at any point through day 90; after an authorized purge becomes effective, neither remains retrievable through normal, recovery, or backup workflows, and the purge audit identifies its scope and completion time.
- **AC-014**: A recovery exercise performed with simulated loss of the primary AWS region retrieves the secondary-region cold copy without reading primary-region data and reconstructs the sampled library within the 24-hour recovery objective.
- **AC-015**: A pull request with an intentional failure cannot merge; after correction, every required check reports success under its stable check name for the exact head revision.
- **AC-016**: A merged revision automatically reaches non-production, and an approved versioned release promotes the identical artifact digest to production with traceability to source, dependencies, SBOM, provenance, tests, and security evidence.
- **AC-017**: A fork-originated or otherwise untrusted pull request can run safe validation but cannot read production secrets, obtain an AWS deployment identity, approve an environment, upload a trusted release artifact, or deploy.
- **AC-018**: A failed production health check blocks or reverses promotion to the last compatible healthy artifact, preserves persistent data, and records the failure, decision, actor, artifact identities, and validation outcome.
- **AC-019**: Every third-party action in an implemented workflow is pinned to a reviewed full commit identity, uses only necessary permissions, and has documented source, update strategy, and risk review.

### Key Entities

- **Library**: An access-controlled collection of images, metadata, branches, commits, and recovery policy.
- **Image asset**: A stable logical photograph identity with an original binary, integrity checksum, format, current path, import state, and lifecycle status.
- **XMP revision**: Immutable sidecar content associated with an image, plus checksum, parsed metadata, compatibility state, and provenance.
- **Working change**: The current uncommitted difference between a synchronized XMP file and the selected branch head.
- **Commit**: An immutable authored change set with message, timestamp, one or more parents, references to XMP revisions, and a reversible presentation visibility state; branch references may move without changing the commit object.
- **Branch**: A movable named reference to a commit and the working context used for new commits.
- **Tag**: An immutable human-readable reference to a commit.
- **Conflict**: Concurrent changes that cannot be safely reconciled without a chosen result.
- **Rendered preview**: A derivative linked to an image, XMP revision, darktable runtime version, render settings, checksum, and cache status.
- **Metadata record**: Searchable normalized values and their source/provenance for an image at a defined revision.
- **Sync device**: An authorized Windows installation with its local state, selected library/branch, synchronization cursor, and health status.
- **Backup recovery point**: A verified, time-bounded set of objects and indexes sufficient to reconstruct a library.
- **Audit event**: A tamper-evident record of a security-sensitive or history-changing action.

## Non-Functional Requirements

- **NFR-001 Performance**: For a library of 100,000 images, 95% of gallery filter/sort interactions MUST show the first page within 2 seconds; a cached thumbnail within 500 ms; and an uncached preview MUST begin showing progress within 2 seconds and finish within 30 seconds for the agreed reference RAW set. Verification: representative load test and render benchmark.
- **NFR-002 Scalability**: The initial release MUST support at least 100,000 images, 1,000,000 committed XMP revisions, 10 concurrently active browser sessions, and 5 active sync devices per library without violating other stated targets. Verification: capacity test.
- **NFR-003 Availability**: Browse, history, and synchronization services MUST achieve 99.9% monthly availability, excluding announced maintenance; cached browsing SHOULD remain available when rendering is degraded. Verification: service-level monitoring.
- **NFR-004 Reliability and recovery**: Durable primary data MUST span multiple availability zones, have a recovery point objective of 1 hour, and have a recovery time objective of 24 hours from an independent cold copy in a second AWS region; restore drills MUST be completed at least quarterly with simulated primary-region loss, checksum validation, and commit-graph validation. Verification: backup telemetry and restore reports.
- **NFR-005 Security**: All network traffic and stored originals, sidecars, metadata, credentials, backups, and audit data MUST be encrypted; access MUST be least-privilege; privileged and recovery actions MUST require stronger re-authentication; known critical vulnerabilities MUST block release. Verification: access tests, configuration review, dependency scan, and penetration testing before production launch.
- **NFR-006 Privacy**: Libraries MUST be private by default; metadata such as geolocation MUST not be exposed outside authorized users; the owner MUST be able to export data and request permanent deletion after the 90-day recovery window, with completion bounded by documented storage-lifecycle timing. Verification: privacy test matrix and deletion/recovery audit.
- **NFR-007 Accessibility**: Core web workflows MUST conform to WCAG 2.2 AA, including keyboard navigation, visible focus, text alternatives for meaningful images, scalable text, and non-color-only status indicators. Verification: automated checks plus manual keyboard and screen-reader testing.
- **NFR-008 Compatibility**: The product MUST support the two most recent stable darktable release lines selected during planning, current supported Windows 11 releases, and current desktop versions of Chrome, Edge, and Firefox; compatibility failures MUST be reported rather than silently changing edits. Verification: compatibility matrix.
- **NFR-009 Observability**: Operators MUST receive metrics, structured logs, and traceable operation identifiers for ingest, sync, indexing, rendering, commit, merge, backup, and restore; failed backups and sustained error-rate or latency breaches MUST alert within 15 minutes. Verification: fault-injection exercise.
- **NFR-010 Maintainability**: Metadata mappings, supported formats, darktable compatibility, storage lifecycles, and retention policies MUST be versioned and testable without rewriting unrelated product components. Verification: design review and contract/regression tests.
- **NFR-011 Testability**: Automated tests MUST cover XMP round trips, commit graph invariants, conflict preservation, metadata search, authorization boundaries, synchronization recovery, render provenance, backup restoration, workflow security boundaries, deployment health checks, and rollback; critical history, authorization, and delivery paths require end-to-end tests. Verification: CI evidence and traceability matrix.
- **NFR-012 Localization**: The initial release MAY provide English UI only, but dates, times, numbers, and time zones MUST be locale-aware and stored without losing the original capture-time context. Verification: tests using at least three locale/time-zone combinations.
- **NFR-013 Deployment and rollback**: Production changes MUST be deployable without losing stored images or history and MUST have a tested rollback that preserves data written during the failed rollout. Verification: pre-production deployment and rollback exercise.
- **NFR-014 Operations**: Documented runbooks MUST cover darktable worker failure, synchronization backlog, corrupt objects, index rebuild, key rotation, backup failure, isolated recovery, CI outage, compromised workflow credentials, failed deployment, and rollback or forward recovery; every runbook MUST identify an owner and escalation condition. Verification: operational readiness review.
- **NFR-015 Cost**: The design MUST expose per-library online storage, cold storage, transfer, indexing, and rendering consumption plus GitHub Actions runner, cache, and artifact consumption, and support configurable budgets and alerts; planning MUST estimate monthly cost for the reference 100,000-image library and expected delivery frequency. Verification: cost model reconciled with representative billing reports.
- **NFR-016 Compliance**: No regulated-industry certification is assumed for v1; applicable privacy, copyright, export, and deletion obligations for the chosen launch jurisdictions MUST be documented before production use. Verification: product-owner/legal sign-off or explicit private-use exemption.
- **NFR-017 CI feedback**: At least 95% of ordinary pull-request validation runs MUST complete within 15 minutes, and the first actionable failure MUST be visible within 5 minutes. Verification: workflow-duration telemetry over a rolling 30-day window.
- **NFR-018 Delivery performance**: A healthy protected-default-branch revision MUST reach non-production within 20 minutes of validation, and an approved production promotion MUST complete or safely roll back within 30 minutes, excluding explicitly announced migrations. Verification: deployment telemetry and quarterly rollback exercises.
- **NFR-019 CI/CD security and provenance**: Workflows MUST default to read-only repository permissions, elevate only the jobs that require it, use no long-lived AWS credentials, expose no secrets in logs or artifacts, and produce verifiable provenance and an SBOM for deployable artifacts. Critical or high exploitable dependency or workflow findings MUST block release. Verification: workflow permission review, secret-leak tests, dependency review, artifact verification, and security scanning.
- **NFR-020 CI/CD reliability**: Duplicate delivery, runner loss, retry, cancellation, and superseding commits MUST not produce concurrent conflicting production changes or an untraceable partial release. Verification: fault-injection tests and deployment audit review.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: At least 95% of first-time test users can import, edit, synchronize, and commit an XMP change without assistance in under 10 minutes.
- **SC-002**: At least 95% of test users can find a known photograph using metadata filters in under 30 seconds in a 100,000-image reference library.
- **SC-003**: Test users can identify the parentage of a selected revision, render it, and create a safe restoration commit in under 3 minutes without losing any existing revision.
- **SC-004**: Across forced disconnect, restart, duplicate-delivery, and concurrent-edit tests, no acknowledged original or committed XMP object is lost or silently overwritten.
- **SC-005**: Quarterly recovery exercises restore 100% of sampled originals and committed XMP revisions with matching checksums and a valid commit graph within 24 hours.
- **SC-006**: At least 99% of supported images have a usable gallery thumbnail within 5 minutes of completed upload under the reference workload; failures are visible and retryable.
- **SC-007**: Every production release is attributable to one reviewed source revision and one immutable artifact, with complete required test, security, SBOM, provenance, approval, deployment, and rollback evidence retained for the defined audit period.
- **SC-008**: In quarterly exercises, operators can identify and restore the last compatible healthy release through GitHub Actions within 30 minutes without losing committed library data.

## Constraints, Assumptions, and Dependencies

### Constraints

- Production hosting is on AWS; the primary service spans availability zones in one region and the independently recoverable cold-storage tier resides in a second region.
- Server-side rendering uses the darktable command-line interface and must isolate untrusted or malformed media inputs.
- The desktop integration targets Windows and must present ordinary image and XMP files at paths darktable can consume.
- The product follows Git concepts and user expectations but versions XMP sidecars and library state rather than acting as a general Git host.
- Continuous integration, artifact production, environment promotion, production approval, and rollback are orchestrated through GitHub Actions; exact workflow commands and matrices must come from the approved technical plan and repository toolchain.

### Assumptions

- Original images are immutable after ingestion; replacement creates a distinct asset or explicitly audited new binary state.
- Common darktable-supported RAW formats plus JPEG, TIFF, and PNG form the initial format set; the exact compatibility matrix is finalized during planning.
- XMP is the authoritative edit representation; the server does not depend on a synchronized darktable database file for version history.
- Destructive purge is separate from ordinary deletion, requires explicit confirmation, begins only after the 90-day recovery window, and completes according to documented backup-storage lifecycle timing.
- Preview outputs are derivatives and can be regenerated; original images, committed XMP revisions, graph data, access policy, and reconstruction metadata are durable records.
- Exact merge rules for darktable XMP fields will be defined and validated during technical planning; ambiguous edit-pipeline changes default to a conflict rather than an unsafe automatic merge.
- The default delivery model automatically deploys the protected default branch to non-production and promotes a versioned release to production only after protected-environment approval.

### Dependencies

- Supported darktable releases and their command-line rendering and XMP behavior.
- Windows filesystem synchronization capabilities and darktable's handling of file change notifications, partial files, sidecars, moves, and offline paths.
- AWS services selected during planning for multi-availability-zone online objects, metadata/indexing, compute, identity, monitoring, and second-region archival storage.
- A representative, legally usable image/XMP corpus for metadata, rendering, compatibility, scale, and corruption tests.
- GitHub repository branch protection, protected environments, Actions runners, artifact/provenance support, and AWS identity federation.

## Risks

- Darktable XMP is structured edit state rather than line-oriented source text; naïve text merges can create valid XML with incorrect visual results.
- Rendering untrusted image files invokes complex native codecs and requires strong isolation, resource limits, patching, and failure containment.
- A OneDrive-like Windows provider is a substantial product surface with offline, hydration, rename, conflict, and upgrade semantics.
- RAW files are large, so online storage, egress, preview generation, and restore costs can dominate operating cost.
- Darktable version changes can alter metadata interpretation or rendered output; provenance and compatibility policy are essential.
- Cold storage provides low cost but not instant recovery; recovery expectations and testing must match archive retrieval times.
- CI/CD compromise has production reach; overly broad workflow permissions, mutable actions, poisoned caches or artifacts, and unsafe pull-request triggers can bypass otherwise strong application controls.
- Slow or flaky required checks can block delivery, while environment drift or non-backward-compatible data changes can make rollback unsafe.

## Open Questions

- During planning, define the supported darktable and image-format compatibility matrix, XMP-aware merge rules, exact AWS regions and services, backup-storage lifecycle timing after the 90-day recovery window, reference cost profile, GitHub Actions triggers and stable check names, runner/runtime matrix, local-equivalent validation commands, artifact retention, release versioning, environment protection, and migration/rollback strategy.
