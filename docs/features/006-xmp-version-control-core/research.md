# Research: XMP Version-Control Core

**Feature**: [spec.md](spec.md) | **Date**: 2026-09-03 | **Status**: Complete for planning

1. **Custom content-addressed DAG** — immutable XMP content and canonical manifests avoid Git repository/LFS coupling while retaining Git-like user semantics.
2. **Serializable CAS ref movement** — branch head is an expected-version compare-and-swap in the same transaction as commit, reflog, idempotency, and hold changes; stale writers preserve divergence.
3. **Derived projections** — image lineage and materialized branch state are read models rebuilt from immutable graph evidence; they never become authority.
4. **Ref-only rewriting** — commits never mutate; non-default rewrite displaces only refs and adds recoverable reflog holds. Hidden state is presentation-only.
5. **No automatic merge** — core exposes exact base/ours/theirs and can atomically publish a resolver-supplied result, but never invents XMP content.

The umbrella evidence is in [program research](../003-darktable-version-control/research.md#decision-4-use-a-content-addressed-custom-commit-dag-not-git-repositories).
