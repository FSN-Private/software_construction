# Implementation Plan: Gallery-First Frontend

**Branch**: `feature/015-gallery-first-frontend` | **Date**: 2026-09-29 | **Spec**: [spec.md](spec.md)

**Status**: Execution authorized by approved-spec implementation request; routine choices recorded below.

## Summary

Compose/restyle the existing React client to match the saved gallery, modal left filters and image inspector. Reuse native dialogs and history/lifecycle components. The owner revision adds one bounded read-only gallery filter-values API; no new dependency, service or persistent entity.

## Technical Context

TypeScript 6.0.3, React 19.2.8, Vite 8.2.2 and existing pinned Playwright 1.63.0. CSS vocabulary in gallery.css/workspace.css. Existing owner token/session boundary. Browser history/URL holds applied browse state; drafts/density ephemeral. Existing PostgreSQL/search/render services unchanged. Reference targets: 100,000-asset gallery p95 2 s, cached thumbnails p95 500 ms; 500-version history p95 2 s; drawer p95 200 ms. Existing configured Chromium/Edge/Firefox baseline; record exact executed versions.

## Constitution Check

Initial design: clean worktree, matching branch, master divergence 0 behind/1 ahead, approved spec. The 2026-09-30 owner revision adds one specified read-only API contract without schema, dependency or infrastructure changes; tasks map every new requirement and local milestones remain separate from deployment. External capacity, rollout, supported screen-reader and owner visual acceptance need actual evidence or remain explicitly incomplete. No extension hooks exist. Local research rather than delegated agents follows repository restriction on delegation.

## Project Structure and Approach

- App.tsx: compact owner workspace, Gallery/History, guarded branch dialog, secondary import/lifecycle/diagnostics and sign-out. Retain mounted gallery across secondary views for drafts/context; verify session before private UI.
- Gallery.tsx/gallery.css: preserve authoritative edit/download logic, add toolbar/scoped search, draft/apply native drawer, advanced facets, validation/chips/density, contained detail, semantic metadata, safe URL navigation and cancelled obsolete reads.
- ImageHistory.tsx: real compact history and exact selected version; export/reuse VersionPreview from Comparison.tsx, which preserves explicit render/provenance checks. Full Lineage remains authoritative for graph/mutations.
- LibraryHistory.tsx: existing library-wide commits/refs, image selection into existing lineage.
- tests/browser/gallery.spec.ts and frontend.spec.ts: existing regressions plus deterministic new interaction/layout/privacy/history tests; screenshots in ignored artifacts and concise checked-in evidence.

## Interfaces and Client State

See [contracts/frontend.md](contracts/frontend.md), [data-model.md](data-model.md) and [research.md](research.md). Search selects Filename/path, Title or Description; no unsupported OR endpoint. Rating is exact because existing service supports discrete ratings, not a threshold. Show photos avoids unsupported draft counts. Existing compare exposes identities/provenance, not complete historical metadata: explicitly label historical metadata unavailable while showing separate current/capture values. Requests do not automatically create historical/full renders.

Use native modal focus/inert behavior. Filter Close/Escape/backdrop discard draft; Reset clears draft. Cancel obsolete reads and gate late operation results by navigation identity. Unsaved edit navigation requires confirmation; unload warns; cancelled browser navigation retains draft/current URL. Expired sessions hide private data, keep bounded in-memory drafts for reauthentication and never automatically repeat mutations.

## Validation, Errors and Non-Functional Quality

Existing format/typecheck/source/rendered-accessibility/build plus browser regression suite. New deterministic cases cover all primary states, drawer/focus/validation, context, version identity, failures, privacy/sign-out, responsive layout, text expansion and network limits. Relevant backend regressions remain unchanged and run where available. Source checks alone do not prove performance or accessibility. Every FR/AC/NFR/SC maps to tasks and evidence; external validation remains explicitly incomplete if access unavailable.

## Migration, Rollout, Rollback and Operations

No migration or infrastructure workflow changes. Preserve old owner query links. Deploy only through existing authorized release procedure; rollback previous immutable web artifact without modifying source/XMP/history. Secondary diagnostics remain available. Update web guide for drawer/history/rollback/failure flows.

## Alternatives and Risks

New framework/UI library rejected: existing React/native dialogs suffice. Rewriting lineage rejected: reuse existing exact-state/safety behavior. Pixel-copy of sample content rejected: real data and accessible contrast prevail. Long labels/history scroll/reflow. Native dialogs require focus/browser testing. No consequential durable architectural decision introduced.

## Milestones

M0: approved spec, plan/research/contracts/tasks and consistency review committed.
M1: workspace/gallery/filters, docs and relevant checks committed.
M2: inspector/history and regressions committed.
M3: cross-cutting evidence/convergence/remediation committed. Required external evidence must remain incomplete if unavailable; no push authorized.

## 2026-09-30 owner-requested revision

The owner-approved interaction changes require one read-only API addition. `GET /v1/libraries/{library}/gallery/filter-values?branch=&facet=&q=` returns at most 100 distinct active, present values for one allowlisted facet, with a `hasMore` flag. It uses the same owner guard, canonical library, active branch generation, active/restored assets and geolocation preference as browse, and `private, no-store` response behavior. `q` searches normalized value text; latitude/longitude choices are available only after the owner enables location in gallery privacy. Numeric results are invariant strings usable in the current min/max query; text results use stored display text where available. This avoids deriving values from a single page or downloading the entire index. No new table, dependency or migration.

In the client, native `<details>` groups keep the drawer compact. Categorical selects and optional numeric value picks use the new endpoint; existing free numeric bounds remain. Capture and import dates use a two-column row at desktop drawer width. Failed value lookup leaves a labelled retry state and free entry for numerical criteria. The drawer fetches values only on open and when a group's search changes, with cancellation on close/branch change. The current filter URL and draft/apply behavior remain authoritative.

The gallery stays mounted under a native `<dialog>` detail overlay. Existing detail content, exact-version checks, metadata edit guards, and URL history remain. ImageHistory's selection stays in the overlay; its comparison action adds a second commit picker and uses the existing compare result/provenance component within the overlay. The advanced full lineage view stays available by a distinct action. The overlay scrolls independently and focus returns to its tile. Browser tests cover direct links, Back, dirty edits, selection and privacy.

The import screen gains a directory input using `webkitdirectory`; browser-provided `webkitRelativePath` is the import path. Duplicate exact paths are rejected before hashing or batch creation; identical basenames in different subfolders are accepted. Real batch/upload contracts remain untouched. The local Playwright preview fixture identifies itself and disables import rather than reporting a backend-shaped `fixture_unavailable` error. Preview uses a long-lived headed browser without the Playwright Inspector panel occupying viewport width. A 1440px and zoom geometry check distinguishes a true layout defect from Inspector docking.

Revision milestones: M4 specs/contract and tests; M5 filter API/UI plus responsive layout; M6 overlay/comparison and folder import/preview guidance; M7 validation/convergence/documentation. Each is committed locally on the matching branch. Existing T009/T011 external evidence remains open. Rollback uses the previous web/API artifacts; no persistent data changes.

## 2026-09-30 owner-reported regression correction

M8 fixes three behaviors within the approved gallery contract. The shared Modal toggles one document scroll-lock class while native dialogs are open; dialogs remain independently scrollable and closing them restores the previous gallery position. The interactive Playwright preview uses the browser window's viewport rather than a fixed test viewport, so maximizing it grows the gallery. The preview's synthetic 100,000-image fixture returns cursors and distinct 40-card pages, keeping paging controls truthful; regular small fixtures retain their existing behavior. Verify with browser scroll, resize, paging and responsive checks. No API, schema, dependency or deployment change; rollback uses the previous web artifact.

## 2026-10-01 PR CI remediation

M9 addresses actionable checks on PR #30. Backend CI requires formatter-compliant layout of the new gallery facet switch; this is a whitespace-only C# change. Firefox CI found a gallery scroll jump after closing image detail; restore focus with `preventScroll` and verify the existing browser regression. Infrastructure and security CI reject transitive `brace-expansion` 5.0.9 bundled inside pinned `aws-cdk-lib`. The latest published CDK 2.272.0 inspected on 2026-10-01 still bundles 5.0.9; a consumer lockfile edit or override would not change the published package contents. T021 tracks an upstream patched package and subsequent audit/CI verification. No vulnerability suppression or unverified lockfile rewrite is planned. T009/T010/T011 acceptance gates remain separate and open until evidenced or explicitly waived.

The second PR run still failed the Firefox scroll assertion immediately after focus returned. Scroll restoration is scheduled in an animation frame, so the browser regression now waits for the existing 5px tolerance rather than sampling before that frame. The next Linux run decides whether the application restoration is truly reliable; a persistent failure requires an implementation fix under T020.
