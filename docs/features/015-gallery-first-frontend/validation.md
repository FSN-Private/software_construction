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

## 2026-09-30 viewport and preview regression checks

- Opening Filters or Image details now locks document scrolling while the dialog retains its own scroll. Closing the dialog restores the gallery position. The shared modal manages the scroll-lock class; the detail open action no longer jumps to the top.
- The interactive Playwright preview uses the browser window's viewport, so maximizing the window expands the gallery. Its synthetic 100,000-image response now pages through distinct 40-image sets with usable Next and Previous controls. The preview remains read-only and does not represent live 100,000-asset service capacity.
- All **75 frontend browser cases passed** across Chrome for Testing 153.0.8010.12, Edge 154.0.4258.37 and Firefox 155.0. The preview verification passed in Chrome; normal owner paging passed separately in all three browsers. Formatting, type checking, source/accessibility checks and production build passed.
- Full-suite runs on this workstation hit intermittent performance-test failures while unrelated browser actions also slowed sharply. The final frontend-only matrix passed, including browser-native drawer p95 values of Chrome **91 ms**, Edge **98 ms** and Firefox **67 ms**; these exclude Playwright command latency. The existing live-reference 100,000-asset test still awaits its external host under T009. Owner review of the maximized browser remains outstanding.

## Required external evidence before completion

1. Provide a disposable authorized PostgreSQL/S3 reference host with 100,000 assets, cached derivatives, and a 500-version image. Set `GALLERY_LIVE_API` for the browser live-reference case; capture 30 page samples and 40 cached S3 thumbnails in each browser, plus 500-version service usability. Confirm the test host accepts the fixture token or provide a safe equivalent owner-auth configuration. Record p95 values against NFR-001/002 and asset/version population evidence.
2. Have the owner perform the rating + camera/tag → image → capture metadata/history → gallery return journey and time it against SC-002's two-minute target. Review the six screenshots beside the approved mockup with representative photos and record explicit visual acceptance against SC-001.
3. Complete the primary browse/filter/detail/version/return/compare flows using keyboard and a supported screen reader. Record browser, screen-reader version, focus/announcement observations, any fixes, and an unresolved-blocker count of zero for SC-005/NFR-006.
4. In an authorized pre-production environment, run the existing immutable-web-artifact rollout and rollback, then verify original/XMP/commit/ref/working state hashes or IDs and existing direct links remain intact. Record artifact IDs, operation IDs, evidence, and recovery outcome for NFR-013/014. Deployment authorization is separate from implementation approval.

The [web guide](../../../apps/web/README.md) documents usage, preview/session recovery, and rollback intent. T009 and convergence task T011 remain open until the external checks above pass or the owner explicitly waives a named requirement. T010's completion gate likewise remains open pending that evidence and final convergence.

## 2026-10-01 PR #30 CI follow-up

### Retrieved live-capacity evidence

On follow-up, the completed [backend job in run 36817362744](https://github.com/FSN-Private/software_construction/actions/runs/36817362744/job/110225232794) supplies real PostgreSQL/S3 capacity evidence previously missing from this feature record. With `GALLERY_CAPACITY=1` and `GALLERY_LIVE_BROWSER=1`, the 100,000-asset server browse p95 is **156.8 ms** for a filtered 40-card page (25 measured runs after 5 warmups). The redesigned UI's live page/cached-S3-thumbnail p95 results are Chrome **1083/42 ms**, Edge **1172/51 ms**, and Firefox **973/73 ms**; all three live-reference cases passed. This satisfies the gallery portion of NFR-001/SC-006 on CI's disposable reference environment, not production rollout evidence.

Persisted reference-scale history checks also pass: 10k-node library lineage p95 **172 ms**, image lineage **139 ms**, and image history with hidden ancestry **152 ms**. These support service performance but do not record the exact 500-version gallery-overlay end-to-end exercise required by T009. The fixture browser history measurements remain separate. Supported screen-reader, timed owner journey, reference-based visual acceptance, and pre-production rollout/rollback evidence are still pending.

The first PR run passed Windows checks but failed backend formatting, the Firefox browser matrix, infrastructure audit, and Trivy security scanning. The backend log identified only seven whitespace violations in the new gallery facet switch. `dotnet format` changed only that switch's line layout; `dotnet format --verify-no-changes --no-restore` and the API build now pass locally.

Firefox reported a 1505px gallery scroll jump after closing image detail and restoring focus to the initiating tile. The shared modal now restores focus with `preventScroll`; the existing Chrome regression passes locally, along with web formatting and type checking. The local Firefox runner could not create a page (`browserContext.newPage` raised an internal `_page` TypeError before test execution), so the PR's next Linux Firefox run must verify this correction.

PR run 36815635593 confirmed the backend formatter, Windows checks, and Chrome/Edge browser cases pass. Its Firefox run still reported the same 1505px difference at the immediate scroll assertion. The gallery intentionally restores scroll in `requestAnimationFrame`, while the test's focus assertion can succeed before that frame; the scroll assertion now polls for the restored position within Playwright's existing timeout. The next CI run must establish whether restoration actually completes. This change does not relax the final 5px scroll tolerance.

PR run [36816408716](https://github.com/FSN-Private/software_construction/actions/runs/36816408716) completed: backend, web (full Chrome/Edge/Firefox matrix), and Windows passed. The required aggregate failed solely because infrastructure audit and Trivy security checks still reject the bundled `brace-expansion` 5.0.9. T020's in-repository format/scroll work is complete; T021 remains the CI blocker. T009/T010/T011 external feature-acceptance evidence also remains open, so the PR is not ready to merge.

Infrastructure npm audit and Trivy both found high-severity denial-of-service advisories for `brace-expansion` 5.0.9 ([GHSA-6j4f-fj2g-mc7p](https://github.com/juliangruber/brace-expansion/security/advisories/GHSA-6j4f-fj2g-mc7p), [GHSA-qhr7-859c-m2p7](https://github.com/advisories/GHSA-qhr7-859c-m2p7)). This copy is **bundled** inside `aws-cdk-lib` 2.267.0. The official npm registry's latest `aws-cdk-lib` 2.272.0 tarball was inspected: it still contains `package/node_modules/brace-expansion/package.json` at 5.0.9. The published package is Apache-2.0, npm integrity is recorded by the registry, and its bundled `brace-expansion` is MIT; no untrusted package scripts were run. [npm's package documentation](https://docs.npmjs.com/cli/configuring-npm/package-json/#overrides) explains that published packages can dictate resolutions through bundled dependencies; the [CDK upstream report](https://github.com/aws/aws-cdk/issues/38496) specifically notes consumer overrides do not fix its bundled `brace-expansion`. A lockfile-only version change cannot patch the tarball, so no misleading or unverifiable dependency edit was made. T021 remains open for an upstream release with a patched bundle, followed by full audit, infrastructure synthesis/assertions, and Trivy confirmation. Security scanning remains enabled and failing until fixed; no high-severity risk waiver or scanner exception has been accepted.

## Owner-approved patched CDK distribution

On 2026-10-01 the owner approved the concrete temporary archive in [ADR-0013](../../architecture/decisions/0013-temporary-patched-cdk-bundle.md) after the isolated candidate passed validation. An npm override experiment confirmed it still installs 5.0.9. The adopted local CDK archive instead contains the official `brace-expansion` 5.0.12 files; all 7,350 upstream CDK archive entries are regular files and every file outside the replacement subtree is unchanged. Input SRI hashes, output SHA-512, licenses and removal strategy are recorded in the vendor provenance and infrastructure guide. No dependency install scripts or scanner exemptions are used.

Local results for T022/NFR-019:

- `npm ci --ignore-scripts --no-audit` in the real infrastructure package: passed; installed nested version is 5.0.12.
- `rebuild-cdk-bundle.py` reproduced the approved archive and verified exact original/replacement contents and output digest. Its negative verification of the original vulnerable archive correctly failed. `validate:vendor` rejected the old installed 5.0.9 copy before clean install and passes for the patched archive/lockfile/install.
- `npm run validate:infrastructure`: passed provenance verification, TypeScript checks/build, offline synthesis, template assertions, secret scanning, dependency tree, licenses and high-severity audit. The isolated install also reported zero npm vulnerabilities.
- `npm run test:release-evidence` and `npm run test:reliability`: passed. No application behavior, migration or deployment changed; prior passing API/web tests remain applicable.
- Independent pre-push review and the next GitHub Trivy/required CI run remain necessary before closing T022. T021's upstream-only repair is replaced by the approved T022 alternative once those checks pass. The exit to an upstream distribution remains documented maintenance work.
