# Gallery Data Model

Existing `assets`, `asset_paths`, `edit_identities`, `content_objects`, `vc_materialized_state`, `vc_working_changes`, `windows_sync_devices`, `render_requests`, `renditions`, and `render_provenance` remain authoritative. Gallery tables are rebuildable projections except the owner preference.

| Entity | Key and fields | Invariant |
|---|---|---|
| `gallery_projection` | owner, library, branch, asset, generation; original/XMP SHA-256, schema version, import/capture timestamps, path, format, camera/lens/exposure/dimensions/orientation/rating/rejection/title/description/creator, location presence/coordinates, parse status | One complete record per asset/generation; no partially published row; source checksums identify the exact input. |
| `gallery_facet_values` | projection key, facet name, normalized typed value, source, state, source checksum, timestamp provenance | `missing`, `cleared`, and `invalid` differ; multi-value tags and colors are separate rows. |
| `gallery_generations` | owner, library, branch, generation, schema version, state, activated timestamp | Readers use only a complete active generation; an older complete generation remains selectable for rollback. |
| `gallery_refresh` | owner, library, branch, generation, cursor, total, completed, failed, last error code | Checkpoint advances after atomic asset publication; replay is idempotent. |
| `gallery_preferences` | owner, hide geolocation, version, updated timestamp | Defaults to hidden until owner explicitly changes it; preference never alters XMP. |
| Shared web overlay | Reserved web client working row plus branch/source version and event revision | One current web-originated sidecar per owner/library/branch/asset/edit; device-local rows remain independent. |

Facet normalization: text is Unicode normalized and case-folded for matching while display text is preserved; capture dates use UTC sort keys with original offset status retained; import date comes from the earliest completed original `upload_sessions.verified_at` for the asset; decimals use invariant units; dimensions are integer pixels; orientation is a bounded EXIF value; rating and colors follow darktable XMP meanings; tags are normalized individual labels; location has presence and normalized coordinates. Invalid or absent values never become an invented zero. Exact source mappings and accepted value bounds belong in the implementation and compatibility table added by T004.

Published state transitions: `queued -> parsing -> complete` or `error`; only `complete` replaces the current projection for an asset. If the current source changes before replacement, the prior projection is marked `stale` in every response until rebuilt. A deleted asset leaves normal search immediately through the lifecycle join even before refresh. A changed source checksum or branch generation invalidates stale jobs and cursors. Indexes cover owner/library/branch plus each sortable column, tag/color lookup, active state, and refresh checkpoint.

Migration `014_gallery_metadata.sql` is expand-only. It retains one active generation per owner/library/branch, enforces owner-to-asset identity at the database boundary, requires exactly one typed value for a present facet, and separates the default-hidden owner preference from rebuildable projection rows. The migration rehearsal applies every embedded migration twice, checks existing source identity and rendering tables, and rolls back a generation switch without losing the old active index.

## Extraction sources (T004)

The bounded reader accepts the feature-005 JPEG, PNG, TIFF, and DNG corpus (128 MiB original metadata read cap, 2 MiB XMP cap). Malformed or entity-bearing XMP is reported as `xmp_invalid`; an empty XMP field is `cleared`, an absent field is `missing`, and a malformed value is `invalid`. The projection adds immutable source checksums during publication. Unicode text uses NFKC for display, and tag/color search keys use invariant uppercase NFKC. Numeric values use invariant decimal notation.

| Facet | Embedded source | XMP override or source | Normalization |
|---|---|---|---|
| Capture date | EXIF DateTimeOriginal, then TIFF DateTime | `exif:DateTimeOriginal` | UTC ISO 8601 key; `explicit_offset` or `offset_unknown` provenance |
| Camera, lens | EXIF Make + Model; LensModel | `tiff:Model`, `exif:LensModel` | Trimmed NFKC text |
| Focal length, aperture, shutter | EXIF FocalLength, FNumber, ExposureTime | Corresponding `exif:*` attributes/elements | Positive invariant numbers; rational values converted to decimals |
| ISO, width, height, orientation | EXIF ISO, dimensions, orientation; JPEG/PNG headers for dimensions | `exif:ISOSpeedRatings`, PixelXDimension, PixelYDimension; `tiff:Orientation` | Bounded positive integers; orientation 1–8 |
| Rating, rejection | None | `xmp:Rating` 0–5; -1 means rejected; `darktable:rejected` | Bounded integer and Boolean |
| Title, description, creator | EXIF ImageDescription, Artist for title/creator | `dc:title`, `dc:description`, `dc:creator` | Trimmed NFKC text, RDF first list item |
| Tags, color labels | None | `dc:subject`, `darktable:colorlabels` | Individual NFKC invariant uppercase list items, max 128 |
| Geolocation | EXIF GPS | `exif:GPSLatitude` and `exif:GPSLongitude` | Decimal degrees, with XMP degree/minute reference accepted; bounded latitude/longitude |
| Import date, path, filename, file type | Feature-005 verified upload, current asset path, and source format | No XMP override | Derived during projection refresh; earliest completed original verification time |

When an embedded capture date has no offset, the reader labels it `offset_unknown` and stores the literal clock reading as a UTC key solely for stable sorting. The UI must show that qualification; it must not describe the key as a known instant. XMP wins only when its element/attribute is present, including clears and invalid values. A malformed sidecar does not silently replace embedded values.

## Refresh publication (T005)

`Gallery:LibraryId` selects the one canonical library for the first release. The hosted refresh worker discovers owners and their branches every 30 seconds. It reads only verified immutable originals and branch-selected XMP, skips rows whose checksums/path/import timestamp are current, and writes a projection plus all facet rows in one transaction. It re-reads the authoritative source inside that transaction and discards a parse if the source changed meanwhile. Session advisory locks prevent concurrent refresh runs for the same owner/library/branch. The refresh row holds a per-asset checkpoint and an invalidation revision, so an interrupted scan resumes and an update during a scan forces a new pass before it may report complete. The first complete scan activates the generation; the prior generation remains available for rollback.

Database triggers mark the affected projection stale and queue a refresh after import, lifecycle/path, sidecar identity, branch working, or committed-state changes. Deleted assets are excluded by the authoritative lifecycle join even if an older projection remains. Parse errors are stored as stable status codes, without path or metadata values in worker logs. The disposable PostgreSQL/S3 rehearsal validates import publication, replay, change invalidation, recovery, and checkpoint resume.
