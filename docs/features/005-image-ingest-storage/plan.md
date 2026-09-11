# Implementation Plan: Image Ingest and Durable Storage

**Branch**: `feature/005-image-ingest-storage` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)
**Status**: Approved — owner authorization recorded 2026-09-07

## Summary

Build the storage contract that later features consume: owner-scoped resumable upload sessions, SHA-256 content-addressed immutable S3 objects, PostgreSQL logical assets and associations, idempotent publication/reconciliation, private quarantine, path history, 90-day soft deletion, and checksummed reconstruction manifests. This feature owns original/XMP bytes and their integrity/lifecycle; it neither commits revisions nor renders, syncs, searches, or permanently purges them.

The durable ownership and publication model is recorded in proposed [ADR-0005](../../architecture/decisions/0005-immutable-content-and-repairable-publication.md). It refines the program's S3/PostgreSQL decision and consumes feature 004's identity, error, telemetry, audit, and delivery contracts.

## Technical Context

| Area | Decision |
|---|---|
| Runtime | .NET 10/C# 14 modular-monolith API and asynchronous ingest worker |
| Persistence | PostgreSQL 18 transactional metadata; S3 Standard, versioning, SSE-KMS, block-public-access, and checksum validation |
| Transfer | owner-authorized multipart upload with server-issued opaque session and idempotency key; clients never receive arbitrary bucket authority |
| Identity | UUID logical asset/edit IDs; SHA-256 + byte length identifies immutable content; paths are mutable records |
| Atomicity | database outbox/reconciliation state makes object/database publication repairable; caller-selected atomic batches use a prepared set and one transaction |
| Compatibility | corpus-verified RAW formats plus JPEG, PNG, TIFF; darktable base and numbered-duplicate XMP sidecars only |
| Scale | 100,000 active assets and 1,000,000 content/XMP records; 1,000-item progress visible <2s and p95 verification <60s after transfer |

## Constitution Check

- **PASS — scope**: image and XMP ingest/lifecycle only; all downstream version, sync, rendering, gallery, and permanent-purge behavior remains separate.
- **PASS — branch**: `feature/005-image-ingest-storage` diverges from `master`; no history is reconciled.
- **PASS — integrity/security**: immutable checksummed bytes, short-lived owner-scoped upload/download authority, private quarantine, redaction, audit, and retry-safe operations are first-class.
- **PASS — recovery**: reconstruction manifests and non-destructive reconciliation support feature 011; ordinary deletion does not erase authority.
- **GATE**: S3/RDS/KMS creation, data migration, deployment, and any external mutation remain unauthorized.

## Design

### Components and data flow

```text
web/API → UploadSession → private staging object
                              ↓ verified SHA-256 / format / XMP parser
                         ingest worker → association + collision decision
                              ↓
PostgreSQL metadata ← outbox/reconciliation → immutable authoritative S3 object
                              ↓
                    active asset / quarantine / failed / recoverable-deleted
```

The API writes an upload session and idempotency record before authorizing a constrained transfer. Completion verifies received length and SHA-256 from streamed bytes before assigning a deterministic content key (`sha256/<digest>`). An existing authoritative object with the same checksum is reused but never merges logical assets, paths, edit identities, or audit records. The worker detects supported format, well-formed XMP, naming convention, candidate association, collision, and quota state. It records a durable outcome for every item.

Authoritative object creation and PostgreSQL publication cannot be one distributed transaction. The database records a `pending-publication` intent/outbox before S3 promotion. A worker completes the immutable object write and then atomically transitions the metadata set to active; reconciliation retries or safely removes unreferenced staging data. A request is acknowledged only after the object and active metadata are mutually reachable. Atomic batches prepare every item first and publish all selected database records in one transaction, or none; pre-existing deduplicated objects may remain harmlessly reusable.

### Data model and lifecycle

See [data-model.md](data-model.md). Content objects are immutable. Asset and edit identity are stable; a normalized Windows-safe collision key and display path are separate path records. XMP bytes are stored untouched after validation; parsing only informs association/compatibility. Base `<image>.xmp` and numbered `<image>_NN.xmp` become separate edit identities for one source asset. Any zero/multiple candidate, unsupported naming, malformed XML, or unsafe state stays private and non-authoritative in quarantine.

`uploading → verifying → pending-publication → active` is the successful route. Failure is explicit and retryable; unsafe inputs use `quarantined`; deletion is `recoverable-deleted` with a 90-day deadline and lifecycle hold checks; restoration returns the original asset/association/path history, requiring an explicit alternate path on collision. Physical purge is deferred to feature 011.

### Interfaces, validation, and downstream handoff

The versioned ingest contract is [ingest-storage-v1.md](contracts/ingest-storage-v1.md). All state-changing requests require an idempotency key and expected lifecycle version. API errors use the feature-004 problem contract and return safe correction codes. Downloads are authorized per immutable object, record checksum/provenance, and stream exact bytes.

Every lifecycle decision emits an audit event without path, media, XMP, credential, or geolocation payload. Metrics cover batch age/rate, verified bytes, quarantine, orphan repair, integrity drift, and storage errors; a sustained fault alerts within 15 minutes. Reconstruction manifests are canonical, versioned, checksummed inventories of all authoritative relationships and handoff IDs, enabling feature 011 to detect missing, altered, extra, or mis-associated samples.

### Security, migration, operations, and cost

Use least-privilege service roles; private S3 paths; TLS; SSE-KMS; short-lived scoped URLs/tokens; content-length and expected-checksum constraints; and no user-selected object key. Lock and review every parser, imaging detector, SDK, and worker dependency; require lockfiles, SBOM/provenance through feature 004, license/advisory review, and corpus fixtures before enabling a new format. Database changes are expand/migrate/contract; no migration reinterprets bytes or existing identity silently. Runbooks cover stuck uploads, quarantine review, orphan repair, checksum drift, quota, outage, and manifest verification. Staging and quarantine receive explicit expiry; inventory reports bytes/object counts and reusable duplicate content.

## Alternatives and post-design check

Database blobs, mutable file paths as identity, client-provided storage credentials, best-effort dual writes, and immediate physical deletion are rejected: each weakens streaming, least privilege, recoverability, or correctness. Exact multipart limits, retention days for non-authoritative staging/quarantine, and corpus membership are implementation policy values; they must be documented and tested before enabling production and do not change the approved behavior.

**Post-design Constitution Check: PASS.** No material product ambiguity remains; external account, bucket, KMS, and budget inputs are deployment prerequisites, not implementation authority.

## Implementation policy and CI contract

The bounded transfer implementation proxies owner-authenticated 8-MiB parts to S3 multipart uploads. This avoids exposing storage keys or transferable bucket authority. Sessions last 24 hours; individual objects are limited to 1 GiB and batches to 1,000 items. Parts are resumable without changing the lifecycle version; completion advances the version. The worker streams the entire version-pinned object through SHA-256 and the corpus-backed inspector before conditional authoritative publication. `ingest_publications` is the durable batch outbox; per-item savepoints allow independent outcomes, while an atomic set rolls back every metadata publication on any invalid input. Immutable objects left by a rollback are reused on retry and stay traceable through upload sessions.

The existing `ci / backend` check on pull requests and pushes to `master` now includes the PostgreSQL/MinIO integration suite using the same pinned Compose services as local validation, alongside format, build, unit, contract, and packaging checks. The existing web, Windows, infrastructure, security, and aggregate required check names remain stable. No new permissions, cloud credentials, or actions are introduced. The backend job retains its 10-minute timeout; the maintainer owns failures and retries only after diagnosing them. Capacity measurements are an explicit local/release validation command, stored in this feature's validation record; test-generated data is disposable and is not uploaded as a CI artifact. Workflow syntax was checked against the [official reference](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax).

The non-production runtime follows accepted ADR-0003: retained encrypted S3 and single-AZ PostgreSQL back API and worker Fargate services in private subnets. An internal application load balancer is exposed through an API Gateway HTTP API, providing a managed HTTPS endpoint without adding a DNS or certificate prerequisite. ECS begins both services at zero tasks against immutable placeholder tags; the first verified release publishes the real images, migrates the database, raises both desired counts to one, and restores the original counts and task definitions on failure. RDS credentials enter each task as separate Secrets Manager fields and are assembled into a TLS-required Npgsql connection string only in process memory.

Storage readers always validate bytes before returning a download. Logical move/delete/restore transitions and their audit/response receipts share a PostgreSQL transaction and owner lock. Restore is inclusive at the stored microsecond-precision day-90 deadline. A deleted image gates access to all its edits while retaining their identities and independent deletion state. Recovery verification uses a separately retained trusted manifest, a reconstructed manifest, and hashes computed from restored object files.
