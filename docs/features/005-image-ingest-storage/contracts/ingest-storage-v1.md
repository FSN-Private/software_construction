# Ingest and Storage Contract v1

All owner-scoped mutating calls require `Idempotency-Key` and current lifecycle version when changing an existing resource. Responses include opaque IDs, lifecycle state, safe error code, and operation ID; they never expose S3 keys, credentials, or other-owner objects.

| Operation | Result |
|---|---|
| `POST /v1/import-batches` | creates a batch with `atomic` choice and per-item upload-session descriptors |
| `POST /v1/upload-sessions/{id}/transfer` | issues a 15-minute owner/session/version-bound API transfer descriptor |
| `PUT /v1/upload-sessions/{id}/parts/{number}` | accepts one length/SHA-256-bound 8-MiB part (smaller final part allowed); exact retry reuses its receipt |
| `DELETE /v1/upload-sessions/{id}` | cancels an unfinished session and prevents later completion |
| `POST /v1/upload-sessions/{id}/complete` | closes the transfer for asynchronous whole-stream verification and returns the original durable result on duplicate completion |
| `GET /v1/import-batches/{id}` | item progress/outcome: uploading, verifying, active, quarantined, failed, or integrity-error |
| `GET /v1/assets` / `GET /v1/assets/{id}/edits` | owner-scoped active, integrity-error, or recoverable records and stable edit identities |
| `GET /v1/assets/{id}/content` / `GET /v1/edits/{id}/content` | exact original/XMP byte stream with content digest and provenance |
| `PATCH /v1/assets/{id}/path` | move/rename with expected version; rejects collision/Windows-invalid path |
| `DELETE /v1/assets/{id}` / `POST /v1/assets/{id}/restore` | 90-day recoverable deletion/restoration; returns hold or destination-conflict state |
| `GET /v1/reconstruction-manifests/{id}` | versioned checksummed authority inventory for recovery verification |

Stable errors include `checksum_mismatch`, `unsupported_format`, `malformed_xmp`, `unassociated_xmp`, `ambiguous_association`, `edit_collision`, `path_collision`, `invalid_windows_path`, `quota_exceeded`, `upload_incomplete`, `upload_expired`, `transfer_authorization_required`, `atomic_batch_failed`, `lifecycle_conflict`, `retention_hold`, `restore_expired`, `not_authoritative`, `integrity_error`, and `storage_retryable`.
