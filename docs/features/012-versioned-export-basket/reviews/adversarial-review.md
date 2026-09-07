# Adversarial Review: Versioned Export Basket

**Feature**: [012 Versioned Export Basket](../spec.md)
**Review date**: 2026-09-03
**Review type**: Local read-only adversarial specification review
**Status**: PASS - no valid unresolved findings remain

## Review Scope

- Checked against the approved umbrella program 003, especially exact darktable rendering, hidden versions, non-default branch rewriting, 90-day recovery, private access, and deletion/purge policy.
- Checked against sibling contracts in features 005, 006, 008, 009, 010, and 011 as available from their feature branches.
- Checked against repository constitution requirements for complete, testable, non-functional specification coverage before implementation.

## Findings and Dispositions

### AR-001 - Initial spec did not meet repository feature-spec completeness

- **Severity**: High
- **Evidence**: The initial 012 draft lacked explicit goals, non-goals, users/roles, constraints, detailed acceptance scenarios per story, and several non-functional categories required by `feature-documents.md`.
- **Impact**: Planning could proceed with ambiguous scope and weaker traceability than the constitution requires.
- **Disposition**: Valid and fixed. The spec now includes complete role, goal, non-goal, scenario, acceptance, constraint, assumption, dependency, NFR, and success-criteria coverage.

### AR-002 - "Any version" conflicted with hidden, displaced, and purged history states

- **Severity**: High
- **Evidence**: Parent and feature 006/010 semantics distinguish hidden presentation, displaced commits retained for 90 days, and permanent purge after retention. The initial export language did not say whether these states were exportable.
- **Impact**: The implementation could either expose hidden versions accidentally or promise export of data that must become unrecoverable after purge.
- **Disposition**: Valid and fixed. FR-007 through FR-009 and AC-006 through AC-007 now define explicit behavior: hidden versions require advanced/show-hidden selection, retained displaced versions may export while required bytes are held, and permanently purged bytes make items permanently unavailable.

### AR-003 - Basket selections could drift if branch heads or working files changed

- **Severity**: High
- **Evidence**: Git-like refs are movable, and uncommitted working files are mutable. An export basket that stores only branch names, paths, or "current" labels could export a later state than the owner selected.
- **Impact**: The feature's core promise of exact historical export would be false under normal branch movement.
- **Disposition**: Valid and fixed. FR-003 through FR-005 and FR-012 require exact asset/XMP identity pinning, branch/tag resolution at selection time, and durable export-only snapshots or committed-version selection for working states.

### AR-004 - Manifest and package privacy boundaries were under-specified

- **Severity**: Medium
- **Evidence**: Export manifests need enough provenance to verify outputs, but filenames, paths, geolocation, and XMP content can be private library data.
- **Impact**: Operators or logs could expose sensitive media metadata, or manifests could be too vague to prove output provenance.
- **Disposition**: Valid and fixed. FR-017, FR-022, NFR-006, and the risks now require complete provenance while excluding sensitive payloads from audit/telemetry and forcing a privacy-preserving manifest profile during planning.

### AR-005 - Cost and operations controls were too weak for batch rendering

- **Severity**: Medium
- **Evidence**: Export can trigger many darktable renders and large downloadable packages, but the initial draft only mentioned bounded retention.
- **Impact**: A single owner could accidentally create long-running or expensive work with unclear cancellation and operator controls.
- **Disposition**: Valid and fixed. FR-019, FR-020, FR-024, NFR-014, and NFR-015 now require cancellation, expiry, operator pause/drain, runbooks, and configurable item/package/render/cost limits.

## Final Verdict

PASS. The revised specification is complete enough for product-owner review and subsequent Spec Kit planning. Remaining decisions are planning-level choices: exact preset catalog, package format, retention cleanup mechanics, and data schema.

## Follow-up Review Fixes

The subsequent independent review identified three additional concerns. All are valid and fixed in the current specification:

- **Basket retention semantics (Medium)**: FR-025 and AC-013 state that basket pins are identity references rather than lifecycle-retention holds, explain availability through pre-existing holds, and define hidden, displaced, expired, and permanently purged outcomes.
- **Geolocation and metadata privacy (Medium)**: FR-026 and AC-014 establish a safe default that strips GPS coordinates, derived location labels, and contact metadata, requiring explicit owner confirmation to retain them.
- **Package integrity (Low)**: FR-017 and AC-015 add per-output byte lengths/checksums and a separately verifiable package checksum for interrupted-download and extracted-file verification.

Final independent reassessment is pending.
