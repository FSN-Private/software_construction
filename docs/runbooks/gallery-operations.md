# Gallery operations

## Configuration and progress

Set API `Gallery:LibraryId` and matching web `galleryLibraryId` for the one canonical owner library. Run migrations 014–017 before serving requests. The hosted gallery refresh worker polls every 30 seconds. Inspect `gallery_refresh` by owner, library, branch, and generation for `state`, `total`, `completed`, `failed`, `last_error_code`, and `updated_at`; never log facet values, filenames, tags, descriptions, or coordinates. The `Platform.Gallery` meter reports request latency, stable error codes, preview states, facet names, edit conflicts, and refresh completion/failure. These labels contain no submitted values.

## Refresh and recovery

An import, lifecycle change, XMP working change, or committed state change invalidates affected projections. The refresh checkpoint resumes after process interruption; a source change during a scan invalidates its revision and triggers a new pass. A failed source read records a safe code and leaves the previous row labelled stale. Check source object integrity and the failed count, repair through the ingest or version-control recovery runbook, then restart the worker. Do not mark a generation active by hand. A complete backfill activates the new generation transactionally; retain the prior complete generation until the new one passes counts and spot checks.

## Rollback

For an application rollback, deploy the prior API and web artifact together. Migrations are expand-only. If a new projection generation is wrong, pause refresh, verify the prior generation is complete, and set that generation active with the current active generation retired in one database transaction. Resume refresh after correcting the parser or source issue. Retain original and XMP content and feature-006 working rows. See the feature-009 release plan for the verification query and promotion gate.

## Web edits and Windows sync

Web edits publish verified XMP and a branch-scoped `__gallery_web__` working row in one database transaction. A stale expected version or checksum returns `stale_working_state`; the owner refreshes details, reviews the new value, and retries. Each eligible device sees the same XMP in its branch namespace. A divergent device-local working row remains separate and is flagged as a conflict; do not overwrite or auto-merge it. Explicit owner staging and commit moves the web XMP into the committed branch. A branch switch or revocation clears device content grants. A pending or failed thumbnail never substitutes another rendition; diagnose through the rendering operations runbook.
