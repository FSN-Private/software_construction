# Platform API validation

The API is built with .NET SDK analyzers enabled and warnings treated as errors.

From the repository root:

- `npm run format` checks backend formatting.
- `npm run lint` runs static analysis.
- `npm run test` runs the API foundation unit harness and release-tool tests.
- `npm run test:contract` validates shared v1 contracts.

Feature 005 exposes owner-scoped batch/part/complete/status, exact original/edit retrieval, path move, recoverable delete/restore, and reconstruction-manifest routes under `/v1`. Mutations require a durable `Idempotency-Key`; existing-resource changes also require the current lifecycle version in `If-Match`. Safe problem responses carry stable codes and operation IDs.

Run `npm run test:ingest` with the pinned local PostgreSQL/MinIO services for the real storage/HTTP/fault suite. See [ingest operations](../../docs/runbooks/ingest-storage.md) and [manifest verification](../../docs/runbooks/reconstruction-manifest.md).

## Export baskets (feature 012)

The additive export schema and owner-scoped basket routes are available behind `Exports:Enabled` (disabled by default). `/v1/libraries/{library}/exports/baskets` supports saved/default baskets, revision-checked mutations and exact committed-version selections. Working selections require a committed version; hidden selections require advanced history. Data scope is checked at every basket/selection access, and duplicate items remain explicit. See `docs/features/012-versioned-export-basket/contracts/` and `docs/operations/export-basket.md`. Preparation and dedicated download transport are subsequent implementation slices; production enablement requires the feature 011 integration gate.
