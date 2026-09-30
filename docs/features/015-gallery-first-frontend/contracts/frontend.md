# Frontend Integration Contract

Reuse authorized session/status, gallery browse/detail/preferences/edit/location, source download, renditions, image history/compare/capabilities and version-control refs/commits/checkout. The 2026-09-30 revision adds one read-only API endpoint; no schema changes.

Browse forwards supported facets/controls only; never UI-only asset/history/range keys. Search scope maps to path/title/description. Counts and composition server-authoritative.

Historical selection uses compare target/source commit IDs and exact asset/XMP/provenance, explicit render actions and clear missing-historical-metadata disclosure. Full lineage retains graph/protected mutations/recent-auth.

Native filters draft/apply/cancel and focus containment; branch picker uses existing expected-state checkout. Secondary Library preserves lifecycle integrity. Navigation retains owner-only context/drafts; sign-out clears private UI/hosted session.

## Filter values revision

`GET /v1/libraries/{library}/gallery/filter-values?branch={branch}&facet={facet}&q={optional}` requires the existing owner authorization and canonical gallery library. Allowed facets: `camera`, `lens`, `fileType`, `tag`, `color`, `rating`, `orientation`, `focalLength`, `aperture`, `shutter`, `iso`, `width`, `height`, plus `latitude`/`longitude` only when the owner's location privacy preference is set to show. Response: `{ "values": [{ "value": "...", "label": "..." }], "hasMore": false }`, ordered by normalized value, at most 100. An optional query `q` (up to 100 characters) restricts returned values. Values come from active/restored assets in the selected branch's active generation, independent of page cursor. Unknown facet/invalid query returns `400 invalid_filter`; wrong library returns `404 library_unavailable`; hidden location returns `400 location_hidden`. Response is private/no-store. Numeric strings are invariant and can populate existing min/max controls without changing browse syntax. Client aborts stale requests and keeps direct URL filters usable if choices cannot be loaded.

Directory import uses the existing import-batch payload with each browser-provided `webkitRelativePath` as `path`. Exact duplicate paths are rejected before POST; identical basenames in different directories are valid. The local preview fixture is read-only and disables import controls with an explicit explanation.
