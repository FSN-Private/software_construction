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

The additive export schema and owner-scoped routes are available behind `Exports:Enabled` (disabled by default). `/v1/libraries/{library}/exports/baskets` supports saved/default baskets, revision-checked mutations and exact committed-version selections. Working selections require a committed version; hidden selections require advanced history. Data scope is checked at every basket/selection access, and duplicate items remain explicit.

Submission captures immutable selections, preset, renderer compatibility and consent using a durable `Idempotency-Key`. The background worker prepares verified originals or pinned rendered JPEGs, with per-item outcomes and a mandatory manifest. Completed packages expire after 24 hours. Cancellation and permanent-unavailability fences revoke download access before physical cleanup; unknown storage writes remain draining until settlement is proven.

Native downloads use a dedicated HTTPS origin configured by `Exports:DownloadOrigin` and `Exports:WebOrigin`: an owner-authenticated grant creates a one-use bootstrap POST, then a protected path-scoped cookie authorizes bounded range streaming of the exact object version. Session revocation remains available when the capability is disabled. Configure limits using `Exports:Items`, `Baskets`, `Requests`, `PackageBytes`, `PreparationSeconds` and `RetentionHours` (24). See [API contracts](../../docs/features/012-versioned-export-basket/contracts/export-api.md), [operations](../../docs/operations/export-basket.md) and [validation](../../docs/features/012-versioned-export-basket/validation.md). Production enablement requires the feature 011 publisher, verifiable abandoned-writer recovery and deployed transport acceptance evidence.
