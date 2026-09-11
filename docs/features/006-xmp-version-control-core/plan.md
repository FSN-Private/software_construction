# Implementation Plan: XMP Version-Control Core

**Branch**: `feature/006-xmp-version-control-core` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)
**Status**: Approved — 2026-09-10

## Summary

Implement a custom immutable, library-wide DAG for byte-exact XMP revisions. PostgreSQL stores canonical commits, entries, refs, working/staging state, reflogs, visibility, and rebuildable projections; feature-005 content IDs remain authoritative. Serializable expected-head compare-and-swap, idempotency, and lifecycle holds prevent loss during concurrency or history cleanup. No Git protocol, automatic merge, or UI is included.

The format/invariants are in proposed [ADR-0006](../../architecture/decisions/0006-content-addressed-xmp-commit-dag.md).

## Technical Context

| Area | Decision |
|---|---|
| Runtime/storage | .NET 10 domain module; PostgreSQL 18; immutable XMP/manifest bytes from feature 005 |
| Commit ID | SHA-256 of a versioned canonical manifest: library ID, ordered parents/entries, author/times/message, policy version |
| Transaction | serializable expected-head CAS; commit, ref, reflog, idempotency record, and hold changes publish together |
| Working state | mutable owner/client/branch-scoped rows reference immutable base/current XMP IDs |
| Read model | materialized branch state/image projections derive from manifests and are rebuildable/integrity checked |
| Retention | immutable commits never change; ref/reflog/hidden metadata manages storage holds; rewrite recovery ≥90 days |
| Targets | 100-image commit ≤2s p95 after content exists, measured through pinned object-storage validation; per-asset head ≤200ms; 10k-node projection page ≤2s; persisted capacity includes 1M working entries and 100k assets |

## Constitution Check

- **PASS**: core contracts only; sync, semantic merge, rendering, and UI remain later features.
- **PASS**: current branch is correct; the owner authorized merging the freshly fetched `origin/master` on 2026-09-10 so feature-004/005 dependencies are present without rewriting history.
- **PASS**: immutable manifests, CAS, idempotency, audit/redaction, and lifecycle holds protect history.
- **PASS**: feature-004/005 contracts are present. Production migration/deployment remains outside this implementation request.

## Design

```text
working change → staged → validated → immutable commit + entries
                                        ↓ (same serializable transaction)
expected branch head ───── CAS → new head + reflog + lifecycle holds
                                        ↓
                        materialized state / image projection (rebuildable)
```

A staged entry contains asset/edit identity, base/current XMP IDs, and optional path/lifecycle change. Validation proves each object is authoritative and in scope. Canonical manifest ordering and binary-safe encoding make its digest the commit identity. A repeated idempotency key returns its original outcome. Exactly one head transaction wins; stale callers receive base/ours/theirs and retain working changes.

Branches are library-wide normalized refs. Image projections include changed-image commits and parent/merge connectors, not a separate graph. Integrity traversal checks parent presence, library scope, acyclicity, object IDs, and manifest digest before ref advancement; broken refs quarantine and alert.

App-created commits are canonical-verified at insertion and immutable thereafter. Each commit advance revalidates the current head manifest and quarantine state; ref creation and rewrite perform the full graph verification. A scheduled full canonical/normalized/provenance/storage-version traversal runs at startup and every 10 minutes when object storage is configured, and derived projections read canonical manifests rather than normalized rows.

The default branch rejects all rewrite/delete/force operations. A confirmed non-default rewrite previews impact, moves only its ref, appends an immutable reflog, and holds newly unreachable commits for 90 days. Recovery creates a named ref to the same digest. Hide/unhide is presentation metadata only. Tags are immutable exact refs. Checkout must block, preserve/stash, or owner-confirm discard dirty work. Restore/revert creates a new commit. A later resolver may submit a fully resolved merge; the core atomically records two parents and CAS advances the target without selecting content.

[version-control-v1.md](contracts/version-control-v1.md) defines working/staging, refs, compare/divergence, recovery, visibility, checkout, and integrity for features 007/010. Errors are machine-readable and plain-language but exclude XMP/path payload. Migrations use expand/migrate/contract and a rebuild rehearsal; runbooks cover stale refs, divergence, corruption, projection rebuild, reflog recovery/expiry, and protected refs.

## Alternatives and Post-design Check

Git/LFS, mutable commit rows, last-writer-wins, line-based XMP merging, and non-rebuildable projections are rejected as unsafe or incompatible. Exact encoding/index choices are constrained by stable digest fixtures and latency tests.

**PASS.** No product ambiguity remains; data actions require explicit approval.

## Adversarial-review remediation

The completed implementation review found gaps in canonical-evidence enforcement, content provenance, retention overlap, concurrent rebuilds, mutation idempotency, restore/revert semantics, checkout coordination, public recovery contracts, merge completeness, CI coverage, and integrity alerting. Tasks T024–T032 are the approved in-scope remediation plan; they preserve the existing product contract and add no new dependency.

The stable Round 2 implementation review found remaining replay-order, slash-route, checkout/rename fencing, production-representative capacity, verifier resilience, and quarantine-publication races. Tasks T033–T037 close those gaps without changing the approved public product behavior or adding a dependency.

CI remains triggered for every pull request and pushes to `master`, with stable required job name `ci / backend` and aggregate `ci / required`. The backend job must run unit/contract tests plus Feature 006 PostgreSQL integration and persisted capacity checks on Ubuntu/.NET 10/PostgreSQL 18. Failures are required, owned by the feature implementer, and retried only by rerunning the deterministic job; no test artifact containing XMP payloads is retained.
