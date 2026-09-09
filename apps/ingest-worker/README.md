# Ingest worker

Processes durable Feature 005 batch publication, whole-stream verification, conservative image/XMP association, quarantine, retry, and temporary-object expiry. It never activates metadata until version-pinned bytes and the selected atomic set pass.

For offline recovery verification, run `dotnet run --project apps/ingest-worker/Platform.Ingest.Worker.csproj -- --verify-manifest <trusted.json> <recovered.json> <object-directory>`. See the [manifest runbook](../../docs/runbooks/reconstruction-manifest.md).
