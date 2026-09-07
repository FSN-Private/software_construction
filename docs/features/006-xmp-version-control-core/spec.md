# Feature Specification: XMP Version-Control Core

**Feature Branch**: `feature/006-xmp-version-control-core`

**Created**: 2026-09-02

**Status**: Draft — awaiting product-owner approval

**Owner**: Project maintainer

**Parent Program**: [003 Darktable Version-Controlled Image Library](../003-darktable-version-control/spec.md)

**Depends On**: Features 004 and 005 contracts

**Input**: Provide a Git-like, content-preserving version-control engine for darktable XMP sidecars with library-wide atomic commits, per-image lineage projections, working/staging state, branches, tags, protected history, recoverable rewriting, hidden versions, and conflict preservation.

## Problem Statement

Ordinary file history does not express coherent edits across photographs, branch experiments, merges, or recoverable history changes. The library needs an immutable and auditable XMP history model that behaves like Git where useful while protecting image edits from silent overwrite or accidental loss.

## Goals

- Preserve every committed XMP byte stream and its exact relationship to image/edit identity.
- Create atomic library-wide commits containing changes to one or many images.
- Support working changes, staging, commits, branches, tags, checkout state contracts, compares, and reversions.
- Protect the default branch while permitting confirmed rewriting of non-default branches with a 90-day recovery reference.
- Expose image-focused lineage projections without changing the underlying library-wide graph.
- Preserve both sides of divergence and provide stable primitives for later merge/conflict UI.

## Non-Goals

- A general Git server, Git wire protocol, or compatibility with arbitrary Git clients.
- Versioning original image binary changes as ordinary XMP commits.
- Windows placeholder/hydration implementation.
- Automatic semantic XMP merge or its user interface; feature 010 owns merge policy and interaction.
- Rendering revisions or building the visual lineage interface.
- Physical deletion from cold recovery storage.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Stage and commit coherent XMP changes (Priority: P1)

As the library owner, I can review uncommitted XMP changes, stage a selected set across images, and create one atomic named commit without losing prior revisions.

**Why this priority**: Atomic durable history is the core product distinction and a dependency of sync and UI features.

**Independent Test**: Create working changes for multiple images, stage a subset, commit it, inject an object failure, retry, and verify graph/state/object integrity and exact prior-version recovery.

**Acceptance Scenarios**:

1. **Given** valid working XMP changes based on the selected branch head, **When** the owner stages and commits them with a message, **Then** one immutable commit records author, time, parent, message, ordered changes, and exact XMP identities and advances the branch atomically.
2. **Given** a selected multi-image staged set with one unavailable/invalid object, **When** commit is attempted, **Then** no partial commit or branch advance is published and every working change remains recoverable.
3. **Given** uncommitted changes not selected for staging, **When** another staged set is committed, **Then** the unselected changes remain distinguishable and are not included silently.

---

### User Story 2 - Branch and compare safely (Priority: P2)

As the library owner, I can create/switch branches, tag commits, compare states, detect divergence, and create restore/revert commits while the default branch remains protected.

**Why this priority**: Experimentation and understandable lineage require stable references and no silent overwrite.

**Independent Test**: Create divergent branches, tags, compares, a revert, a stale-head commit, and a default-branch rewrite attempt; verify expected graph/ref outcomes.

**Acceptance Scenarios**:

1. **Given** a commit, **When** the owner creates a branch or tag, **Then** the reference resolves to that exact commit and name collisions are rejected deterministically.
2. **Given** two devices or clients advancing the same branch from one head, **When** the second submits a stale expected head, **Then** its ref update is rejected, both XMP revisions remain preserved, and divergence is reported.
3. **Given** a historical commit, **When** the owner requests restoration or revert, **Then** a new commit/branch state is produced without deleting intervening commits.
4. **Given** the protected default branch, **When** amend, rebase, reset, force-move, or deletion is attempted, **Then** the operation is rejected and audited.

---

### User Story 3 - Clean and recover history presentation (Priority: P3)

As the library owner, I can rewrite a non-default branch after reviewing impact, recover displaced commits for 90 days, and hide noisy commits from normal views without deleting or changing them.

**Why this priority**: Advanced control is useful only if cleanup remains recoverable and auditable.

**Independent Test**: Rewrite a non-default branch, resolve old/new refs, hide an intermediate commit, rebuild projections, recover the displaced head on day 90, and verify commit content/parentage never changed.

**Acceptance Scenarios**:

1. **Given** a non-default branch and an explicit affected-commit preview, **When** the owner confirms a rewrite, **Then** only the branch reference moves, displaced commits receive a 90-day recovery reference, and an audit event records the operation.
2. **Given** a visible commit, **When** the owner hides it, **Then** normal projections collapse/omit it while direct lookup, real parentage, descendants, recovery, audit, show-hidden, and unhide remain intact.
3. **Given** a displaced commit within 90 days, **When** the owner recovers it, **Then** a named reference makes the same immutable commit reachable again.

### Edge Cases and Error Handling

- Two commits race to advance one branch; exactly one expected-head update wins and the loser becomes an explicit divergence, not lost data.
- A commit request is repeated after timeout; idempotency returns the original commit and never creates a second logical change.
- A staged XMP object is deleted, quarantined, corrupt, or belongs to another asset/library; commit fails before ref publication.
- A graph references a missing parent/object or forms a cycle through corruption; integrity validation quarantines the affected ref and blocks advancement.
- A branch/tag uses an invalid, reserved, case-colliding, or existing name; creation is rejected with the normalized conflict.
- A commit is hidden while it is a branch/tag head; the reference remains real and hidden state affects presentation only.
- A rewrite would displace commits also reachable from another ref; recovery status reflects continued reachability rather than duplicating commits.
- A 90-day reflog expiry occurs while a hold/reference exists; physical removal remains blocked.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every XMP revision used by history MUST reference byte-exact immutable content and one image/edit identity from feature 005.
- **FR-002**: The engine MUST maintain explicit added, modified, deleted, conflicted, and clean working states relative to a selected branch head.
- **FR-003**: The owner MUST be able to stage/unstage selected working changes across one or many images and review their base/current content identities before commit.
- **FR-004**: A commit MUST record immutable identity, one or more parents, author, authored/received times, message, ordered changed entries, and a versioned canonical manifest.
- **FR-005**: Publishing a commit and advancing its target branch MUST be atomic across every staged entry and MUST require the caller's expected branch head.
- **FR-006**: Repeated commit/ref requests with the same idempotency identity MUST return the original outcome and MUST NOT duplicate graph state.
- **FR-007**: Commits and branches MUST be library-wide; an image projection MUST return only commits that change the selected image plus required connecting/merge context.
- **FR-008**: The owner MUST be able to create/list/rename/delete non-protected branches, switch client checkout context through a versioned contract, and resolve each ref to an exact commit.
- **FR-009**: Tags MUST identify exact commits and be immutable under ordinary use; replacing a tag requires explicit delete/create confirmation and audit.
- **FR-010**: The engine MUST compare commits/refs by changed assets, paths, lifecycle, and XMP content identities and expose base/ours/theirs for divergence.
- **FR-011**: The engine MUST detect divergent changes from a shared base and preserve every input; it MUST NOT silently select last writer or publish an unresolved merge result.
- **FR-012**: Restore/revert MUST create a new reachable state/commit and MUST NOT mutate the historical commit being restored.
- **FR-013**: The default branch MUST reject amend, rebase, reset, force-move, deletion, and other history rewriting.
- **FR-014**: A non-default branch MAY be rewritten only after an impact preview and explicit confirmation; immutable commits never change, only refs do.
- **FR-015**: Each ref movement MUST append an immutable reflog/audit entry containing old/new head, actor, operation, time, reason, and recovery expiry where applicable.
- **FR-016**: Commits displaced by authorized rewriting or deletion MUST remain recoverable for 90 days and MUST remain beyond that while another ref, hidden-state retention, hold, or recovery rule requires them.
- **FR-017**: The owner MUST be able to hide/unhide a commit as reversible presentation metadata independent of immutable commit identity and parentage.
- **FR-018**: Graph/materialized projections MUST be rebuildable and verifiable from immutable commits/manifests/objects and MUST report missing, altered, cyclic, or cross-library references.
- **FR-019**: Branch/tag/commit operations and protected-history violations MUST produce attributable audit events without including XMP payloads.
- **FR-020**: The engine MUST expose stable versioned contracts for working state, staging, commits, refs, compare, divergence, reflog/recovery, visibility, graph traversal, and integrity verification to features 007 and 010.
- **FR-021**: The checkout/switch contract MUST detect dirty working state and either block, preserve/stash, or explicitly discard it only after owner confirmation, with an auditable outcome.
- **FR-022**: Commits, refs, reflogs, hidden states, and recovery references MUST publish and release lifecycle holds through the feature 005 storage contract so required image and XMP bytes cannot be purged while history requires them.
- **FR-023**: The engine MUST atomically publish a validated resolved merge result with its base, source, target, parent references, resolved XMP identities, audit evidence, and expected target-head advancement.

### Acceptance Criteria

- **AC-001**: A 100-image staged commit either publishes every selected XMP entry and advances once or publishes no commit/ref advance under every injected single-step failure.
- **AC-002**: Ten concurrent expected-head attempts against one branch produce one winner, nine explicit stale-head outcomes, and zero missing XMP revisions.
- **AC-003**: Replaying an identical commit request 100 times produces one commit identity and one branch transition.
- **AC-004**: A divergent graph with branches/tags/merge parents returns correct whole-library and image-focused projections and has no cycles or missing parents.
- **AC-005**: Every default-branch rewrite attempt is rejected; a confirmed non-default rewrite moves only the intended ref and creates a recoverable reflog entry.
- **AC-006**: A displaced head can be recovered through day 90 with identical commit/object hashes; expiry never removes content still reachable or held.
- **AC-007**: Hide/unhide changes normal projection only and produces identical XMP/commit hashes and descendant resolution before and after.
- **AC-008**: Seeded manifest/object/parent corruption is detected by integrity verification and blocks affected ref advancement.
- **AC-009**: Compare/divergence returns exact base/ours/theirs identities needed for a later semantic merge without choosing a winner.
- **AC-010**: A checkout fixture with dirty working changes blocks or preserves them according to the chosen explicit path and never silently moves or loses them.
- **AC-011**: A purge fixture cannot remove an image or XMP object required by a reachable, hidden, reflog, or recovery-held commit; it becomes eligible only after all holds release.
- **AC-012**: A resolved merge fixture records base/source/target evidence and resolved entries, creates the correct parents, and either atomically advances the expected target head or leaves no merge commit/ref change.

### Key Entities

- **Working change**: Asset/edit identity, selected branch/base commit, base/current XMP identities, path/lifecycle change, origin, and state.
- **Staging set**: Owner/client, ordered selected changes, expected head, version, and status.
- **Commit**: Immutable canonical change-set identity, parents, author/message/times, manifest, policy versions, and integrity state.
- **Commit entry**: One asset/edit change from prior to new XMP/path/lifecycle content.
- **Branch**: Movable named ref, protection state, head, version, and deletion/recovery state.
- **Tag**: Named immutable reference to one commit.
- **Reflog entry**: Ref transition with recovery deadline and reachability state.
- **Commit visibility**: Reversible presentation choice scoped to owner/library and audit history.
- **Materialized branch state**: Rebuildable asset/edit/path/XMP snapshot for one ref head.
- **Divergence/conflict set**: Base/ours/theirs identities and affected asset/field categories awaiting resolution.

## Non-Functional Requirements

- **NFR-001 Performance**: At reference scale, branch-head state for one asset returns p95 ≤200 ms; first page of a 10,000-node lineage projection p95 ≤2 seconds; a 100-image commit publishes p95 ≤2 seconds after content exists. Verification: graph/load tests.
- **NFR-002 Availability**: Read history remains available during worker degradation; commit/ref commands achieve 99.9% monthly availability. Verification: SLO monitoring.
- **NFR-003 Security**: Every graph query/command is library-owner authorized; commit/ref identities cannot be used to cross library boundaries. Verification: authorization matrix.
- **NFR-004 Privacy**: Audit/telemetry contains hashes/IDs and operation categories, not XMP payloads, filenames, geolocation, or descriptive metadata. Verification: log tests.
- **NFR-005 Accessibility**: Not applicable to the engine's internal UI-free core; feature 010 owns accessible presentation. Error contracts MUST include machine code and plain-language summary for accessible consumers.
- **NFR-006 Compatibility**: Manifests, commit IDs, graph/ref contracts, and policy versions are forward-readable and explicitly versioned. Verification: compatibility fixtures.
- **NFR-007 Observability**: Metrics cover commit/ref rate/latency/conflict, stale heads, graph verification, reflog expiry, and repair; integrity failure alerts within 15 minutes. Verification: fault injection.
- **NFR-008 Maintainability**: Domain invariants have one authoritative implementation and no storage/provider leakage in public contracts. Verification: architecture tests.
- **NFR-009 Testability**: Property/model tests cover DAG acyclicity, atomic ref updates, reachability, idempotency, projection rebuild, hidden state, and expiry/holds. Verification: CI evidence.
- **NFR-010 Localization**: Commit/ref identifiers and hashes are locale-invariant; user messages/times are localizable without changing identity. Verification: locale tests.
- **NFR-011 Deployment**: Graph schema/manifest changes use backward-compatible readers/writers and verified rebuild before contract removal. Verification: migration rehearsal.
- **NFR-012 Operations**: Runbooks cover stale/conflicted refs, graph corruption, projection rebuild, reflog recovery/expiry, and protected-ref incident. Verification: exercise.
- **NFR-013 Cost**: Storage/reporting distinguishes unique XMP bytes, graph rows, manifests, projections, audit, and retained displaced history; compaction never weakens recovery. Verification: inventory/cost report.
- **NFR-014 Compliance**: Commit/audit/recovery retention follows the umbrella privacy/deletion policy and documented jurisdictional review. Verification: policy mapping.
- **NFR-015 Reliability**: No acknowledged committed XMP or reachable commit is lost or silently changed across concurrency, retry, restart, rebuild, or ref rewrite. Verification: fault/property tests.
- **NFR-016 Scalability**: Support at least 1,000,000 XMP revisions/commit entries and 100,000 assets per library while meeting stated read/commit targets. Verification: capacity test.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Across concurrency and fault tests, 100% of acknowledged commits have complete immutable entries and exactly the intended ref transition.
- **SC-002**: The owner can identify working/staged changes and create a multi-image commit in under 2 minutes through a reference client.
- **SC-003**: No divergence, rewrite, retry, hidden-version, expiry, or rebuild test loses a committed XMP identity.
- **SC-004**: Whole-library and image-focused graph projections agree on 100% of seeded ancestry/reachability assertions.
- **SC-005**: Every seeded corruption or protected-history violation is detected before it can advance an affected ref.

## Constraints, Assumptions, and Dependencies

### Constraints

- XMP content and commit objects are immutable; mutable behavior is expressed through refs, working state, and presentation metadata.
- The default branch is protected; non-default rewriting remains recoverable for 90 days.
- Git concepts guide behavior, but Git storage/protocol compatibility is out of scope.

### Assumptions

- Feature 005 supplies verified immutable XMP/content and stable image/edit identity.
- Feature 007 assigns each device a checked-out branch through the contract defined here.
- Feature 010 supplies semantic automatic merge and human conflict resolution; this feature preserves divergence primitives and publishes only resolved content.
- Hidden commits remain retained history and never enter purge solely because hidden.

### Dependencies

- [003 umbrella program](../003-darktable-version-control/spec.md).
- Feature 004 identity/audit/error/idempotency/CI conventions.
- Feature 005 content, asset/edit identity, lifecycle holds, and reconstruction manifest.

## Risks and Open Questions

- A custom DAG can drift from expected Git semantics; contract examples and invariants must state every deliberate difference.
- Materialized state can diverge from immutable graph; it must be rebuildable and continuously verifiable.
- Very large commits/graphs can create lock or traversal pressure; planning must define bounded transaction/pagination/index strategy.
- No material product ambiguity remains. Exact canonicalization, commit-ID algorithm, persistence schema, transaction isolation, and repair mechanics are planning decisions.
