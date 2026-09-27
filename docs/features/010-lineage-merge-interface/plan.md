# Implementation Plan: Lineage and Merge Interface

**Branch**: `feature/010-lineage-merge-interface` | **Date**: 2026-09-26 | **Spec**: [spec.md](spec.md)
**Status**: Design complete; owner authorized proceeding from the approved specification on 2026-09-26.

## Summary

Add a private image-history workspace beside the gallery. It reads feature 006's immutable library-wide graph and feature 008's exact-version previews, displays a paged graph and equivalent searchable table, and exposes the existing ref, commit, checkout, recovery, and visibility operations. A feature 010 server boundary prepares semantic XMP comparisons and merge proposals, then validates both branch heads and the complete chosen result before delegating atomic publication to feature 006.

## Technical Context

| Area | Decision |
|---|---|
| Runtime | .NET 10 API; React 19 and TypeScript 6 web app; no new dependency |
| Persistence | Existing PostgreSQL version-control tables and content-addressed XMP objects; additive merge-content provenance and derived branch reachability tables |
| Interfaces | Owner-authorized feature 010 read/prepare/confirm routes under `/v1/libraries/{library}/lineage`; existing feature 006 mutation routes remain authoritative |
| Tests | .NET unit and PostgreSQL/MinIO integration checks; web typecheck, source/accessibility checks, and Playwright Chrome/Edge/Firefox matrix where available |
| Reference scale | 100,000 assets, 1,000,000 committed revisions, first lineage page capped at 100 commits; 500-version view p95 ≤2 s and comparison p95 ≤3 s excluding preview work |
| Security | Existing owner authorization and operation IDs; no XMP, path, location, or commit text in logs or errors; rewrite/recovery follows parent re-authentication policy |

## Constitution Check

| Gate | Result |
|---|---|
| Branch and artifact directory | PASS: matching branch and `docs/features/010-lineage-merge-interface/`; clean before planning; `origin/master...HEAD` was 0 behind / 2 ahead |
| Approved source | PASS: `spec.md` approved by project owner on 2026-09-26; `tasks.md` becomes the sole numbered work source after generation |
| Testable quality | PASS: contract and fixtures cover every FR, AC, and NFR; validation is mapped in `tasks.md` |
| Supply chain | PASS: reuse installed .NET and web dependencies; no package, lockfile, or third-party action change planned |
| History safety | PASS: feature 006 retains atomic commits, ref protection, recovery, and audit authority |

## Research and design artifacts

- [Research](research.md) records the merge, projection, and dirty-state choices and alternatives.
- [Data model](data-model.md) defines transient proposals and the one durable generated-content provenance record.
- [Contract](contracts/lineage-merge-v1.md) defines read/prepare/confirm behavior and delegates.
- [Quickstart](quickstart.md) lists runnable validation paths.
- [ADR-0012](../../architecture/decisions/0012-generated-merge-xmp-provenance.md) records the new generated-content authority boundary.

## Design and affected components

1. `apps/web/src/Gallery.tsx` adds a history entry while preserving its URL-backed asset, branch, filter, sort, and page state. `apps/web/src/Lineage.tsx` renders the selected image's lineage, table, comparison, merge review, and history controls. The selected image stays a projection of library-wide commits; hidden or unrelated ancestors are labelled rather than silently represented as direct parents.
2. `apps/api/Features/Lineage/` reads feature 006 graph/ref/working data and authoritative XMP bytes. Comparison binds exact commit and XMP identities. Each preview request binds the selected commit and edit identity; pending, failed, incompatible, and missing previews remain explicit.
3. Semantic merge policy v1 recognizes only the seven existing gallery metadata fields: rating, rejected, color, tags, title, description, and location. It compares base/target/source values and automatically combines proven disjoint recognized changes. Any concurrent scalar change, unknown or processing/mask/history change, malformed XMP, path/lifecycle overlap, or missing base is a conflict. The owner chooses each recognized field and one exact opaque XMP side when needed; the server applies chosen recognized values to that verified side. Unknown content is never silently overwritten. The policy is versioned and fixture tested.
4. A merge proposal identifies source/target refs and heads, base, all changed asset/edit identities, proposed XMP identities, conflicts, and preview/provenance states. Its review token is a digest of the canonical proposal; it is transient and immutable. Confirmation requires an idempotency key, the reviewed token, complete resolutions, and a final result review. The server publishes generated XMP as verified content, records input and policy provenance, then passes prepared resolver entries to feature 006's two-parent merge publication. Feature 006 validates the source ref and target ref under its publication transaction; a changed head or working state invalidates the proposal without publishing a commit.
5. Branch/tag/commit/rewrite/recovery/restore/visibility/checkout controls call feature 006 contracts. Commit preparation lists staged and gallery web working changes separately. Checkout uses the browser client identity and feature 006 dirty action contract; gallery's `__gallery_web__` working namespace needs explicit protection so preserve/discard cannot silently strand or erase it. Rewrite and recovery require a valid signed owner token with an `auth_time` no older than five minutes; absent or old authentication blocks the action and the web prompts a fresh owner sign-in.
6. The initial image-history page intersects the existing indexed asset-to-commit entries with a derived branch-reachability index, then fetches at most 101 canonical manifests to determine whether a next page exists. It returns exact image-changing commits and parent IDs; omitted library-wide connector commits remain identifiable by those IDs and the separate branch-head label. An additive migration backfills existing branches. A database ref trigger maintains one reachable-commit row per branch and commit in the same transaction as ordinary publication, merge, branch creation, rename, deletion, and rewrite. Immutable commits and refs remain authoritative. Ordinary first-parent commits add one row; branch creation, rewrite, and merge import or rebuild reachable IDs. This avoids the much larger per-branch copy of every image entry while bounding the history read to one image's indexed revisions. The index can be rebuilt from immutable history after a fault.
7. Normal image history uses the indexed asset-entry and branch-reachability intersection to report whether hidden image-changing commits exist on the selected branch. A page-level collapsed-history notice stays accurate across any number of omitted library-wide connectors and merge paths without traversing those paths. Canonical parent IDs remain on each node; direct hidden parents retain their node marker. Show-hidden returns the exact hidden image commits and canonical parents. The sparse reference-scale fixture covers long connector gaps, a hidden relevant image commit, and an unrelated hidden library-wide commit.

## Validation and error behavior

- Error bodies use safe codes and operation IDs. `stale_head`, `stale_working_state`, `unresolved_divergence`, `protected_ref`, `dirty_checkout`, `preview_unavailable`, and `recovery_expired` show specific refresh or recovery actions. No failed proposal is automatically retried.
- Unit fixtures cover graph collapse and semantic XMP policy. Integration fixtures prove exact proposed XMP bytes/identities, source and target movement, duplicate confirmation, no partial publication, dirty gallery state, and audit. Browser checks cover keyboard, screen-reader semantics, context preservation, and degraded previews.
- Load checks use a 500-version image projection and comparison fixture plus the persisted one-million-entry/100,000-asset reference fixture for the indexed first history page. Three locale/time-zone settings and a Chrome/Edge/Firefox matrix cover presentation compatibility.

## Migration, rollout, rollback, and operations

- Add the provenance and derived-lineage tables and extend feature 006's content-authority validation to accept only verified server-generated merge content for its exact asset/edit. Migration 020 builds the lineage index from existing refs before the new API serves history. Apply the additive migrations and API before the web entry; older clients continue using gallery and feature 006. The web entry is enabled only when the lineage API is available.
- Disable mutating lineage actions during an incident while keeping history reads available. Roll back the web and additive API without changing stored graph, refs, XMP objects, working state, or audit evidence. Inspect operation IDs, stale/merge failure rates, latency, and graph size; alert on sustained failures within 15 minutes.
- Existing 90-day displaced-commit retention and presentation-only hidden state remain under feature 006. This feature does not trigger archival purge. Rollback keeps the provenance and derived-lineage tables and generated objects for commits already written; a later cleanup migration may remove them only after graph/recovery proof. On projection drift, pause lineage reads, rebuild from immutable commits and current branch refs, and resume after count/identity checks.

## Alternatives and risks

| Alternative | Decision |
|---|---|
| Browser-only merge calculation | Rejected: untrusted clients cannot enforce source/target freshness or authoritative content publication. |
| Merge raw XML text or all darktable nodes automatically | Rejected: syntactically valid output can change the visual edit unexpectedly. |
| Copy one side's XMP for every divergence | Rejected: silently loses disjoint edits. |
| Store long-lived proposals | Rejected: duplicate state and stale proposal cleanup add no value; confirm recomputes from exact heads. |

The principal risk is semantic mismatch between parsed metadata and arbitrary darktable XMP. The policy fails closed and requires an exact owner-chosen resolution. A second risk is gallery working changes held under a separate client namespace; checkout protection is explicitly tested across both namespaces before that action is exposed.

## Project Structure

```text
apps/api/Features/Lineage/             # exact-state reads, semantic policy, proposal/confirm boundary
apps/api/Database/Migrations/          # additive generated-XMP provenance table
apps/web/src/Lineage.tsx                # owner workspace and accessible history controls
apps/web/src/Gallery.tsx                # context-preserving history entry
packages/contracts/lineage/            # shared API records if server contracts warrant them
tests/lineage/                         # unit and integration fixtures
apps/web/tests/browser/                 # browser journeys
docs/features/010-lineage-merge-interface/
docs/runbooks/                          # lineage incident and rollback guidance
```

## Milestones

1. **Planning**: design, contract, numbered tasks, and analysis; local documentation commit.
2. **History and comparison**: exact projection, gallery entry, table/graph, comparison/provenance, tests, and documentation; local commit.
3. **Safe merge**: semantic policy, proposal, resolution, confirmation, stale-state and audit tests; local commit.
4. **History actions and readiness**: ref/commit/checkout/recovery controls, accessibility and browser checks, load/operations validation, convergence, and documentation; local commit.

No push, merge, deployment, or external publication is implied by these milestones.
