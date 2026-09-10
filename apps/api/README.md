# Platform API validation

The API is built with .NET SDK analyzers enabled and warnings treated as errors.

From the repository root:

- `npm run format` checks backend formatting.
- `npm run lint` runs static analysis.
- `npm run test` runs the API foundation unit harness and release-tool tests.
- `npm run test:contract` validates shared v1 contracts.

Feature 005 exposes owner-scoped batch/part/complete/status, exact original/edit retrieval, path move, recoverable delete/restore, and reconstruction-manifest routes under `/v1`. Mutations require a durable `Idempotency-Key`; existing-resource changes also require the current lifecycle version in `If-Match`. Safe problem responses carry stable codes and operation IDs.

Run `npm run test:ingest` with the pinned local PostgreSQL/MinIO services for the real storage/HTTP/fault suite. See [ingest operations](../../docs/runbooks/ingest-storage.md) and [manifest verification](../../docs/runbooks/reconstruction-manifest.md).
