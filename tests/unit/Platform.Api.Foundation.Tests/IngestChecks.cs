using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Ingest.Worker;
using Platform.Ingest.Worker.Verification;
using Platform.Storage;
using System.Diagnostics;
using System.Security.Cryptography;
using static Platform.Storage.IngestDb;

internal static class IngestChecks
{
    public static async Task RunAsync()
    {
        foreach (var extension in new[] { "dng", "jpg", "png", "tiff", "dng.xmp", "_01.dng.xmp" })
        {
            var name = extension.StartsWith('_') ? "sample" + extension : "sample." + extension;
            using var file = File.OpenRead("tests/fixtures/ingest/" + name);
            _ = ContentInspector.Inspect(file, name);
        }
        using (var malformed = File.OpenRead("tests/fixtures/ingest/malformed.xmp"))
            await RejectAsync<InvalidDataException>(() => Task.FromResult(ContentInspector.Inspect(malformed, "bad.xmp")));
        var dng = File.ReadAllBytes("tests/fixtures/ingest/sample.dng");
        using (var truncated = new MemoryStream(dng[..4096], writable: false))
            await RejectAsync<InvalidDataException>(() => Task.FromResult(ContentInspector.Inspect(truncated, "truncated.dng")));
        var png = File.ReadAllBytes("tests/fixtures/ingest/sample.png");
        using (var truncated = new MemoryStream(png[..9], writable: false))
            await RejectAsync<InvalidDataException>(() => Task.FromResult(ContentInspector.Inspect(truncated, "truncated.png")));
        var jpeg = File.ReadAllBytes("tests/fixtures/ingest/sample.jpg");
        using (var truncated = new MemoryStream(jpeg[..4], writable: false))
            await RejectAsync<InvalidDataException>(() => Task.FromResult(ContentInspector.Inspect(truncated, "truncated.jpg")));
        Check(ContentInspector.SourceCandidates("folder/sample_01.dng.xmp").Contains(("folder/sample.dng", 1)), "Numbered darktable naming");
        if (Environment.GetEnvironmentVariable("INGEST_INTEGRATION") != "1") { Console.WriteLine("Ingest corpus checks passed; set INGEST_INTEGRATION=1 for local PostgreSQL/MinIO tests."); return; }

        var schema = "ingest_test_" + Guid.NewGuid().ToString("N");
        const string connectionString = "Host=127.0.0.1;Port=54329;Username=ingest;Password=ingest-test-only;Database=ingest;Include Error Detail=false";
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var commandSchema = "ingest_migration_" + Guid.NewGuid().ToString("N");
        var runtimeRole = "runtime_" + Guid.NewGuid().ToString("N");
        const string runtimePassword = "integration-runtime-password";
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {commandSchema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using (var migrationDb = NpgsqlDataSource.Create(connectionString + ";Search Path=" + commandSchema))
            {
                await DatabaseMigrator.ApplyAsync(migrationDb, runtimeRole, runtimePassword, default);
                await DatabaseMigrator.ApplyAsync(migrationDb, runtimeRole, runtimePassword, default);
                await using var count = migrationDb.CreateCommand("SELECT count(*) FROM platform_schema_migrations");
                Check((long)(await count.ExecuteScalarAsync())! == 2, "Migration command applies each embedded release migration exactly once");
            }
            await using var runtimeDb = NpgsqlDataSource.Create($"Host=127.0.0.1;Port=54329;Username={runtimeRole};Password={runtimePassword};Database=ingest;Search Path={commandSchema}");
            await using var runtimeCount = runtimeDb.CreateCommand("SELECT count(*) FROM platform_schema_migrations");
            Check((long)(await runtimeCount.ExecuteScalarAsync())! == 2, "Restricted runtime role can read migrated tables");
            await using var runtimeGrants = runtimeDb.CreateCommand("SELECT count(DISTINCT privilege_type) FROM information_schema.role_table_grants WHERE grantee=current_user AND table_schema=current_schema() AND table_name='assets' AND privilege_type IN ('SELECT','INSERT','UPDATE','DELETE')");
            Check((long)(await runtimeGrants.ExecuteScalarAsync())! == 4, "Runtime role receives only the required table DML class of access");
            await RejectAsync<PostgresException>(async () =>
            {
                await using var deniedDdl = runtimeDb.CreateCommand("CREATE TABLE runtime_must_not_create_schema_objects(id integer)");
                await deniedDdl.ExecuteNonQueryAsync();
            });
            await using var roleFlags = admin.CreateCommand("SELECT NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb FROM pg_roles WHERE rolname=$1");
            roleFlags.Parameters.AddWithValue(runtimeRole);
            Check((bool)(await roleFlags.ExecuteScalarAsync())!, "Runtime role has no administrative role attributes");
        }
        finally
        {
            await using var roleExists = admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)");
            roleExists.Parameters.AddWithValue(runtimeRole);
            if ((bool)(await roleExists.ExecuteScalarAsync())!)
            {
                await using var cleanupRole = admin.CreateCommand($"DROP OWNED BY {runtimeRole}; DROP ROLE {runtimeRole}");
                await cleanupRole.ExecuteNonQueryAsync();
            }
            await using var cleanupSchema = admin.CreateCommand($"DROP SCHEMA {commandSchema} CASCADE");
            await cleanupSchema.ExecuteNonQueryAsync();
        }
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        await using var db = NpgsqlDataSource.Create(connectionString + ";Search Path=" + schema);
        await MigrationChecks.ApplyAsync(db);
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"), new AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
        var bucket = schema.Replace('_', '-');
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        var storage = new S3ObjectStore(s3, bucket, "ingest-test-key");
        var imports = new ImportService(db, storage);
        var grants = new TransferGrantIssuer(db);
        var processor = new BatchProcessor(db, storage);
        const string owner = "test-owner";
        var images = new[] { "sample.dng", "sample.jpg", "sample.png", "sample.tiff", "sample.dng.xmp", "sample_01.dng.xmp" };
        var payloads = images.Select(name => File.ReadAllBytes("tests/fixtures/ingest/" + name)).ToArray();
        var request = new ImportRequest(true, images.Select((name, i) => new ImportItem(name, Hash(payloads[i]), payloads[i].Length)).ToArray());
        var batch = await imports.CreateAsync(owner, "test-import-key-01", "op_test", request, default);
        var replay = await imports.CreateAsync(owner, "test-import-key-01", "op_test", request, default);
        Check(replay.Id == batch.Id, "Create is durable and idempotent");
        await RejectAsync<IngestException>(() => imports.StatusAsync("another-owner", batch.Id, default));
        foreach (var item in batch.Items)
        {
            await grants.IssueAsync(item.Id, owner, 1, DateTime.UtcNow, default);
            var bytes = payloads[Array.IndexOf(images, item.Path)];
            var part = 1;
            for (var offset = 0; offset < bytes.Length; offset += S3ObjectStore.PartSize)
            {
                var chunk = bytes.Skip(offset).Take(S3ObjectStore.PartSize).ToArray();
                await imports.PutPartAsync(owner, item.Id, 1, part, chunk, Hash(chunk), "op_test", default);
                await imports.PutPartAsync(owner, item.Id, 1, part++, chunk, Hash(chunk), "op_test", default);
            }
            await imports.CompleteAsync(owner, item.Id, 1, "op_test", default);
            await imports.CompleteAsync(owner, item.Id, 1, "op_test", default);
        }
        Check(await processor.ProcessNextAsync(default), "Worker claims the durable publication set");
        var outcome = await imports.StatusAsync(owner, batch.Id, default);
        Check(outcome.State == "completed" && outcome.Items.All(i => i.State == "active"), "Atomic mixed corpus publishes");
        Check(!await processor.ProcessNextAsync(default), "Worker completion is idempotent");
        await using (var connection = await db.OpenConnectionAsync())
        {
            Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM assets", default))! == 4, "One asset per image");
            Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM edit_identities", default))! == 2, "Distinct base/numbered edits");
        }
        foreach (var bytes in payloads)
        {
            var address = new ContentAddress(Hash(bytes), bytes.Length);
            var meta = await s3.GetObjectMetadataAsync(bucket, address.AuthoritativeKey);
            await using var downloaded = await storage.ReadVerifiedAsync(address.AuthoritativeKey, meta.VersionId, address, default);
            Check(downloaded.Length == bytes.Length, "Download is byte exact");
        }
        await FaultChecksAsync(db, s3, bucket, imports, processor, storage, grants);
        await RetrievalPathChecks.RunAsync(db, storage);
        await LifecycleChecks.RunAsync(db, storage);
        await ReconstructionChecks.RunAsync(db, storage);
        await HttpIngestChecks.RunAsync(db, storage);
        await CapacityChecks.RunAsync(db, storage);
        await ReferenceBatchChecks.RunAsync(db, storage, s3, bucket);
        Console.WriteLine($"Ingest PostgreSQL/MinIO checks passed ({images.Length} corpus objects plus retry/quarantine/atomic/fault matrix). Test schema/bucket: {schema}/{bucket}");
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task FaultChecksAsync(NpgsqlDataSource db, IAmazonS3 s3, string bucket, ImportService imports, BatchProcessor processor, S3ObjectStore storage, TransferGrantIssuer grants)
    {
        const string owner = "test-owner";
        var png = File.ReadAllBytes("tests/fixtures/ingest/sample.png");
        var dng = File.ReadAllBytes("tests/fixtures/ingest/sample.dng");
        var jpeg = File.ReadAllBytes("tests/fixtures/ingest/sample.jpg");
        var invalid = "<bad"u8.ToArray();
        async Task<BatchStatus> Upload(bool atomic, params (string Path, byte[] Bytes, string? Hash)[] files)
        {
            var batch = await imports.CreateAsync(owner, Guid.NewGuid().ToString("N"), "op_fault", new(atomic, files.Select(f => new ImportItem(f.Path, f.Hash ?? Hash(f.Bytes), f.Bytes.Length)).ToArray()), default);
            foreach (var item in batch.Items)
            {
                var bytes = files.Single(f => f.Path == item.Path).Bytes;
                await grants.IssueAsync(item.Id, owner, 1, DateTime.UtcNow, default);
                for (int offset = 0, number = 1; offset < bytes.Length; offset += S3ObjectStore.PartSize, number++)
                {
                    var part = bytes.Skip(offset).Take(S3ObjectStore.PartSize).ToArray();
                    await imports.PutPartAsync(owner, item.Id, 1, number, part, Hash(part), "op_fault", default);
                }
                await imports.CompleteAsync(owner, item.Id, 1, "op_fault", default);
            }
            return batch;
        }
        var failed = await Upload(true, ("atomic.png", png, null), ("bad.xmp", invalid, null));
        await processor.ProcessNextAsync(default);
        var status = await imports.StatusAsync(owner, failed.Id, default);
        Check(status.State == "failed" && status.Items.All(i => i.State == "quarantined"), "Atomic failure publishes no selected item");
        var mixed = await Upload(false, ("copy.png", png, null), ("bad2.xmp", invalid, null), ("wrong.png", png, new string('0', 64)));
        await processor.ProcessNextAsync(default);
        status = await imports.StatusAsync(owner, mixed.Id, default);
        Check(status.Items.Single(i => i.Path == "copy.png").State == "active", "Independent good item remains active");
        Check(status.Items.Single(i => i.Path == "bad2.xmp").Code == "malformed_xmp", "Malformed XML is quarantined");
        Check(status.Items.Single(i => i.Path == "wrong.png").Code == "checksum_mismatch", "Whole-object SHA mismatch is quarantined");
        var orphan = await Upload(false, ("orphan.png", png, null));
        var address = new ContentAddress(Hash(png), png.Length);
        var before = await s3.GetObjectMetadataAsync(bucket, address.AuthoritativeKey);
        await using (var bytes = new MemoryStream(png)) await storage.PublishVerifiedAsync(address, bytes, default);
        await RejectAsync<AmazonS3Exception>(() => new BatchProcessor(db, new S3ObjectStore(s3, "missing-" + Guid.NewGuid().ToString("N"), "ingest-test-key")).ProcessNextAsync(default));
        Check((await imports.StatusAsync(owner, orphan.Id, default)).Items[0].State == "verifying", "Unavailable storage does not acknowledge content");
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE ingest_publications SET updated_at=now()-interval '10 seconds' WHERE batch_id=$1", default, orphan.Id);
        await processor.ProcessNextAsync(default);
        Check((await imports.StatusAsync(owner, orphan.Id, default)).State == "completed", "Restart repairs object/metadata interruption");
        var after = await s3.GetObjectMetadataAsync(bucket, address.AuthoritativeKey);
        Check(before.VersionId == after.VersionId, "Duplicate bytes reuse the immutable version");
        var cancellation = await imports.CreateAsync(owner, Guid.NewGuid().ToString("N"), "op_cancel", new(false, [new("cancel.png", Hash(png), png.Length)]), default);
        await imports.CancelAsync(owner, cancellation.Items[0].Id, 1, "op_cancel", default);
        await RejectAsync<IngestException>(() => imports.CompleteAsync(owner, cancellation.Items[0].Id, 1, "op_cancel", default));
        await processor.ProcessNextAsync(default);
        var unassociated = await Upload(false, ("missing.dng.xmp", File.ReadAllBytes("tests/fixtures/ingest/sample.dng.xmp"), null));
        await processor.ProcessNextAsync(default);
        Check((await imports.StatusAsync(owner, unassociated.Id, default)).Items[0].Code == "unassociated_xmp", "Unknown source never becomes authoritative XMP");
        var duplicateSource = await Upload(false, ("sample_01.dng", dng, null));
        await processor.ProcessNextAsync(default);
        Check((await imports.StatusAsync(owner, duplicateSource.Id, default)).State == "completed", "Second logical asset may reuse immutable bytes");
        var ambiguous = await Upload(false, ("sample_01.dng.xmp", File.ReadAllBytes("tests/fixtures/ingest/sample_01.dng.xmp"), null));
        await processor.ProcessNextAsync(default);
        var associationCorrectionStarted = Stopwatch.GetTimestamp();
        Check((await imports.StatusAsync(owner, ambiguous.Id, default)).Items[0].Code == "ambiguous_association", "Two deterministic source candidates report a stable owner-corrective state");
        var correctedAssociation = await Upload(false, ("copy.png.xmp", File.ReadAllBytes("tests/fixtures/ingest/sample_01.dng.xmp"), null));
        await processor.ProcessNextAsync(default);
        Check((await imports.StatusAsync(owner, correctedAssociation.Id, default)).State == "completed", "A corrected deterministic sidecar name publishes successfully");
        var associationCorrection = Stopwatch.GetElapsedTime(associationCorrectionStarted);
        Check(associationCorrection < TimeSpan.FromMinutes(2), "Reported association errors must be correctable within two minutes");
        Console.WriteLine($"Association correction exercise: {associationCorrection.TotalMilliseconds:F1} ms.");
        var corrupt = await Upload(false, ("truncated.png", png[..20], null), ("tiny.png", png[..9], null), ("tiny.jpg", jpeg[..4], null), ("unknown.bmp", "not an image"u8.ToArray(), null));
        await processor.ProcessNextAsync(default);
        Check((await imports.StatusAsync(owner, corrupt.Id, default)).Items.All(i => i.Code == "unsupported_format"), "Corrupt and unsupported content remain non-active");
        await RejectAsync<IngestException>(() => new ImportService(db, storage, 1).CreateAsync(owner, Guid.NewGuid().ToString("N"), "op_quota", new(false, [new("quota.png", Hash(png), png.Length)]), default));
        var unauthorized = await imports.CreateAsync(owner, Guid.NewGuid().ToString("N"), "op_authority", new(false, [new("authority.png", Hash(png), png.Length)]), default);
        await RejectAsync<IngestException>(() => imports.PutPartAsync(owner, unauthorized.Items[0].Id, 1, 1, png, Hash(png), "op_authority", default));
        await grants.IssueAsync(unauthorized.Items[0].Id, owner, 1, DateTime.UtcNow, default);
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE upload_sessions SET transfer_authorized_until=now()-interval '1 second' WHERE id=$1", default, unauthorized.Items[0].Id);
        await RejectAsync<IngestException>(() => imports.PutPartAsync(owner, unauthorized.Items[0].Id, 1, 1, png, Hash(png), "op_authority", default));
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE upload_sessions SET quarantine_until=now()-interval '1 second' WHERE quarantine_version IS NOT NULL; UPDATE upload_sessions SET expires_at=now()-interval '1 second' WHERE id=$1", default, unauthorized.Items[0].Id);
        await processor.ExpireAsync(default);
        await using (var connection = await db.OpenConnectionAsync())
        {
            Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM upload_sessions WHERE quarantine_version IS NOT NULL AND quarantine_until<=now()", default))! == 0, "Expired quarantine versions are removed");
            Check(await ScalarAsync(connection, "SELECT state FROM upload_sessions WHERE id=$1", default, unauthorized.Items[0].Id) as string == "failed", "Expired uploads cannot later complete");
        }
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
