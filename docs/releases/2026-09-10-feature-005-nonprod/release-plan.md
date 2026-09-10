# Feature 005 non-production release plan

**Owner**: platform maintainer  
**Intended revision**: the merge commit produced from `feature/005-image-ingest-storage` after CI and pre-push review  
**Environment**: AWS non-production  
**Included feature**: [Image ingest and durable storage](../../features/005-image-ingest-storage/spec.md)

## Prerequisites

- The GitHub `nonprod` environment contains every variable and secret listed in the [deployment runbook](../../../docs/runbooks/deployment-recovery.md#required-configuration). As of 2026-09-10, only `AWS_NONPROD_ROLE_ARN` is configured; the remaining values are an external bootstrap blocker.
- The named ECR repositories, ECS cluster, API service, worker service, task definitions, PostgreSQL database, private versioned S3 bucket, KMS key, Cognito owner, HTTPS endpoint, task roles, and deployment-role grants exist in the approved account and region.
- API and worker task definitions supply `ConnectionStrings__Ingest`, `Ingest__Bucket`, and `Ingest__KmsKeyId`; the database connection string comes from the task secret provider.
- The intended revision has passing `ci / required`, passing alternate-model pre-push review, and successful five-artifact release evidence.

## Rollout and observable results

1. Build release evidence. Expect checksummed `api.tar`, `ingest-worker.tar`, web, infrastructure, and Windows artifacts plus provenance and SBOM evidence for one source SHA.
2. Publish API and worker images to their named ECR repositories. Expect immutable registry digests in `nonprod-deployment.json`.
3. Register candidate API and worker task definitions. Expect both to retain required secret/config bindings and name the new image digests.
4. Run the worker with `--migrate` using its service network. Expect exit code 0 and two checksum records in `platform_schema_migrations`; any error aborts before service changes.
5. Update worker and API services and wait for stability. Expect both services to settle on the candidate task definitions.
6. Require public `/live`, owner-authenticated `/ready`, and the one-item Feature 005 smoke test. Expect byte-identical download, recoverable deletion/restoration, and reconstruction-manifest export; retain IDs and SHA-256 in deployment evidence.
7. Observe ingest storage-failure, repair, quarantine, integrity-drift, batch-age/rate, verification-time, and verified-byte signals for 15 minutes. Success means no integrity drift, no sustained storage failure, and no stuck smoke batch.

## Abort and rollback

Abort on evidence mismatch, missing configuration, migration error, absent bucket versioning/KMS authority, ECS instability, readiness failure, smoke mismatch, any integrity drift, or sustained storage failure. Before service updates, no rollback is needed. After service updates, the workflow restores both captured prior task definitions and waits for stability. The additive migration remains applied; do not drop its schema or delete S3 versions. Repair any queued publication with the restored or corrected worker before retrying.

The platform maintainer owns the decision to retry after examining the named workflow run, ECS stopped-task reason, CloudWatch signals, and retained deployment evidence. Production promotion remains prohibited until this non-production plan succeeds and its observation window is recorded.
