# Implementation Plan: Gallery Metadata Search

**Branch**: `feature/009-gallery-metadata-search` | **Date**: 2026-09-25 | **Spec**: [spec.md](spec.md)
**Status**: Approved by project owner on 2026-09-25

## Technical context and constitution check

The new branch was rebased from the stale local `master` onto `origin/master` after the owner identified its squash merge of 008. The current `origin/master` contains features 005–008, including the working-state, Windows-sync, and rendering services required here. The stale 009 branch is preserved at `archive/009-gallery-metadata-search-stale`. The API is .NET 10 with Npgsql and owner JWT authorization; the web app is React/TypeScript. Source originals and sidecars live in private S3 storage. No gallery metadata index exists yet.

The [constitution](../../../.specify/memory/constitution.md) requires approved artifacts, traceable numbered tasks, matching branch, coherent milestone commits, applicable tests and documentation, dependency review, and a separate model's pre-push review. This plan and its tasks must be approved before implementation. No merge, push, deployment, or history rewrite follows from implementation approval.

## Boundaries and data flow

1. Feature 005 remains authoritative for asset identity, path, lifecycle, original checksum, and import date. Feature 006 remains authoritative for committed and working XMP. Feature 008 remains authoritative for rendition status and provenance. The gallery stores only a disposable searchable projection and the owner's presentation preference. Because 005 has owner-scoped assets without a library key, the first release admits one configured canonical library ID per owner and rejects other gallery library IDs; it does not imply unsupported multi-library isolation.
2. A refresh job reads an active asset and its immutable original/XMP objects, parses embedded and XMP metadata under size and time limits, applies documented field precedence, and writes a complete projection in one database transaction. The projection key includes owner, library, asset, selected branch, source checksums, schema version, and generation. A failed parse may leave the previous snapshot visible only with a `stale` source label and error/status; it never presents it as current or publishes a partial row.
3. A branch-scoped web edit uses a reserved web client identity in feature 006, with an expected working version and base XMP checksum. It patches only allowlisted XMP fields, publishes the new immutable XMP object, then records an uncommitted working change. The shared web overlay is resolved by gallery reads, feature-007 namespace reads for devices checked out on that branch, and owner-authorized staging/commit so the change is eventually committable. Device-local changes remain separate. When both target the same sidecar, synchronization reports a conflict and retains both; no silent overwrite or automatic merge.
4. The gallery UI calls owner-authorized `/v1/libraries/{library}/gallery` endpoints. It reuses the existing feature-005 lifecycle endpoints for import, download, delete, and restore and their audit records. Cards load metadata and rendition references only. Opening a full preview or original is an explicit action.

## Query and indexing design

- Store normalized typed columns for range and sort facets plus indexed text and tag values. Preserve each value's source (`xmp`, `embedded`, `derived`), state (`present`, `missing`, `cleared`, `invalid`), source checksum, and timestamp provenance. Tags and colors are distinct rows; a multi-value filter means any selected value within one facet, with AND across different facets.
- XMP wins for organizational/descriptive fields when present, including an explicit clear. Embedded data supplies capture/exposure/camera/lens/dimensions unless an accepted XMP field explicitly overrides it. Unparseable values are marked invalid, not guessed. Capture time retains its original timezone/offset when known; missing offset is explicitly labelled. Import date is the earliest completed original upload's `verified_at` for that asset, labelled as the verified import time.
- Use a bounded, allowlisted query builder with parameterized SQL. Each sort has an indexed `(owner, library, branch, sort value, asset id)` path. Nulls sort last in both directions, and asset ID breaks ties. A server-validated cursor binds schema version, filter hash, sort, direction, generation, and last sort tuple. A changed filter or generation returns `cursor_expired`; the UI restarts from page one. Count and page read one consistent database snapshot.
- Geolocation suppression is stored per owner and applied server side before search, facet counts, card payloads, and normal details. With suppression enabled, location filters return a validation error and location values are absent from responses. A separate explicit owner action may reveal retained location on detail; it never changes XMP.
- A refresh queue/checkpoint records total, completed, failures, last processed asset, and schema version. It can resume after interruption and can be rebuilt from source objects. Migration is expand, backfill, verify, switch read generation, then contract. Rollback reselects the prior complete generation while retaining source objects and working changes.

## API, UI, errors, and operations

See [gallery-v1.md](contracts/gallery-v1.md) for request/response and error shapes and [data-model.md](data-model.md) for fields and lifecycle. The gallery uses URL query state for filters, sort, and page cursor so opening/returning from detail preserves context. Every control has a visible label, keyboard operation, validation message, and text status for missing/pending/failed previews. Dates and exposure values are formatted in the UI from invariant API values. Unsupported darktable operations have no edit controls.

Authorization uses the existing owner guard before any query, edit, location reveal, or lifecycle handoff. Audit events record edit accepted/rejected, access failure, and existing lifecycle actions with operation ID and resource ID; logs and metrics exclude tag, title, description, filename, and coordinates. Metrics measure page latency, search failures, preview states, edit conflicts, facet use, and refresh progress. Owner-only data remains private and uncached by shared intermediaries.

## Validation and rollout

Unit tests cover normalization, precedence, missing versus cleared, XMP patch preservation, cursor binding, and conflict classification. PostgreSQL integration tests cover every facet and sort, combined filters, stable pages, count, owner isolation, atomic refresh, lifecycle delegation, shared branch overlay, and recovery. Current Chrome, Edge, and Firefox checks cover keyboard use, text alternatives, URL context, hidden location, and error states. A supported darktable round trip checks the edited sidecar and unrelated XMP content. A 100,000-asset fixture measures 95th-percentile initial page under two seconds and cached thumbnails under 500 ms in the documented reference environment. Local CI checks include .NET tests/build, web typecheck/build, migration validation, and dependency audit. Production rollout and rollback require the release-operations skill and separate authorization.

## Alternatives and risks

- PostgreSQL projection is chosen over a search cluster because the reference scale is 100,000 assets and all required filters can be indexed in the existing database. Revisit only if the reference load test fails after index and query tuning.
- Keyset paging is chosen over offset paging for deterministic adjacent pages. A generation-bound cursor expires on relevant refresh or lifecycle change, avoiding misleading continuation after mutation.
- A shared web client plus branch overlay is chosen over copying a web change to each device. Copying creates divergent working versions and partial delivery. This choice changes the 006/007 read boundary and needs explicit integration tests; see [ADR-0003](../../architecture/decisions/0003-branch-scoped-web-working-overlay.md).
- Embedded metadata extraction needs a format-aware parser for the 005 compatibility corpus. Select and pin a maintained parser only after the dependency assessment in T002. Do not claim unsupported RAW coverage. Malformed inputs remain indexed with a visible parse state and owner-safe diagnostics.
- The largest risks are XMP round-trip semantics, performance of broad combined filters, and conflict presentation. T003, T007, and T012 include corpus and reference-scale gates before marking delivery complete.

## CI contract

Extend the existing `.github/workflows/ci.yml` backend and web jobs on pull requests and `master` pushes; preserve stable `ci / backend`, `ci / web`, and `ci / required` names. The backend job runs new gallery unit and PostgreSQL/S3 integration checks, including the connected 100,000-asset browser reference, using the isolated test database; the web job runs typecheck, source/accessibility checks, build, and current Chrome/Edge/Firefox Playwright interaction checks. Both use repository-locked .NET 10 and Node 24 toolchains. The manually dispatched trusted Windows job covers Cloud Files and darktable integration where hosted runners cannot; its absence from a local run must be reported. The required gate remains `ci / required`; failures belong to the feature author, with retries only for demonstrated transient infrastructure errors. No new secrets, permissions, or long-lived artifacts are planned. The web timeout is 15 minutes and backend timeout is 20 minutes to accommodate browser installation and sequential reference timing.

## Milestones

M1: Approved design, source/facet projection, migration, and refresh with tests.
M2: Search API, detail API, accessible gallery, privacy, and lifecycle entry points with tests.
M3: Web XMP edits, shared-branch Windows sync, darktable round trip, performance, operations documentation, and convergence.
M4: Independent adversarial review and remediation record. Each milestone gets a coherent commit after its validation.
