# Web validation

After owner sign-in, the library screen accepts images and darktable XMP sidecars, calculates SHA-256 locally, shows resumable per-item progress and corrective quarantine codes, and verifies downloaded bytes. Keep source files until a batch reports active. Reselect the same files and batch ID to resume an interruption.

Move/rename preserves identity and path history. Delete is recoverable for 90 days; the screen displays the locale-formatted deadline and accepts an alternate path when the original destination is occupied. A hold blocks deletion. An integrity-error record remains visible but cannot be downloaded or changed until the recovery runbook is completed.

From this directory, run `npm run format`, `npm run typecheck`, `npm run test`, `npm run test:accessibility`, and `npm run build`.

The portal is a static, semantic owner client. Automated source/render checks cover labels, landmarks, announced progress/errors, and recovery controls; a production release still requires the repository's broader WCAG 2.2 AA validation.

For Gallery, set API `Gallery:LibraryId` and the matching runtime `galleryLibraryId` in `/platform-config.js`. Gallery uses the same owner session and links to Library for import, delete, and restore. Search filters, sorting, and page position stay in the URL. Open a card to inspect metadata source and preview provenance; use the explicit buttons to download an original or request a full preview. The edit form supports rating, rejection, color, tags, title, description, and location. Saving creates an uncommitted branch working change; a `stale_working_state` response refreshes detail so the owner can review and retry. A later version-control stage and commit publishes the XMP change.

From gallery detail, **View image history** opens an image-filtered lineage graph and equivalent searchable table on the selected branch. The page displays exact library-wide commit and parent IDs, branch/tag labels, hidden ancestry, and a show-hidden control. Back to image details preserves the gallery's asset, branch, filters, sort, and page position.

Select two commits in lineage and choose **Compare versions** to see exact XMP identities, the common ancestor, and committed differences. Request each historical preview separately. Pending, failed, or unavailable rendering leaves the textual comparison usable; a completed preview is displayed only when its provenance matches that commit and XMP digest.

The lineage workspace also prepares library-wide merges. Choose source and target branches, review every affected image and retained target state, select a side for each conflict, inspect the exact resulting XMP, then confirm with a commit message. A changed head or working state requires a fresh preparation. Branch and tag controls, hide/reveal, restore/revert, staged commits, non-default rewrite preview, and 90-day reflog recovery use version-control audit and state checks. Rewrite and recovery require a fresh owner sign-in; the sign-in action clears the Cognito hosted session before returning to history. Branch switching offers block, preserve, or confirmed discard when browser working changes exist; device conflicts block a switch until resolved. Gallery metadata saves remain uncommitted until staged and published.

## Gallery-first workspace (feature 015)

After owner sign-in, Gallery is the default view. Search selects filename/path, title or description using existing AND-composed metadata semantics. Change density for a tighter contact sheet. Sort and applied criteria remain in the owner URL.

Filters opens a temporary left drawer. Common criteria appear first; More filters exposes every existing facet. Rating is exact-match. Show photos applies the draft; Reset clears draft fields and takes effect when applied. Close, Escape or backdrop cancels unapplied changes. Remove a chip to remove only that filter. Invalid ranges remain in the drawer for correction. Gallery privacy controls location visibility.

Click a photograph for a contained preview and metadata/history inspector. Metadata labels identify source and missing/cleared state. Select a commit for its exact image/XMP identity and available preview, or explicitly request a historical render. Complete historical field metadata is not supplied by the existing service and is labelled unavailable; current working metadata is separate. Compare versions/View image history opens existing full lineage and mutation tools. Global History shows library-wide commits with image entry points.

Image actions and Edit XMP metadata progressively disclose download/full-preview/lifecycle and organizational edits. Changes stay uncommitted. Unsaved drafts are retained per branch/image in bounded session storage for reauthentication, warn on leaving, and block branch switching until saved/discarded. Sign-out clears private UI/drafts and ends the hosted session. Import and Service details are secondary destinations; returning keeps gallery state. Full graph/merge/recovery safeguards remain unchanged.

No data migration is required. Release/rollback uses the existing immutable web artifact process; rolling back presentation does not change image/XMP/history storage. Keep existing platform-config and API contracts. Preview/session failures have explicit statuses and retry/sign-in paths. View Service details from Owner controls for correlated diagnostics. Deployment requires separate authorization.

For a separately running local Vite server on Windows, set `BROWSER_TEST_EXTERNAL_SERVER=1` before the browser command to avoid managing that server as a Playwright child. Fixture screenshots and measurements appear under ignored test-results; live capacity, supported screen-reader, rollout/rollback and owner visual acceptance remain separate evidence requirements.

To inspect the gallery locally without a configured owner backend, keep your Vite server running, then run from the repository root in a second PowerShell terminal. The browser test defaults to port 4173; set `BROWSER_TEST_PORT` to your server's actual port only if it differs.

```powershell
$env:GALLERY_PREVIEW='1'
$env:BROWSER_TEST_EXTERNAL_SERVER='1'
npm --prefix apps/web run test:browser -- preview.spec.ts --project='Chrome for Testing' --headed
```

Playwright starts the web client, supplies non-private fixture gallery responses, and pauses with an interactive browser. The fixture thumbnails are gray placeholders; use the implementation screenshots in `docs/features/015-gallery-first-frontend/reviews/implementation/` for the layout reference. Close the browser or stop the command to finish. This preview does not authenticate against Cognito or display private library photos. For real data, the deployed `/platform-config.js` must name the Cognito hosted UI, API, exact registered callback origin, and canonical `galleryLibraryId`; a standalone Vite server has none of these by default.

If **Sign in as owner** returns to the same page, check the post-click path. `/oauth2/authorize` on the Vite origin means the configured hosted-UI URL points back at the frontend, so Vite serves the app again instead of Cognito. If the browser returns to `/` without an ID token, check the Cognito callback registration. If it returns with a token but shows a session error, check `/v1/session` authorization and API CORS for the exact web origin. Never use the browser-test fixture configuration as a real Cognito configuration, and do not paste an ID token into an issue or chat.
