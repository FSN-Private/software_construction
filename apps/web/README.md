# Web validation

After owner sign-in, the library screen accepts images and darktable XMP sidecars, calculates SHA-256 locally, shows resumable per-item progress and corrective quarantine codes, and verifies downloaded bytes. Keep source files until a batch reports active. Reselect the same files and batch ID to resume an interruption.

Move/rename preserves identity and path history. Delete is recoverable for 90 days; the screen displays the locale-formatted deadline and accepts an alternate path when the original destination is occupied. A hold blocks deletion. An integrity-error record remains visible but cannot be downloaded or changed until the recovery runbook is completed.

From this directory, run `npm run format`, `npm run typecheck`, `npm run test`, `npm run test:accessibility`, and `npm run build`.

The portal is a static, semantic owner client. Automated source/render checks cover labels, landmarks, announced progress/errors, and recovery controls; a production release still requires the repository's broader WCAG 2.2 AA validation.

For Gallery, set API `Gallery:LibraryId` and the matching runtime `galleryLibraryId` in `/platform-config.js`. Gallery uses the same owner session and links to Library for import, delete, and restore. Search filters, sorting, and page position stay in the URL. Open a card to inspect metadata source and preview provenance; use the explicit buttons to download an original or request a full preview. The edit form supports rating, rejection, color, tags, title, description, and location. Saving creates an uncommitted branch working change; a `stale_working_state` response refreshes detail so the owner can review and retry. A later version-control stage and commit publishes the XMP change.
