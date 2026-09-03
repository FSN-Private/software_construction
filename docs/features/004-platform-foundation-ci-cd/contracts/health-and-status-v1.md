# Health and Status Contract v1

| Method/path | Authentication | Success | Failure |
|---|---|---|---|
| `GET /live` | none | `200 { "status":"live", "contractVersion":"v1" }` | `503` only when process cannot serve |
| `GET /ready` | owner | `200 { "status":"ready", "operationId":"…", "releaseId":"…" }` | generic `401/403`; detail-free `503` |
| `GET /v1/session` | owner | `200 { "subject":"opaque-id", "role":"owner", "operationId":"…" }` | generic `401/403` |
| `GET /v1/version` | owner | `200 { "releaseId":"…", "sourceRevision":"…", "manifestDigest":"sha256:…", "operationId":"…" }` | generic `401/403` |

Errors use `application/problem+json` with `type`, `title`, `status`, `code`, and `operationId`; never include credentials, account/infrastructure identifiers, configuration, user metadata, or dependency diagnostics. Clients continue an operation through `X-Operation-Id`; otherwise the service creates and returns one.
