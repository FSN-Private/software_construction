# Web validation

After owner sign-in, the library screen accepts images and darktable XMP sidecars, calculates SHA-256 locally, shows resumable per-item progress and corrective quarantine codes, and verifies downloaded bytes. Keep source files until a batch reports active. Reselect the same files and batch ID to resume an interruption.

Move/rename preserves identity and path history. Delete is recoverable for 90 days; the screen displays the locale-formatted deadline and accepts an alternate path when the original destination is occupied. A hold blocks deletion. An integrity-error record remains visible but cannot be downloaded or changed until the recovery runbook is completed.

From this directory, run `npm run format`, `npm run typecheck`, `npm run test`, `npm run test:accessibility`, and `npm run build`.

The portal is a static, semantic owner client. Automated source/render checks cover labels, landmarks, announced progress/errors, and recovery controls; a production release still requires the repository's broader WCAG 2.2 AA validation.

For Gallery, set API `Gallery:LibraryId` and the matching runtime `galleryLibraryId` in `/platform-config.js`. Gallery uses the same owner session and links to Library for import, delete, and restore. Search filters, sorting, and page position stay in the URL. Open a card to inspect metadata source and preview provenance; use the explicit buttons to download an original or request a full preview. The edit form supports rating, rejection, color, tags, title, description, and location. Saving creates an uncommitted branch working change; a `stale_working_state` response refreshes detail so the owner can review and retry. A later version-control stage and commit publishes the XMP change.

From gallery detail, **View image history** opens an image-filtered lineage graph and equivalent searchable table on the selected branch. The page displays exact library-wide commit and parent IDs, branch/tag labels, hidden ancestry, and a show-hidden control. Back to image details preserves the gallery's asset, branch, filters, sort, and page position.

Select two commits in lineage and choose **Compare versions** to see exact XMP identities, the common ancestor, and committed differences. Request each historical preview separately. Pending, failed, or unavailable rendering leaves the textual comparison usable; a completed preview is displayed only when its provenance matches that commit and XMP digest.

The lineage workspace also prepares library-wide merges. Choose source and target branches, review every affected image and retained target state, select a side for each conflict, inspect the exact resulting XMP, then confirm with a commit message. A changed head or working state requires a fresh preparation. Branch and tag controls, hide/reveal, restore/revert, staged commits, non-default rewrite preview, and 90-day reflog recovery use version-control audit and state checks. Rewrite and recovery require a fresh owner sign-in; the sign-in action clears the Cognito hosted session before returning to history. Branch switching offers block, preserve, or confirmed discard when browser working changes exist; device conflicts block a switch until resolved. Gallery metadata saves remain uncommitted until staged and published.
