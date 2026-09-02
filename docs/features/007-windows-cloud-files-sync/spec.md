# Feature Specification: Windows Cloud Files Synchronization

**Feature Branch**: `feature/007-windows-cloud-files-sync`

**Created**: 2026-09-02

**Status**: Draft — awaiting product-owner approval

**Owner**: Project maintainer

**Parent Program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Depends On**: Features 004–006 contracts

**Input**: Present a OneDrive-style Windows folder that darktable can use, with online placeholders, on-demand hydration, recursive pinning, offline editing, safe upload, per-device branch checkout, visible status, and conflict preservation.

## Problem Statement

The library must feel like ordinary files to darktable while the authoritative data lives remotely. A basic network mount is unreliable offline and a full mirror can exceed local storage. The Windows integration must bridge remote versions to stable local paths without exposing partial files, losing edits, or letting one device's branch switch affect another.

## Goals

- Expose a stable Windows folder containing image and XMP files darktable can read and write.
- Fetch online-only files transparently and let users pin files/folders for offline work.
- Synchronize local XMP changes after stable writes and reconnection without silent overwrite.
- Maintain an independent checked-out branch and working state per device.
- Make hydration, upload, conflict, authentication, quota, and health states visible and recoverable.

## Non-Goals

- Synchronizing darktable `library.db`, `data.db`, presets, styles, configuration, or caches.
- Replacing darktable's own import/catalog behavior.
- Implementing server commit/branch semantics; feature 006 owns them.
- Rendering thumbnails/previews or providing the web lineage/gallery interfaces.
- Supporting Windows versions outside the approved Windows 11 compatibility matrix.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse and hydrate a remote library (Priority: P1)

As the owner, I can browse the library in File Explorer and open an online-only image/XMP pair in darktable, causing complete verified local files to become available without manual downloading.

**Why this priority**: Transparent darktable access is the integration's primary value.

**Independent Test**: Register a fresh device, enumerate 100,000 placeholders, open representative files through darktable, interrupt hydration, retry, and verify bytes/status/path identity.

**Acceptance Scenarios**:

1. **Given** an authenticated device and selected library branch, **When** its folder is opened, **Then** the current branch namespace appears with stable ordinary image/XMP paths and online-only status without downloading all bytes.
2. **Given** an online-only file, **When** darktable reads it, **Then** hydration supplies the complete checksummed content or a clear error; no partial content is presented as complete.
3. **Given** interrupted hydration, **When** connectivity resumes, **Then** the operation safely resumes/retries and converges to exact authoritative bytes.

---

### User Story 2 - Pin and edit offline (Priority: P2)

As the owner, I can pin an active project, disconnect, edit XMP files with darktable, and synchronize the stable local changes after reconnection.

**Why this priority**: The approved experience requires network-style browsing plus dependable offline work.

**Independent Test**: Pin a nested folder, verify recursive availability, disconnect, edit/rename XMP in darktable, restart the provider, reconnect, and verify one preserved working change per edit.

**Acceptance Scenarios**:

1. **Given** a file or folder selected for pinning, **When** pinning completes, **Then** every requested item is fully available offline and status identifies any exception.
2. **Given** pinned files and no connectivity, **When** darktable reads originals and updates XMP, **Then** reads/writes succeed locally and durable pending-change state survives restart.
3. **Given** stable offline changes, **When** connectivity returns, **Then** each change uploads once against its recorded base and becomes server working state without an implicit commit.

---

### User Story 3 - Switch branches and resolve conflicts safely (Priority: P3)

As the owner, I can independently select a branch per device and receive explicit protection when checkout or synchronization would overwrite dirty/divergent files.

**Why this priority**: Git-like branching is unsafe if the filesystem adapter treats remote state as last-writer-wins.

**Independent Test**: Use two devices on different branches, create concurrent XMP edits, attempt checkout with dirty state, resolve the conflict, and prove neither input is lost.

**Acceptance Scenarios**:

1. **Given** two devices, **When** each checks out a different branch, **Then** each folder shows its branch state and switching one does not change the other.
2. **Given** uncommitted local changes, **When** branch checkout is requested, **Then** checkout blocks until the owner commits, stashes/preserves, or explicitly discards those changes.
3. **Given** local and remote XMP changes from one base, **When** synchronization detects divergence, **Then** both versions remain recoverable, the local file is not silently overwritten, and a conflict is shown for later resolution.

### Edge Cases and Error Handling

- File Explorer, darktable, antivirus, and the provider read/write one file concurrently; only a stable closed byte state is uploaded.
- A file is hydrated while its branch/path changes remotely; the requested identity either completes consistently or restarts against the new state.
- Disk space runs out during pin/hydration; partial bytes are cleaned or retained as explicitly incomplete and authoritative status is unchanged.
- A case-only rename, invalid Windows character, reserved filename, overly long path, or case-collision occurs; a deterministic safe local representation preserves logical identity.
- The user edits an unpinned hydrated file and Windows requests dehydration; dirty content remains local until safely uploaded/resolved.
- The provider crashes/restarts during callbacks, upload, checkout, rename, or cursor application; the journal replays idempotently.
- Authentication expires during darktable access; clean local/pinned reads continue, remote operations pause, and reauthentication does not discard state.
- A branch delta cursor is expired or inconsistent; the device performs verified reconciliation rather than guessing or deleting dirty files.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The owner MUST be able to register/revoke a Windows device and select one local sync root for the library.
- **FR-002**: Each device MUST maintain an independent checked-out branch, synchronization cursor, working state, and health state.
- **FR-003**: The sync root MUST expose stable ordinary image/XMP paths and online-only placeholders for the selected branch without pre-downloading the full library.
- **FR-004**: Each placeholder MUST bind to a versioned opaque asset/edit/content identity independent of mutable path and contain no credential or sensitive metadata.
- **FR-005**: Reading an online-only file MUST hydrate the requested complete authoritative bytes with size/hash validation and retry/cancellation semantics.
- **FR-006**: The owner MUST be able to pin/unpin individual files and folders; folder pinning MUST recurse and expose per-item completion/error state.
- **FR-007**: Clean unpinned hydrated files MAY be dehydrated; dirty, pending, conflicted, or explicitly pinned content MUST NOT be automatically removed.
- **FR-008**: Offline reads of available/pinned files and writes to local XMP files MUST work without connectivity and survive provider/device restart.
- **FR-009**: The provider MUST detect a stable completed local change before hashing/uploading it and MUST never publish a partial write.
- **FR-010**: Every upload/move/delete MUST include the recorded base identity/version and idempotency identity; stale/divergent state MUST become a conflict rather than overwrite.
- **FR-011**: Uploaded XMP changes MUST become uncommitted server working changes and MUST NOT create commits automatically.
- **FR-012**: Move/rename MUST preserve asset/edit identity and synchronize logical path changes; collision or unsupported local name MUST be explicit.
- **FR-013**: Checkout MUST refuse to overwrite dirty/conflicted/pending files and MUST offer commit, preserve/stash, or explicitly confirmed discard paths through feature 006 contracts.
- **FR-014**: Local durable journal state MUST record cursor, checked-out branch, identities, hashes, local sequence, pin/availability, pending operations, and conflicts without storing raw authentication secrets.
- **FR-015**: Explorer/companion status MUST distinguish online-only, hydrating, available, pinned, dirty, uploading, synchronized, paused/offline, authentication-required, quota/error, and conflicted states.
- **FR-016**: Device revocation MUST prevent new remote access while preserving local bytes for an explicit owner-directed cleanup/export decision.
- **FR-017**: The provider MUST expose actionable diagnostics and audit-safe operation identifiers without logging XMP/image content, tokens, or sensitive paths.
- **FR-018**: Installation/configuration guidance MUST state supported darktable XMP settings and explicitly exclude synchronization of darktable database/configuration files.

### Acceptance Criteria

- **AC-001**: A fresh device enumerates a 100,000-image namespace without downloading original bytes and opens the first directory within the stated performance target.
- **AC-002**: Hydrated originals/XMP match authoritative SHA-256 across interruption, cancellation, restart, and retry tests; no partial file is reported complete.
- **AC-003**: Recursive pinning makes 100% of a representative project available offline and reports every seeded disk/quota/download failure.
- **AC-004**: Offline darktable edits survive provider and OS restart and upload exactly once as uncommitted working changes after reconnection.
- **AC-005**: Two devices on different branches remain independent across checkout, synchronization, rename, and remote branch advance.
- **AC-006**: Dirty-state checkout and concurrent-edit tests lose or silently overwrite zero local or remote XMP versions.
- **AC-007**: Case, reserved-name, Unicode, long-path, rename, delete, lock, antivirus, disk-full, and expired-cursor tests produce deterministic non-destructive outcomes.
- **AC-008**: A revoked device cannot hydrate/upload/query remote state; retained local content follows the explicit cleanup/export decision.

### Key Entities

- **Sync device**: Owner installation identity, authorization/revocation, selected branch, cursor, version, and health.
- **Sync root**: Registered local namespace and provider/policy identity.
- **Placeholder**: Local path plus opaque remote asset/edit/content identity, size/hash, availability, pin, and sync state.
- **Hydration operation**: Requested ranges/full object, expected identity/hash, progress, retry, and outcome.
- **Local journal entry**: Monotonic local/remote sequence, base/current hash, operation type, durability, and acknowledgement.
- **Pending working change**: Stable local XMP bytes awaiting or completing server publication.
- **Sync conflict**: Base/local/remote identities, preserved local path/data, reason, and resolution state.
- **Checkout operation**: Old/new branch, dirty-state decision, delta cursor, progress, and result.

## Non-Functional Requirements

- **NFR-001 Performance**: First listing of 1,000 placeholders p95 ≤2 seconds; hydration begins p95 ≤2 seconds and sustains available network throughput without unnecessary full-library transfer. Verification: Windows benchmark.
- **NFR-002 Availability**: Available/pinned local files remain readable during server/network outage; remote sync meets 99.9% monthly availability. Verification: offline/outage tests.
- **NFR-003 Security**: Tokens use OS-protected storage; remote operations are owner/device scoped; placeholder/journal data contains no reusable credential. Verification: threat/access tests.
- **NFR-004 Privacy**: Default diagnostics omit filenames/paths, image/XMP bytes, geolocation, tokens, and owner secrets. Verification: log review.
- **NFR-005 Accessibility**: Companion setup/status/conflict/checkout controls meet WCAG 2.2 AA-equivalent Windows accessibility, keyboard, narrator, focus, and non-color status needs. Verification: manual/automated tests.
- **NFR-006 Compatibility**: Support current Windows 11 x64/ARM64 releases and darktable 5.6.x/5.4.x file behavior; incompatibility is reported. Verification: matrix.
- **NFR-007 Observability**: Local/server metrics cover hydration/upload latency/failure, cursor lag, dirty/conflict count, disk/quota/auth state, and crashes without private payload. Verification: fault injection.
- **NFR-008 Maintainability**: Filesystem API calls are isolated behind a versioned adapter; sync state transitions are explicit and replayable. Verification: architecture/state-model tests.
- **NFR-009 Testability**: Unit/model tests plus a dedicated resettable Windows 11 integration host cover callbacks, darktable access, offline/restart, races, paths, and fault recovery. Verification: CI evidence.
- **NFR-010 Localization**: UI is localizable; paths preserve Unicode; identities/state machines are locale-invariant. Verification: locale/path corpus.
- **NFR-011 Deployment**: Client update preserves sync-root registration/journal compatibility and can roll back while the prior version remains protocol-compatible. Verification: upgrade/rollback test.
- **NFR-012 Operations**: Runbooks cover auth expiry, stuck hydration/upload, journal repair, disk/quota, cursor reconciliation, revocation, and safe uninstall. Verification: exercise.
- **NFR-013 Cost**: Status reports downloaded/pinned/uploaded bytes and avoids duplicate transfer through verified content identity/range behavior. Verification: transfer accounting.
- **NFR-014 Compliance**: Installation, code signing, diagnostics consent, retained local data, and deletion/export behavior are documented before distribution. Verification: release review.
- **NFR-015 Reliability**: Across restart, concurrency, retry, disconnect, checkout, and conflict tests, no acknowledged or stable local XMP change is silently lost. Verification: fault matrix.
- **NFR-016 Scalability**: Support 100,000-image namespace, 5 devices per library, and multi-terabyte remote content without requiring equivalent local disk. Verification: capacity test.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A first-time owner can register a device, open one online-only photograph in darktable, and pin its folder in under 10 minutes.
- **SC-002**: 100% of sampled hydrated/pinned bytes match authoritative checksums after interruption and restart testing.
- **SC-003**: Offline/reconnection and concurrent-edit test matrices lose or silently overwrite zero stable XMP changes.
- **SC-004**: Two devices can use different branches for 24 hours of simulated activity without cross-checkout file changes.
- **SC-005**: At least 95% of usability testers can identify online-only, pinned, dirty, syncing, and conflicted states without assistance.

## Constraints, Assumptions, and Dependencies

### Constraints

- Windows 11 and a OneDrive-style connected virtual folder with pinning are approved product constraints.
- Each device has its own checked-out branch; local changes are working state, not automatic commits.
- darktable database/configuration synchronization is out of scope.

### Assumptions

- Feature 005 exposes stable image/edit/content/path identities and resumable transfers.
- Feature 006 exposes branch checkout/delta, expected-base working changes, conflicts, staging, and discard/preserve contracts.
- darktable is configured to write XMP and check for updated XMP according to supported guidance.
- Files are fully hydrated before darktable opens them; partial-range optimization must remain behaviorally equivalent.

### Dependencies

- [003 umbrella program](../003-darktable-version-control/spec.md).
- Feature 004 owner authentication, errors, audit, telemetry, client release/signing, and CI contracts.
- Features 005–006 storage and version-control APIs.
- A dedicated resettable Windows 11 test host and trusted production signing mechanism before release.

## Risks and Open Questions

- Cloud filesystem callbacks interact with darktable, antivirus, Explorer, and OS policies; the fault matrix is a release gate.
- Desktop test infrastructure can become privileged; untrusted pull requests cannot use credentialed/self-hosted end-to-end runners.
- Local disk cleanup can be mistaken for deletion; pin/dehydrate/delete states must remain distinct.
- No material product ambiguity remains. Exact Windows API adapter, local database, installer/signing, token custody, and protocol paging are planning decisions.
