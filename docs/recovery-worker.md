# Recovery worker operations

The recovery worker exposes `capture`, `copy`, `verify`, `drill`, `bootstrap-index`, and an offline-only `verify-qualification` mode. AWS-backed modes use the task role's default credentials and fail closed when required account, region, trust-pin, digest, database TLS, or exact-version configuration is absent. The offline qualification verifier does not load AWS configuration.

## Point-index bootstrap

Run `bootstrap-index` as a one-time operator task before enabling copy schedules, and only with its dedicated recovery-account role. This role needs `sts:GetCallerIdentity` and `dynamodb:Scan`, `GetItem`, `Query`, `PutItem`, and `UpdateItem` on the recovery ControlTable. It must not have primary-read, S3 payload/evidence, KMS signing, secret, fence-table, or intent-table access. The scheduled copy role must not receive `Scan`; a missing or incomplete index marker makes copy admission fail closed.

The bootstrap task requires `Recovery__Environment`, `Recovery__RecoveryWorkerBuildDigest`, `Recovery__DestinationRegion`, `Recovery__DestinationAccountId`, and `Recovery__ControlTableName`. It may also receive both `Recovery__OwnerId` and `Recovery__LibraryId` to include an explicitly provisioned empty scope. `Recovery__LibraryId` by itself does not filter discovered scopes; a completely empty table requires an explicit owner/library pair and an already provisioned canonical scope. The task never invents owner identities or watermark rows.

Before running the task, disable capture/copy schedules, stop every existing capture/copy task and deployment that can publish a point, and reconcile every unknown or unsettled write intent. DynamoDB's strongly consistent paginated Scan is not a table-wide snapshot. The task therefore requires writers to remain quiesced from the beginning of discovery through index completion and validation. If the scan limit or timeout is reached, any scope is malformed, or the final strongly consistent owner-partition query differs from the scanned point set, the task fails without declaring that scope complete. Do not enable schedules until every discovered scope has a complete marker and its baseline count/digest matches the indexed membership.

The baseline is stored at `OWNER#<sha256(ownerId NUL libraryId)>/INDEX`; immutable point membership rows use the same partition and `POINT#<lowercase-guid-D>` sort keys. Copy admission transactionally acquires the point lease, inserts any new membership row, and increments `indexedPointCount`. Replays condition-check the existing marker and exact membership without incrementing. The purge worker relies on this strongly consistent owner index and rejects a missing marker, count drift, baseline drift, or an unindexed point.

## Local validation

The recovery contract and runner tests are local checks, not proof of account separation or a cloud restore:

```powershell
dotnet run --project tests/recovery/Platform.Recovery.Tests.csproj
dotnet publish apps/recovery-worker/Platform.Recovery.Worker.csproj -c Release -o artifacts/recovery-worker
node tests/integration/recovery-native/run.mjs
```

The native integration harness requires Docker and exercises the pinned PostgreSQL 18 toolchain, exported-snapshot dump/restore, TLS hostname/CA validation, and inventory consistency. A real release still requires the protected recovery-account drill, independently signed access/isolation evidence, provider-backed alert delivery, workload/RPO/RTO measurements, and owner MFA access proof. Successful local commands must never be described as AWS qualification evidence.
