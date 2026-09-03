# Implementation Plan: Lineage and Merge Interface

**Branch**: `feature/010-lineage-merge-interface` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)
**Status**: Proposed — pending approval

## Summary

Build an accessible React lineage/comparison/merge interface over feature 006 graph/ref APIs and feature 008 provenance-aware previews. The interface prepares immutable-state-bound proposals and submits only final, confirmed resolver output; it cannot invent a commit, rewrite protected history, or use a preview as graph truth.

## Design

Use paged image projections with graph and equivalent searchable table/tree views. Selected version state includes exact commit/XMP IDs, parent/branch/tag labels, hidden/recovery status, and read-model version. Comparison uses explicit two inputs/common ancestor, semantic XMP diff policy version, and two independent preview/provenance states.

Merge preparation binds source, target, common ancestor, expected target head, affected images, and policy version. Auto-merge only has disjoint recognized metadata changes; simultaneous scalar fields, unknown modules, masks, and history are conflicts. The owner selects every conflict value, then reviews the complete resolved XMP identities, affected images, preview availability/provenance, and final confirmation. Submission delegates feature-006 resolver-supplied merge publication with expected-head CAS; any stale head invalidates the proposal.

Branch, tag, commit, restore/revert, hide/reveal, recovery, and web-branch checkout are thin contracts over feature 006. Protected-default rewrite is disabled; non-default rewrite requires impact/reachability preview and confirmation. Mutating controls can be disabled operationally without disabling read-only lineage. All actions are owner-authorized, audited, redacted, localizable, keyboard operable, and provide text alternatives. **PASS**: current branch diverges from `master`; no external mutation is authorized.
