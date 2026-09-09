# Storage

Feature 005 owns immutable content, path, lifecycle, and reconstruction primitives here.
# Feature 005 transfer policy

Storage accepts 1-byte through 1-GiB objects, with resumable 8-MiB parts (the last may be smaller). Each API part is bound to owner/session, expected length, and SHA-256. Retries reuse persisted part receipts; completion records a versioned staging object. Authoritative writes verify the complete stream and use conditional create, SSE-KMS, and an immutable SHA-256 key. Originals and XMP are never overwritten.

Staging uploads expire after 24 hours; quarantine has a 30-day correction window. Ordinary asset deletion preserves bytes for the approved 90-day recovery period and never invokes physical deletion. See [validation](../../docs/features/005-image-ingest-storage/validation.md).
