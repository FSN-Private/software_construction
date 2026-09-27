# Feature 010 validation

**Branch:** `feature/010-lineage-merge-interface`
**Date:** 2026-09-27
**Scope:** Local implementation and validation. No push, merge, deployment, or data cleanup was performed.

**Implementation milestones:** `be926b1` (lineage API and safe merge), `2c7eb22` (web workspace and operations). This record closes the readiness milestone.

## Automated evidence

| Check | Result | Coverage |
|---|---|---|
| `dotnet build apps/api/Platform.Api.csproj --no-restore -v:q` | Pass, zero warnings/errors | API and lineage contracts compile. |
| `LINEAGE_INTEGRATION=1 dotnet run --project tests/lineage/Platform.Lineage.Tests.csproj --no-restore` | Pass on disposable PostgreSQL schema and versioned local S3 bucket | Projection, hidden ancestry, owner routes, seven recognized XMP fields, unsafe opaque/malformed changes, exact bytes, clean and resolved-conflict merges, disconnected ancestry, source/target head movement, cancellation before publication, replay, provenance, dirty working, browser checkout, stash restore, and device conflict. |
| 500-version service fixture, 20 samples | History p95 **95 ms**; exact comparison with verified differing XMP p95 **175 ms** | First history page 100 nodes and next offset 100; exact compared versions and semantic differences. Local nominal load, below 2 s and 3 s targets. |
| `VERSION_CONTROL_INTEGRATION=1 dotnet run --project tests/version-control/Platform.VersionControl.Tests.csproj --no-restore` | Pass after migration 019 | Feature 006 ref, commit, rewrite/recovery, 90-day boundary, hide/reveal, restore/revert, audit, checkout, and merge regression. Storage-backed 100-entry commit p95 468 ms; 10k-node projection first page 7 ms. The separate 1-million-entry optional capacity run was not repeated for this feature. |
| Foundation and Windows sync `dotnet test ... --no-restore -v:q` | Pass | Existing unrelated contract regressions. |
| Web `npm run typecheck`, `npm run test`, `npm run test:accessibility`, `npm run build` | Pass | Types, source and rendered accessibility structure, gallery/lineage/merge semantics, static bundle. |
| Changed web files `npx prettier --check ...` | Pass | All changed TypeScript and JavaScript files. Repository-wide `npm run format` reports nine pre-existing files outside this feature, including generated `test-results/.last-run.json`; those files were left alone. |
| Playwright behavior matrix, `BROWSER_TEST_PORT=1624`, Chrome for Testing/Edge/Firefox | **33 passed** | Gallery context, hidden ancestry, comparison on failed preview, merge review, mutation pause, checkout, staged commit, ref/tag/visibility/restore, rewrite/recovery, authorization, and three locale/time-zone settings. Additional stash list/restore journey passed in all three browsers after the broad run. |
| Fresh sign-in browser check | **3 passed** | On `recent_authentication_required`, each engine follows the Cognito hosted `/logout` redirect with return state, compatible with classic hosted UI. |
| 500-version browser fixture, 20 samples per engine | Chrome p95 **1,010 ms**; Firefox p95 **1,475 ms**; Edge isolated p95 **986 ms** | Initial usable history page capped at 100, on-demand next page. Edge showed 6,641 ms in one shared browser run, then 986 ms when rerun alone; local contention is a measurement limitation. |
| Existing gallery browser timing, isolated 20-sample runs | Chrome 1,228 ms; Edge 1,329 ms; Firefox 1,724 ms page p95 | Existing gallery timing target remains met. |
| Infrastructure `npm run check`, `npm run synth`, `npm run assert` | Pass | Lineage graph/outcome metrics, privacy-safe event filters, two sustained alarms with runbook links, 15-minute evaluation window. |
| `git diff --check` | Pass | No whitespace errors. |

## Requirement outcomes

- **AC-001–003, AC-016:** Exact parents, refs/tags, hidden ancestry, table search, comparisons, and preview provenance are covered by unit and browser fixtures. Absent image and no common ancestor are explicit.
- **AC-004–005, AC-007, AC-012, AC-015:** Conservative field policy, conflict review, complete affected and retained state, atomic two-parent publication, source/target freshness, duplicate replay, and canceled confirmation are covered by disposable integration checks.
- **AC-006, AC-008, AC-010–011:** Feature 006 regression verifies protected rewrite, 90-day recovery, tag/ref audit, visibility, and restore/revert. Browser journeys verify the controls.
- **AC-009, AC-013–014:** Keyboard-operable semantic controls, rendered accessibility checks, checkout block/preserve/stash restore, and exact gallery URL context are covered in browser checks. A manual screen-reader pass remains a release gate.
- **NFR-001/002/014:** Service and browser capacity fixtures use 500 relevant versions and a 100-node initial page. The optional 1-million-entry feature 006 capacity run remains separate.
- **NFR-003/004/005/009/010/012/015/016:** Owner routes, signed `auth_time` age, content authority, safe error codes, immutable/audited history, stale/dirty checks, idempotency, no preview substitution, and retention are exercised locally. The canceled confirmation fixture stops before publication; feature 006 transaction and stale-head cases cover publication atomicity.
- **NFR-008/011/017:** Local API configuration disables mutation controls while preserving reads. Structured events contain operation IDs and counts, and infrastructure synthesis verifies metrics and 15-minute alarms. Additive migrations 018/019 and rollback guidance retain generated-content provenance, exact tag audit links, and existing history.
- **NFR-006/007/013:** Automated accessibility structure, three current desktop browser engines, and `en-US`/New York, `fr-FR`/Paris, `th-TH`/Bangkok date rendering pass. English is the initial interface language.

## Release gates outside this local run

- Manual keyboard and screen-reader exercise against the deployed UI, plus a formal WCAG 2.2 AA review, still require a human and assistive technology environment.
- Pre-production rollout/rollback with the deployed Cognito hosted UI logout-to-login path, live alert delivery, and production-like 1-million-entry load require release authorization and environment access. The local migration, API/web build, capability pause, and CDK template checks do not claim that deployment exercise.
- No push was requested. The repository pre-push alternate-model review gate has therefore not been run.

## Convergence

The Spec Kit prerequisite check resolved this feature directory and no extension hooks were configured. The first audit checked 23 functional requirements, 16 acceptance criteria, 17 non-functional requirements, six success criteria, five plan decisions, and five constitution principles. It found one high-severity partial FR-018 gap and appended T017. After migration 019 and the tag audit integration check, the second audit found zero actionable missing, partial, contradictory, or unrequested items in the buildable scope. External release gates remain listed above.
