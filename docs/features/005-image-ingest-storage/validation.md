# Feature 005 validation

Validation is local; no deployment, cloud migration, push, or merge is authorized by feature implementation.

## Integrity foundation

- 2026-09-09: clean starting worktree; `feature/005-image-ingest-storage` was 31 commits ahead / 0 behind the local `origin/master` reference (51 / 0 against local `master`). No history reconciliation performed.
- Existing .NET foundation checks pass. New code builds without warnings.
- PostgreSQL 18 / pinned MinIO integration imports the six-file licensed corpus, retries every part and completion, publishes one atomic set, and verifies every downloaded SHA-256. Real SSE-KMS and versioned objects are exercised with a local test-only key.
- Multipart SHA-256 is independently streamed over the complete bytes; S3 multipart composite checksums are not treated as whole-file digests. See [AWS checksum semantics](https://docs.aws.amazon.com/AmazonS3/latest/userguide/checking-object-integrity-upload.html).

## Running validation

Start the isolated services with `docker compose -f tests/integration/ingest/compose.yml -p feature005 up -d`. Test ports bind only to localhost. The public, fixed credentials/key are test fixtures and must never be used for deployed data.

Run `$env:INGEST_INTEGRATION='1'; dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj`. Each run creates a uniquely named schema and bucket so it cannot change pre-existing data. Without the environment variable, the test executable runs the offline foundation/corpus checks.

Further acceptance, fault, capacity, and rollout evidence is recorded below as checks complete.

## Acceptance and fault evidence

- Mixed corpus: DNG, JPEG, PNG, TIFF, base XMP, and numbered XMP publish with distinct asset/edit identity and exact downloaded hashes. Repeated create/part/complete operations do not duplicate logical records or content versions.
- Unsafe input: malformed, unsupported/unassociated, incorrect declared SHA-256, ambiguous/colliding, and atomic-batch failure fixtures remain non-active with stable corrective codes and private 30-day quarantine records.
- RAW truncation: TIFF/DNG inspection follows nested image-file directories and validates every declared strip, tile, and embedded-JPEG offset/byte-count pair. The 29,195,592-byte DNG corpus fixture truncated to 4,096 bytes is rejected before publication; the complete DNG/TIFF corpus remains accepted.
- Short-header truncation: all PNG chunk and JPEG segment structural reads require the declared number of bytes. Offline inspection and the PostgreSQL/MinIO worker quarantine a 9-byte PNG and four-byte JPEG as `unsupported_format` rather than classifying them as retryable storage failures.
- Owner correction: the executable workflow reads the stable `ambiguous_association` result, retries the sidecar with a deterministic source name, and reaches active state in 133.1 ms. A restore collision followed by an explicit alternate path completes in 21.2 ms; both are below SC-004's two-minute target.
- Publication fault: a synthetic unavailable bucket after object creation leaves the session `verifying`; a later worker pass reuses the immutable version and completes metadata. No acknowledged orphan is produced.
- HTTP contract: anonymous and other-owner requests fail closed; mutation idempotency fingerprints reject key reuse; lifecycle versions reject stale changes; transfer descriptors omit object keys and credentials.
- Retrieval/path: source/storage/download hashes match. Unicode, Windows-invalid, case-insensitive collision, stale-version, and 10,000-move checks preserve stable assets, edit associations, and complete path history.
- Lifecycle: ordinary delete hides active access and does not delete shared content. Restore succeeds at the exact stored day-90 deadline, fails after it, preserves edit/audit identity, and requires an explicit alternate path on collision. Active holds block deletion.
- Reconstruction: canonical export detects missing/altered/extra bytes plus missing or altered asset/edit association, path history, hold, audit resource link, provenance, and handoff identifiers. Other owners cannot read a manifest.

## Performance, scale, and cost evidence

The local reference workload used PostgreSQL 18.1 and versioned SSE-KMS MinIO on the developer workstation. Transfer time is excluded from verification timing as specified. Evidence JSON is generated under ignored `artifacts/` by `INGEST_REFERENCE=1` and `INGEST_CAPACITY=1` runs.

| Check | Result | Target |
|---|---:|---:|
| 1,000-item batch create plus first status | 170.7 ms | < 2 s |
| 1,000-item verification completion p95 | 28.70 s | < 60 s |
| Entire 1,000-item verification set | 30.52 s | deterministic outcomes |
| Indexed list plus exact verified retrieval at capacity, p95 | 22.80 ms | no target violation |
| Active assets / edit records | 100,000 / 1,000,000 | model capacity |
| Moves with identity/association preserved | 10,000 | 10,000 |
| Reference payload storage | 299 bytes / 1 unique object for 1,000 logical assets | deduplicated accounting |

The capacity seed took 75.45 s and 10,000 serialized owner-safe moves took 260.85 s. These are validation setup/throughput observations, not interactive latency requirements. The owner lock is a deliberate simple ceiling; partition by asset only if measured multi-operation owner throughput requires it.

## Migration, rollout, and rollback rehearsal

The integration harness applies the original feature migration, inserts an old-shape asset, executes the workflow expansion inside a transaction and rolls it back, then applies it and proves the original ID/state/nullable content fields remain readable while new handoff metadata is populated. The rehearsal passes without reinterpreting content or identity.

Roll out in this order: apply `005_ingest_storage.sql` where absent; apply `005_ingest_workflows.sql` transactionally; configure the private versioned bucket, KMS key, and least-privilege API/worker roles; deploy API readers/writers; deploy the worker; then enable owner UI access. Before traffic, require authenticated `/ready`, a one-item corpus import/download, a soft-delete/restore exercise, and a manifest export. Monitor storage failures, repairs, quarantine, integrity drift, batch age/rate, verification time, and verified bytes for 15 minutes.

Abort on migration error, missing versioning/KMS authority, any integrity drift, sustained storage failure, or smoke-test mismatch. Because this is additive, application rollback may restore the prior API/worker while leaving new tables/columns intact. Do not drop schema, delete object versions, reverse published lifecycle state, or claim persistent-state rollback. Repair queued publication using the new worker before retrying rollout. External migration/deployment was not performed; it requires the separately approved release procedure and environment authority.

## Release preparation — 2026-09-10

The failed Windows release build was reproduced and fixed at its source: `packages/storage/Platform.Storage.csproj` now declares the supported `win-x64` runtime and its lockfile retains that target across both the locked Windows package command and an ordinary backend restore. `npm run package:windows` and the immediately following `npm run test:backend` passed without changing the lockfile.

The release now contains a locked non-root ingest-worker image, a transactional `--migrate` command with embedded migration SHA-256 records, coordinated API/worker ECS rollout and rollback, and an authenticated one-item import/download/delete/restore/manifest smoke. `npm run test:backend`, `npm run test:release-evidence`, `npm run test:package-definitions`, `npm run test:ingest-smoke-script`, API/worker builds, worker publish, workflow YAML parsing, deployment-shell syntax checks, `git diff --check`, and `npm run validate:security` passed. The local Docker daemon and PostgreSQL/MinIO services were unavailable; the exact worker-container build and migration integration therefore remain required checks in remote CI.

No AWS mutation occurred. GitHub configuration-name inspection found only `AWS_NONPROD_ROLE_ARN` in the `nonprod` environment and no non-production owner smoke secrets. The missing account resources and protected values are listed in the [release plan](../../releases/2026-09-10-feature-005-nonprod/release-plan.md) and block T035 until separately authorized bootstrap and deployment.

On 2026-09-11, T035 repository preparation added the missing non-production runtime definitions: paired immutable ECR repositories, zero-count API/worker ECS services and task definitions, retained versioned SSE-KMS ingest storage, retained deletion-protected PostgreSQL 18.4, secret-backed database credentials, Cloud Map service discovery, an HTTPS API Gateway endpoint, deployment-role grants, and machine-readable GitHub configuration outputs. Fixed-cost NAT gateways and load balancers are absent; public-address Fargate tasks deny direct inbound access, while the API permits port 8080 only from the VPC-link security group. The deployment now reports every missing protected value before OIDC, safely reuses only byte-identical immutable image tags, and restores pre-deployment service counts on first-rollout failure. API/worker database configuration is assembled in memory from the secret fields and requires TLS. `npm run test:backend`, the complete infrastructure validation including template assertions and dependency audit, release-evidence contracts, reliability contracts, CI contracts, deployment Bash parsing, PowerShell parsing, and `git diff --check` pass locally. The repository-wide format command reaches unchanged web files with the existing Windows CRLF/Prettier mismatch; .NET formatting passes and no web file is modified. AWS account diff/bootstrap, account-specific cost acceptance, protected GitHub configuration, deployment, smoke, and the observation window remain unexecuted external gates.

The first independent release follow-up review found that production handled only the API and that non-production rollback began too late to cover ambiguous service-update failures. Both findings were accepted. Non-production and production now use one paired deployment script whose rollback trap is active before the first ECS service mutation; production promotion verifies worker/migration/smoke evidence and standalone rollback restores the exact API/worker pair. The smoke also proves deleted access denial, restored checksum equality, and reconstruction-manifest membership without relying on the first page of asset listings. The expanded release-evidence, release-workflow, reliability, package-definition, smoke syntax, YAML parsing, deployment-shell parsing, and API/worker build checks pass.

The second independent review accepted the paired production path, early rollback trap, and strengthened smoke, then found that ECS circuit-breaker reversion could still be mistaken for a successful deployment or rollback. It also found that per-operation timeouts did not reserve runner time for recovery. Both findings were accepted. Deployment and rollback now re-read both ECS services after stability and fail unless their task-definition ARNs exactly match the requested pair. A mocked prior-task response proves that failure path. The delivery workflows also terminate the inner deployment or rollback attempt before the longer job deadline, preserving at least 20 minutes for paired recovery. Independent final review of this revised range remains required before the pre-push gate can pass.

Post-merge release run `34477947247` built every Linux and Windows artifact successfully but failed when GitHub rejected hosted artifact attestation for the private organization's current plan. T040 removes that unavailable API dependency and its `attestations: write`/OIDC permissions. The retained provenance statement uses the documented project-owned build type and is structurally bound to all artifact digests, the exact source SHA, triggering CI run, trusted repository, builder workflow, and release workflow invocation. Deployment verification receives repository/run identity from GitHub context and rejects recomputed-checksum build-type, repository, source, and run substitutions. Local release-evidence and workflow contracts pass; a new protected-default run must confirm the repair remotely.
