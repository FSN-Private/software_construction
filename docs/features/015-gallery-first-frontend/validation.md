# Gallery-first frontend validation

**Branch:** `feature/015-gallery-first-frontend`

**Local implementation:** M0 `a28b12d`, M1 `3258f0f`, M2 `9364eb4`; M3 local evidence is recorded here.

**Result:** The implemented UI and deterministic local checks pass. T009's real-environment and participant evidence is outstanding; the feature completion gate is not met.

## Reviewable layouts

Fixture screenshots use non-private gray thumbnail images, so they show hierarchy, spacing, controls, and reflow, not photographic quality. Compare them with the [approved mockup](references/approved-mockup.png) and [visual criteria](references/visual-reference.md). The UI displays actual authorized renditions when available; the fixture does not embed example photography in the product.

| View | 1440 × 960 | 1280 × 800 |
|---|---|---|
| Gallery | [gallery](reviews/implementation/gallery.png) | [gallery](reviews/implementation/gallery-1280.png) |
| Filters | [drawer](reviews/implementation/filters.png), [date range](reviews/implementation/filters-dates.png) | [drawer](reviews/implementation/filters-1280.png) |
| Image detail | [inspector](reviews/implementation/details.png) | [inspector](reviews/implementation/details-1280.png) |

Automated geometry confirms five gallery columns at 1440, four at 1280, a 64-pixel header, grid beginning within 200 pixels, a compact left drawer 280–460 pixels wide, a 30–40% desktop inspector, and the second image-history entry visible within the 1280 × 800 viewport. The detail overlay stacks below 900 pixels. The gallery itself fills the 1440px viewport; no horizontal page scroll occurs at 768, 390, or 320 CSS pixels or with 200% text sizing in the checked fixture. A 30% control-label expansion fits at 1440 and 320. The owner has not yet accepted the implemented layouts against the reference.

## Local checks

Executed on 2026-09-29/30 with Node 24.20.0, Playwright 1.63.0, Chrome for Testing 153.0.8010.12, Microsoft Edge 154.0.4258.37, and Firefox 155.0:

- `npm --prefix apps/web run format`, `typecheck`, `test`, `test:accessibility`, and `build`: pass. Rendered accessibility checks cover gallery and retained lifecycle/lineage views; they are not a supported screen-reader walkthrough.
- Final full Playwright browser matrix: **99 passed, 3 skipped**. The three skipped cases are the same live 100,000-asset reference exercise, one per browser, because `GALLERY_LIVE_API` is unset.
- Deterministic 100,000-card fixture page p95 / first cached thumbnail p95: Chrome **1008/66 ms**, Edge **1059/56 ms**, Firefox **1245/69 ms**. The 500-version lineage fixture usable p95: **449/485/772 ms** respectively. Drawer usable p95: **99/92/174 ms** respectively. All are within local targets. These synthetic results do **not** substitute for service/capacity evidence.
- The browser suite covers applied filters and all retained facets, all sort/search modes and combined query cases, pagination/density, gallery ↔ detail/history/import navigation and scroll/focus restoration, portrait/panorama contained previews, merged/hidden/repeated image history, exact version/XMP/source preview checks, stale/out-of-order reads, empty/error/session-expiry/unsaved states, original/render request limits, privacy, branch/merge/rewrite/recovery safeguards, narrow/reduced-motion/text-expansion behavior, and locale/time-zone output.
- Normal text/control token contrast checks passed the specified 4.5:1 / 3:1 thresholds. Automated keyboard focus and dialog trapping pass. A human keyboard and supported screen-reader journey is still required.
- `dotnet run --project tests/version-control/Platform.VersionControl.Tests.csproj --no-restore`: contract/projection/rewrite/recovery/visibility checks pass; its PostgreSQL atomicity/concurrency and million-entry capacity paths require `VERSION_CONTROL_INTEGRATION=1` / `VERSION_CONTROL_CAPACITY=1` and were not exercised. `dotnet run --project tests/lineage/Platform.Lineage.Tests.csproj --no-restore`: lineage projection/route checks pass; PostgreSQL/S3 merge integration requires `LINEAGE_INTEGRATION=1` and was not exercised.
- `git diff --check` passes. The owner revision adds a read-only filter-values API without a dependency, schema migration, new service, or infrastructure change. No push or deployment was attempted.

Spec Kit convergence checked FR-001–018, AC-001–014, SC-001–006, the plan, existing tasks, and the repository constitution. It found one missing explicit task assignment for SC-002's timed owner journey and appended T011. The remaining external checks are already assigned to T009/T010; there is no additional local implementation task identified.

## 2026-09-30 owner revision checks

- Final revised Playwright matrix: **114 passed, 3 skipped** across Chrome for Testing 153.0.8010.12, Edge 154.0.4258.37, and Firefox 155.0. The skipped case in each browser requires the unavailable live 100,000-asset reference host. The standalone interactive preview verification also passed in Chrome.
- Revised fixture p95 page/first visible cached thumbnail: Chrome **732/161 ms**, Edge **760/92 ms**, Firefox **816/146 ms**. Drawer usable p95: **91/95/115 ms**. These are browser-native timings and stay below the existing local thresholds.
- The API build and disposable PostgreSQL/MinIO integration suite pass, including gallery filter-value owner isolation, branch scoping, searched camera values, numeric values, invalid facets, hidden-location rejection, and visible owner latitude choices. The API returns at most 100 choices per request from the active projection; browser checks cover a truncated list and search. No schema migration or new dependency was added.
- Browser cases cover collapsed filter groups, one-row date ranges, full-width gallery geometry, branch-derived camera/numeric choices, owner-authorized location choices, free numeric bounds, same-overlay image lineage/comparison, exact selected preview states, Escape/Back/focus restoration, recursive folder paths, duplicate destination blocking, and the read-only preview import explanation.
- The interactive preview now keeps the headed browser open without the docked Playwright Inspector. A maximized browser can use the full viewport. This preview remains fixture data, not a real owner backend; actual import requires the configured owner API.
- The cached-thumbnail timing check now records browser-side page and first-image loaded timestamps. The earlier cross-process check included the time to load all 40 thumbnails and could mislabel that as first-image latency. All three browsers meet the same <2 s page and <500 ms first-thumbnail thresholds in the corrected local fixture check. This remains synthetic evidence, not the real 100,000-asset service test.
- Screenshots above were refreshed for the grouped drawer and gallery overlay. The date-range screenshot shows both date inputs on one line. External owner acceptance, supported screen reader, live capacity and deployment/rollback evidence remain T009/T011/T010 gates.
- Revision convergence checked FR-019–023, AC-015–018, NFR-017–018, the read-only values contract, implementation tasks T012–T017 and the repository constitution. It found one partial gap: owner-authorized latitude/longitude values had no selectable choices. T018 closed that gap with privacy-gated API/UI choices and integration/browser evidence. The follow-up convergence pass found no further buildable gap; the already-numbered external tasks remain pending. The requirements-quality checklist has 18 checked items and no unchecked item.

## Required external evidence before completion

1. Provide a disposable authorized PostgreSQL/S3 reference host with 100,000 assets, cached derivatives, and a 500-version image. Set `GALLERY_LIVE_API` for the browser live-reference case; capture 30 page samples and 40 cached S3 thumbnails in each browser, plus 500-version service usability. Confirm the test host accepts the fixture token or provide a safe equivalent owner-auth configuration. Record p95 values against NFR-001/002 and asset/version population evidence.
2. Have the owner perform the rating + camera/tag → image → capture metadata/history → gallery return journey and time it against SC-002's two-minute target. Review the six screenshots beside the approved mockup with representative photos and record explicit visual acceptance against SC-001.
3. Complete the primary browse/filter/detail/version/return/compare flows using keyboard and a supported screen reader. Record browser, screen-reader version, focus/announcement observations, any fixes, and an unresolved-blocker count of zero for SC-005/NFR-006.
4. In an authorized pre-production environment, run the existing immutable-web-artifact rollout and rollback, then verify original/XMP/commit/ref/working state hashes or IDs and existing direct links remain intact. Record artifact IDs, operation IDs, evidence, and recovery outcome for NFR-013/014. Deployment authorization is separate from implementation approval.

The [web guide](../../../apps/web/README.md) documents usage, preview/session recovery, and rollback intent. T009 and convergence task T011 remain open until the external checks above pass or the owner explicitly waives a named requirement. T010's completion gate likewise remains open pending that evidence and final convergence.
