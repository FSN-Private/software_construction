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
