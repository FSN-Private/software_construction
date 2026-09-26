# Gallery Validation Quickstart

1. On the approved branch, apply migrations to a disposable PostgreSQL library and import the legally usable 005 image/XMP corpus. Record source checksums and build the gallery projection.
2. Search each facet and combine at least three. Page forward and back under each sort with equal/null sort values; verify exact count, no duplicates, and cursor expiry after a relevant mutation.
3. Open detail for embedded-only, XMP-overridden, intentionally cleared, malformed, and missing values. Check source and capture/import timestamp labels.
4. Hide location. Verify no coordinates in cards, normal details, query results, facet counts, responses, or telemetry; use the explicit owner reveal to verify retained XMP.
5. Edit every permitted field, confirm an uncommitted web working change and unchanged original checksum, synchronize two Windows devices on the branch, and round-trip the XMP through the supported darktable runtime. Repeat with a stale edit and a device-local conflict.
6. Exercise import, download, recoverable delete, and restore from the portal. Verify owner authorization, audit events, pending/failed preview labels, accessible controls, and preserved browse URL context.
7. Run a 100,000-asset reference workload and record initial-page and cached-thumbnail p95, refresh recovery, and the migration rollback-generation check in `validation.md`.

The ordinary browser check is `npm --prefix apps/web run test:browser` after installing Playwright's Chromium, Firefox, and Edge browser binaries. It exercises owner UI behavior with a mocked authenticated API. The connected reference runs from `npm run test:ingest` with `INGEST_INTEGRATION=1`, `GALLERY_CAPACITY=1`, and `GALLERY_LIVE_BROWSER=1`; it seeds a disposable 100,000-asset PostgreSQL/S3 fixture and launches the same three browsers against the real browse and rendition locator services. The test host uses a test-only owner token and local HTTP S3 adapter.
