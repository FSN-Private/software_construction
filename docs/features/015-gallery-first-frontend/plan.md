# Implementation Plan: Gallery-First Frontend

**Branch**: `feature/015-gallery-first-frontend` | **Date**: 2026-09-29 | **Spec**: [spec.md](spec.md)

**Status**: Execution authorized by approved-spec implementation request; routine choices recorded below.

## Summary

Compose/restyle the existing React client to match the saved gallery, modal left filters and image inspector. Reuse native dialogs, existing services and history/lifecycle components. No new dependency, service, persistent entity or API contract.

## Technical Context

TypeScript 6.0.3, React 19.2.8, Vite 8.2.2 and existing pinned Playwright 1.63.0. CSS vocabulary in gallery.css/workspace.css. Existing owner token/session boundary. Browser history/URL holds applied browse state; drafts/density ephemeral. Existing PostgreSQL/search/render services unchanged. Reference targets: 100,000-asset gallery p95 2 s, cached thumbnails p95 500 ms; 500-version history p95 2 s; drawer p95 200 ms. Existing configured Chromium/Edge/Firefox baseline; record exact executed versions.

## Constitution Check

Pre-design: clean worktree, matching branch, master divergence 0 behind/1 ahead, approved spec. Post-design: no architecture/contract/dependency change; tasks map every requirement; local milestones only. External capacity, rollout, supported screen-reader and owner visual acceptance need actual evidence or remain explicitly incomplete. No extension hooks exist. Local research rather than delegated agents follows repository restriction on delegation.

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
