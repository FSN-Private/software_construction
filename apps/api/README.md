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

## Backup health and recovery identity (feature 011)

The owner health route is `GET /v1/recovery/health`. It is private and `no-store`; owner identity comes from the authenticated token and, in a recovery-only deployment, a server-side mapping from the recovery issuer/audience/subject to the preserved logical owner ID. The client cannot supply an owner ID. Provision recovery identity mappings only with the separate MFA approval key and its independently pinned public key; the automated backup evidence signer is not an identity approver.

`GET /v1/recovery/points` lists at most 100 independently signed point summaries; use its `nextAfterPointId` as the next request's `afterPointId`. `POST /v1/recovery/sessions` requires recent owner authentication and `Idempotency-Key`, with body `{ "recoveryPointId": "…", "scope": { "kind": "library" }, "reason": "…" }` (asset/edit scopes include their IDs). It returns `202` and an isolated `requested` session. Identical retries preserve the original session and expiry; changed payloads return `409`. `GET /v1/recovery/sessions/{sessionId}` returns only the owning principal's safe status. These routes are private/no-store. The independent retrieval, validation and promotion executors remain under implementation; a request does not expose recovered bytes or authorize live writes.

Enable the API health mirror with `Recovery__Enabled=true` and the deployment's canonical `Recovery__LibraryId`. Configure the independent evidence trust with `Recovery__ObservationTrust__KeyId`, `Recovery__ObservationTrust__PublicKeySpkiBase64`, and `Recovery__ObservationTrust__PublicKeySha256`. In the primary API, set `Recovery__ObservationQueueUrl` for the bounded signed catalog, observation, and failure-cycle queue. A recovery-only API additionally requires the out-of-band `Recovery__OwnerApprovalTrust__KeyId`, `Recovery__OwnerApprovalTrust__PublicKeySpkiBase64`, and `Recovery__OwnerApprovalTrust__PublicKeySha256`, plus pre-approved server-side identity mappings. Missing trust, database state, or required configuration returns a safe `503 recovery_unavailable`; foreign or unmapped authenticated identities are denied. The API re-verifies signed catalog and observation bytes when serving status. Primary health storage contains aggregate class counts and digests only, not object paths or versions.

Status reports seven canonical classes: originals, XMP, version graph, metadata, audit, configuration, and recovery records. Each reports `verified`, `overdue`, `incomplete`, `integrity_failed`, or `unavailable`, with safe exception codes, a UTC observation time, optional coverage and recovery-point age, and the last recorded delivered alert. Queue acceptance alone does not count as alert delivery. External role/secret provisioning, independent-account key policy, and actual regional recovery qualification remain operational prerequisites; see [backup and recovery operations](../../docs/operations/backup-recovery.md) and the [regional recovery runbook](../../docs/runbooks/library-recovery.md).
