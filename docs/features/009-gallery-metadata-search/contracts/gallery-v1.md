# Gallery Contract v1

All routes require the existing owner bearer token and return `Cache-Control: private, no-store`. `{library}` is an authorized library identifier. Error bodies include stable `code` and `operationId`; no sensitive metadata appears in errors or logs.

| Route | Input | Output / behavior |
|---|---|---|
| `GET /v1/libraries/{library}/gallery` | Allowlisted facet query, `sort`, `direction`, `limit` (1–100), optional opaque `cursor`, branch | Cards, exact `count`, active filter labels, sort field, `nextCursor`, generation. AND across facets, OR within one multi-value facet. Hidden location is excluded before all filter/count work. |
| `GET /v1/libraries/{library}/gallery/{assetId}` | Branch, optional browse query | Identity/path, field value + source + missing/cleared state, sort timestamp basis, working context, preview state and provenance, lifecycle version. Normal response obeys location preference. |
| `GET /v1/libraries/{library}/gallery/{assetId}/location` | Explicit reveal action | Owner-only retained coordinates with audit; never embedded in normal cards/details. |
| `PUT /v1/libraries/{library}/gallery/preferences` | `hideGeolocation`, expected version, idempotency key | Updated owner preference/version. Default is hidden. |
| `PATCH /v1/libraries/{library}/gallery/{assetId}/metadata` | Branch, edit ID, allowlisted field changes, expected working version, expected base SHA-256, idempotency key | New XMP checksum, uncommitted working version/context. `409 stale_working_state` gives refresh/retry path; original checksum cannot change. |

The PATCH requires `Idempotency-Key` (1–200 characters). Repeating the same key and payload returns the original result; reusing the key for a different payload returns `409 idempotency_conflict`. The web XMP content association, feature-006 working row, audit event, and replay result commit in one database transaction. A device namespace item includes `webWorking` and `conflict`; divergent device-local XMP is retained and shown as a conflict.
Preference PUT uses the same key length and replay/conflict rules; its version update and replay record commit together.
| Existing feature-005 lifecycle routes | Import batch, original download, delete, restore with lifecycle version | Gallery links/delegates; source service remains authoritative and writes lifecycle audit. |

Facet keys: `captureFrom/To`, `importFrom/To`, `path`, `fileType`, `camera`, `lens`, `focalLengthMin/Max`, `apertureMin/Max`, `shutterMin/Max`, `isoMin/Max`, `widthMin/Max`, `heightMin/Max`, `orientation`, `rating`, `rejected`, `color`, `tag`, `title`, `description`, `creator`, `locationPresent`, and location bounding box when visible. Sort keys: `captureDate`, `importDate`, `filename`, `rating`, `dimensions`; both directions, nulls last, asset ID tie-breaker. Invalid facet combinations return `400 invalid_filter`. Cursor mismatch or generation change returns `409 cursor_expired`; a missing or deleted asset returns `404 asset_unavailable`; an unavailable preview reports `pending` or `failed` without another asset's locator.
