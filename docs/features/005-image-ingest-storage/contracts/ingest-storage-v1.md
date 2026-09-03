# Ingest and Storage Contract v1

All owner-scoped mutating calls require `Idempotency-Key` and current lifecycle version when changing an existing resource. Responses include opaque IDs, lifecycle state, safe error code, and operation ID; they never expose S3 keys, credentials, or other-owner objects.

| Operation | Result |
|---|---|
| `POST /v1/import-batches` | creates a batch with `atomic` choice and per-item upload-session descriptors |
| `POST /v1/upload-sessions/{id}/complete` | verifies declared length/SHA-256 and returns original result on duplicate completion |
| `GET /v1/import-batches/{id}` | item progress/outcome: uploading, verifying, active, quarantined, failed, or integrity-error |
| `GET /v1/assets/{id}/content` | exact original/XMP byte stream with content digest and provenance |
| `PATCH /v1/assets/{id}/path` | move/rename with expected version; rejects collision/Windows-invalid path |
| `DELETE /v1/assets/{id}` / `POST /v1/assets/{id}/restore` | 90-day recoverable deletion/restoration; returns hold or destination-conflict state |
| `GET /v1/reconstruction-manifests/{id}` | versioned checksummed authority inventory for recovery verification |

Stable errors include `checksum_mismatch`, `unsupported_format`, `malformed_xmp`, `ambiguous_association`, `path_collision`, `invalid_windows_path`, `quota_exceeded`, `lifecycle_conflict`, `retention_hold`, `not_authoritative`, and `storage_retryable`.
