# Ingest storage operations

Owner: platform maintainer. Scope: feature 005 import, private staging/quarantine, publication repair, integrity, quota, and object-storage availability. Never include media bytes, XMP, logical paths, credentials, presigned data, or geolocation in tickets or logs. Use opaque batch/session/asset IDs and operation IDs.

## Signals and first response

`/live` only proves the API process is running. Authenticated `/ready` must prove PostgreSQL is reachable within two seconds and the configured private S3 bucket has versioning enabled. Treat three consecutive five-minute `StorageFailures` periods as the 15-minute ingest outage alert. Any `IntegrityDrift` event alerts immediately. `Quarantined` and `Repairs` appear on the ingest dashboard for workload and repair trend review.

On an alert, preserve the operation ID and release ID, check authenticated `/ready`, confirm recent `audit_events` and `ingest_publications` state, then inspect the named S3 object version using least-privilege operator tooling. Do not download content unless the owner explicitly starts a private quarantine or integrity investigation.

## Stuck or interrupted upload

1. Find the owner-scoped session by its opaque ID. Confirm `state`, `expires_at`, `lifecycle_version`, and the count/length of `received_parts`; do not select `logical_path` into logs.
2. If the session is unexpired and `uploading`, ask the owner to reselect the exact source files and use Resume. Already recorded parts are reused.
3. If it expired, the worker changes it to `failed/upload_expired`, records an audit event, and prevents later completion. Start a new batch; never extend the old session by editing the database.
4. If it is `verifying`, check `ingest_publications.last_error` and storage readiness. The worker retries dependency failures after five seconds. Do not create an asset manually.

Escalate after 15 minutes of dependency failure or three failed repair attempts. Record the batch/session/operation IDs, current state, release, dependency status, and safe error code.

## Quarantine review

Quarantine is private and expires 30 days after the failed verification record. Stable reasons include `checksum_mismatch`, `unsupported_format`, `malformed_xmp`, `unassociated_xmp`, `ambiguous_association`, `path_collision`, `edit_collision`, and `atomic_batch_failed`.

Verify the reason against an owner-supplied source copy in an isolated private workspace. For path or association corrections, import again with a supported unique path. For byte/format errors, retain the source until a corrected import becomes active. Never mark a quarantined object authoritative, rewrite its checksum, or attach an XMP by database edit. Treat repeated unexpected checksum failures as possible integrity drift.

## Publication repair and object orphans

`ingest_publications` is the durable batch outbox. An object write can succeed before its PostgreSQL transaction; conditional SHA-256 publication makes that object safely reusable. Restore PostgreSQL or S3 availability and let the worker retry the same verifying session. A batch is acknowledged only after active metadata and immutable content are mutually reachable.

For reconciliation, compare authoritative `content_objects.storage_key/storage_version` with an S3 inventory and the reconstruction manifest. A `sha256/` key absent from all content records is an unacknowledged orphan. Preserve it and its versions for recovery review; feature 011 owns physical purge. Never delete an orphan during incident response.

## Checksum drift

An exact-byte download rehashes the version-pinned object. On mismatch or missing data, the transaction changes the content and referencing active records to `integrity-error`, writes `integrity.drift`, and denies download.

Immediately stop promotion of the affected release, preserve the object versions and audit chain, export a fresh manifest, and compare it with the latest separately retained trusted manifest. Determine whether the object, metadata, path, association, hold, audit link, or handoff ID changed. Recovery from a trusted copy belongs to feature 011; do not overwrite the damaged version or clear `integrity-error` manually.

## Quota and cost

Admission counts active, pending, unexpired staging, and unexpired quarantine bytes against the configured owner quota. `quota_exceeded` is not retryable until retained temporary data expires or capacity is deliberately raised. Batch status reports deduplicated authoritative byte and object counts. Review unexpected growth using database totals and S3 inventory without listing owner paths. A single content checksum may support multiple logical assets and must be charged once for stored payload while retaining separate audit/lifecycle records.

## Storage outage

If `/ready` reports `ingest_unavailable`, check PostgreSQL connectivity, S3 bucket access, bucket versioning, KMS authorization, and service quota. Keep retries on the original idempotency key. Never switch to an unversioned/public bucket, disable SSE-KMS, or acknowledge staging as authoritative. Abort a rollout if authenticated readiness remains unavailable for five minutes; follow [deployment recovery](deployment-recovery.md) for application rollback. Persistent-state rollback is expand-only and follows the feature validation record.

Close the incident only after `/ready` succeeds, queued verifying batches reach a durable terminal state, no new storage failures occur for 15 minutes, and a sampled checksum download and manifest comparison pass.
