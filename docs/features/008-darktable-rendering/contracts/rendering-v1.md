# Rendering Contract v1

All routes require the authenticated library owner. IDs and hashes are opaque strings; display text is localizable.

## Request a rendition

`POST /v1/renditions`

```json
{
  "assetId": "uuid",
  "libraryId": "opaque library identity",
  "editId": "uuid",
  "selectedVersion": "current | feature-006 version identity",
  "rendition": "thumbnail-512 | preview-2048",
  "profile": "render-v1"
}
```

Response is `202 Accepted` for new or coalesced work and `200 OK` for an exact completed cache hit. It contains `requestId`, `state`, `attemptId` when present, `cacheHit`, a localized `statusText`, and `provenance` only for `completed`.

## Status and retry

- `GET /v1/renditions/{requestId}` returns the current safe state, latest attempt, retry availability, and completed provenance.
- `POST /v1/renditions/{requestId}/retry` requires an authorized retryable request and creates a distinct attempt.

## States and safe failure codes

| State | Codes | Required behavior |
|---|---|---|
| `pending` / `running` | none | Never imply a derivative exists. |
| `completed` | none | Return exact immutable provenance and private derivative locator. |
| `unavailable` | `source_unavailable`, `selected_version_unavailable`, `version_resolution_unavailable`, `incompatible_renderer` | No substitute derivative. |
| `failed` / `retryable` | `malformed_xmp`, `unsupported_source`, `resource_limit`, `renderer_timeout`, `output_invalid`, `storage_unavailable` | Return safe text only; never paths, credentials, or embedded metadata. |

## Provenance response

```json
{
  "sourceSha256": "64 lowercase hex characters",
  "xmpSha256": "64 lowercase hex characters",
  "selectedVersion": "current or immutable version identity",
  "rendererRelease": "5.6.1",
  "rendererArtifactDigest": "sha256:…",
  "profile": "render-v1",
  "compatibilityMatrix": "rendering-v1",
  "completedAt": "RFC 3339 UTC timestamp"
}
```

The owner, library identity, and every provenance field form the cache key input. Consumers must display provenance text alongside a completed rendition and must not infer a missing field.
