# Feature 009 independent pre-push review

Date: 2026-09-25. Reviewer model: `gpt-5.6-terra`, different from the implementing GPT-6 model. Remote: `origin`; intended destination: `feature/009-gallery-metadata-search`; base: `origin/master` (`e98ef63` at review). No push has been requested or performed in this review round. Initial range: `e98ef63..ae0f1b1` (five feature commits); revised range: `e98ef63..98d26ce` (includes the cursor remediation commit). The reviewer was read-only.

## Round 1

Prompt scope: approved feature spec, plan, numbered tasks, contract, ADR-0003, validation record, complete `origin/master..HEAD` diff, code/tests/migrations/CI/docs/dependencies, and current local validation. The reviewer was asked to check all FR/AC/NFR/task obligations, correctness, concurrency, security/privacy, compatibility, performance, recovery, tests, and unrelated changes, with severity and blocking classification.

### Blocking B1: browser and thumbnail acceptance evidence

- Reviewer classification: **blocking, high**. T013 and T015 remain open. `validation.md` explicitly lacks authenticated current Chrome/Edge/Firefox interaction checks, end-user initial-page p95, and cached-thumbnail p95. Source/SSR accessibility checks do not prove those browser behaviors.
- Affected: T013, T015; AC-001–AC-010 final browser confirmation; NFR-001, NFR-002, NFR-007, NFR-008, NFR-011; SC-002.
- Impact: the 100,000-asset filtered server page p95 of 503.6 ms does not establish the two-second usable-page or 500-ms cached-thumbnail thresholds, or current-browser interaction behavior.
- Suggested verification: run authenticated reference fixtures in current Chrome, Edge, and Firefox; measure and record page usability and cached thumbnails; complete T013/T015.
- Implementer assessment: **valid, unresolved**. The local workspace has Edge 153 but no Firefox or current Chrome; there is no authenticated reference browser fixture or browser automation harness. The backend benchmark and static accessibility checks are recorded accurately as partial evidence. This is a required external validation dependency, not a code-style issue or a waived criterion.

### Non-blocking notes

- T015 delivery accounting is open because T013 and convergence are incomplete. Disposition: non-blocking note within B1's delivery gap.
- Browser accessibility checks should exercise filter changes, page history, detail return, edit conflict, and thumbnail failure in real browsers. Disposition: non-blocking note to include in T013.

The reviewer found no other blocking issue in backend requirements, concurrency/integrity, security/privacy, migration/rollback, dependency/CI hygiene, or existing darktable read/render evidence.

## Implementer follow-up after round 1

The implementer independently identified a paging risk under T006: the protected cursor hashed filters/sort/branch but did not bind owner/library or an in-place projection invalidation revision. A source update during paging could change ordering without expiring a cursor. Commit `98d26ce` binds owner/library in the filter hash and binds the active generation's `gallery_refresh.invalidation_revision` in the cursor. The PostgreSQL/S3 integration check now rejects a cross-owner cursor and a cursor after revision change. The full disposable integration suite and API `dotnet format --verify-no-changes` passed after the fix. B1 remains unresolved.

## Round 2

The same alternate reviewer inspected the revised range through `98d26ce`, confirmed the owner/library and invalidation-revision cursor remediation, and found no new blocker. B1 remained **blocking** because T013/T015 lacked current-browser and end-user performance evidence. Implementer disposition: valid and still open at that point.

## Round 3

The reviewer inspected the subsequent working-tree browser/CI changes and the pending record. Gate: **BLOCKED**. B1 remained high severity: the initial Playwright timing test intercepted every API request, returned a synthetic 40-card page after a fixed 504 ms delay, and timed only one data-URL thumbnail. That could not establish NFR-001, NFR-002, SC-002, or T013 against a connected 100,000-asset gallery. The reviewer also disputed the Chrome baseline identity and noted the validation row said 9/9 despite 12 ordinary test executions. No other new material backend, migration, authorization, privacy, XMP, or dependency blocker was found; the cursor disposition was explicitly accepted.

Implementer assessment: **B1 valid and fixed in M3 commit `9294028`**. `GalleryLiveBrowserChecks` seeds a 100,000-asset PostgreSQL projection and 40 completed cached JPEG renditions in local S3. It runs the actual gallery browse service, rendition locator, web app, and three browsers, measuring 25 initial pages after five warmups and all 40 lazy-loaded images per browser. The connected run exposed a real API/UI contract defect: the rendering endpoint returns `locator` while the web gallery expected `url`. The web client and mocked fixture now use `locator`, including full-preview behavior. A local test-only HTTP locator adapts the S3 SDK presigned URL to the emulator's HTTP endpoint; production locator code remains unchanged.

The Chrome identity dispute is **invalid with evidence**: the installed Playwright Chromium binary at `feature009-playwright/chromium-1243/chrome-win64/chrome.exe` reports its Windows `ProductName` as `Google Chrome for Testing` and version 153.0.8010.12. The Playwright `channel: 'chromium'` uses the full bundled browser rather than its headless shell; this is the actual Chrome for Testing executable used by the test project. The 9/9 row was a stale documentation count, now corrected to the full ordinary and connected suite results. The reviewer will reassess both dispositions in the next round.

Final connected validation on 2026-09-26: `INGEST_INTEGRATION=1 GALLERY_CAPACITY=1 GALLERY_LIVE_BROWSER=1` passed PostgreSQL/S3 migration, ingest, lifecycle, privacy, Windows sync, and 3/3 connected browser checks. The 100,000-asset filtered server browse p95 was 117.3 ms. The live 40-card page p95 was 691 / 674 / 730 ms and the 40 cached S3 JPEG thumbnail p95 was 70 / 61 / 71 ms in Chrome for Testing / Edge / Firefox. The ordinary browser suite passed 12 checks with three connected-only checks skipped. `npm run lint`, `npm run test:backend`, `npm run test:contract`, web build, accessibility, changed-file Prettier, changed .NET whitespace formatting, `git diff --check`, and `npm audit --audit-level=moderate` passed. The test-only owner token is local to the disposable HTTP host. `docs/features/009-gallery-metadata-search/validation.md` records scope and limitations. Spec Kit convergence found no remaining buildable gap; T013/T015 are complete and M3 is committed. No push has occurred.

## Round 4

The alternate reviewer inspected the complete `origin/master..9294028` range and this pending review record on 2026-09-26. **Result: PASS; no unresolved blocking finding.** The reviewer explicitly accepted B1 as valid and fixed by the connected 100,000-asset PostgreSQL/S3/browser check, the Chrome identity challenge as invalid with the installed binary's product metadata, and the cursor source-change risk as resolved. The reviewer found no new blocking implementation, validation, CI, security, privacy, migration, dependency, or compatibility issue. The test-only identity/session fixture and local HTTP S3 adapter were judged appropriate for the measured UI/query/locator/image-decode path; the validation record correctly limits the result to a disposable local reference environment.

The reviewer also verified that stable `ci / backend` installs browser engines and runs the connected capacity check, `ci / web` runs the ordinary three-browser suite, the `locator` response correction matches the rendering contract, and the prior findings and dispositions remain preserved in this record. The reviewer requested that this agreement be appended and the record committed unchanged as M4.

**Final gate: PASS.** Intended pending push scope is the seven feature commits in `origin/master..9294028` plus the review-only M4 commit to `origin/feature/009-gallery-metadata-search`. No push or merge has been requested or performed in this round; each still requires separate user authorization.
