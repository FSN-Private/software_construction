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
