using Npgsql;
using Platform.Contracts.Exports;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.Exports;
using static Platform.Storage.IngestDb;
using Amazon.S3;
using Amazon.Runtime;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;
using System.Security.Claims;
public static class ExportIntegration
{
    public static async Task RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("EXPORT_DATABASE") ?? "Host=127.0.0.1;Port=54329;Username=ingest;Password=ingest-test-only;Database=ingest";
        var schema = "export_test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(connection);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = NpgsqlDataSource.Create(connection + ";Search Path=" + schema);
            await Platform.Ingest.Worker.DatabaseMigrator.ApplyAsync(db, default);
            await Platform.Ingest.Worker.DatabaseMigrator.ApplyAsync(db, default);
            await using (var oldReader = db.CreateCommand("SELECT count(*) FROM assets WHERE state='active'"))
                if ((long)(await oldReader.ExecuteScalarAsync())! != 0) throw new InvalidOperationException("Additive migration old-reader compatibility");
            await using var count = db.CreateCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name LIKE 'export_%'");
            if ((long)(await count.ExecuteScalarAsync())! != 9) throw new InvalidOperationException("Export migration table count");
            using var s3 = new FixtureS3();
            await s3.ListBucketsAsync(new ListBucketsRequest());
            var bucket = "exports-" + Guid.NewGuid().ToString("N");
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
            try { await BasketChecksAsync(db, s3, bucket); }
            finally
            {
                var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
                foreach (var version in versions.Versions) await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = version.Key, VersionId = version.VersionId });
                await s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
            }
            Console.WriteLine("Export PostgreSQL migration and MinIO connectivity checks passed.");
        }
        finally { await using var cleanup = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
    private static async Task BasketChecksAsync(NpgsqlDataSource db, IAmazonS3 s3, string bucket)
    {
        var selection = new ExportSelectionService(db); var service = new ExportBasketService(db, selection);
        var basket = (await service.ListAsync("owner", "library", default)).Single();
        byte[] originalBytes = [1, 2, 3, 4];
        var sourceVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "original/a", InputStream = new MemoryStream(originalBytes) })).VersionId;
        var asset = Guid.NewGuid(); var edit = Guid.NewGuid(); var sourceSha = ExportIntegrity.Hash(originalBytes); var xmpSha = new string('b', 64);
        var manifest = new VersionControlManifest("library", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "root", [new(asset, edit, "added", null, xmpSha, "photos/CON.CR2", "active")]);
        var bytes = VersionControlManifestCodec.Write(manifest); var commit = VersionControlManifestCodec.Digest(bytes);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,4,'original','original/a','v1','authoritative'),($2,4,'xmp','xmp/b','v1','authoritative'); INSERT INTO assets(id,owner_id,original_sha256,state) VALUES($3,'owner',$1,'active'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($4,$3,0,$2,'fixture'); INSERT INTO asset_paths(id,owner_id,asset_id,display_path,collision_key,active,effective_from) VALUES($5,'owner',$3,'photos/CON.CR2','photos/con.cr2',true,now())", default, sourceSha, xmpSha, asset, edit, Guid.NewGuid());
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner','library','owner',$2,$2,'root',1,1,$3); INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES('owner','library','main','main','branch',$1); INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($4,$5,'history',$6)", default, commit, DateTimeOffset.UnixEpoch, bytes, Guid.NewGuid(), asset, "vc:" + commit);
            await ExecuteAsync(connection, "UPDATE content_objects SET storage_version=$2 WHERE sha256=$1", default, sourceSha, sourceVersion);
        }
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, "current"), null, default))!;
        var originalPin = basket.Items.Single().Pin;
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, commit), null, default))!;
        Check(basket.Items.Count == 2 && basket.Items.Select(i => i.Pin.ItemId).Distinct().Count() == 2, "Duplicates remain explicit");
        await RejectAsync(async () => await service.GetAsync("other-owner", "library", basket.Id, default));
        await RejectAsync(async () => await service.GetAsync("owner", "other-library", basket.Id, default));
        await RejectAsync(async () => await service.MutateAsync("owner", "library", basket.Id, 1, "clear", null, false, null, null, default));
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "INSERT INTO vc_visibility(owner_id,library_id,commit_id,hidden,changed_at,changed_by) VALUES('owner','library',$1,true,now(),'owner')", default, commit);
        await RejectAsync(async () => await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, commit), null, default));
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, commit, true), null, default))!;
        Check(basket.Items.All(i => i.Pin.CommitId == originalPin.CommitId), "Hiding preserves pinned identities");
        await RejectAsync(async () => await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, "working"), null, default));
        for (var i = 1; i < 20; i++) await service.CreateAsync("owner", "library", "Basket " + i, default);
        Check((await service.ListAsync("owner", "library", default)).Count == 20, "Twenty saved baskets supported");
        await RejectAsync(async () => await service.CreateAsync("owner", "library", "Overflow", default));
        if (Environment.GetEnvironmentVariable("EXPORT_PERFORMANCE") == "1")
        {
            await using var connection = await db.OpenConnectionAsync();
            foreach (var number in Enumerable.Range(basket.Items.Count, 250 - basket.Items.Count))
            {
                var extra = originalPin with { ItemId = Guid.NewGuid() };
                await ExecuteAsync(connection, "INSERT INTO export_basket_items(id,basket_id,pin) VALUES($1,$2,$3::jsonb)", default, extra.ItemId, basket.Id, ExportDatabase.Encode(extra));
            }
            basket = await service.GetAsync("owner", "library", basket.Id, default);
            await RejectAsync(async () => await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, commit, true), null, default));
            var opens = new List<double>(); var updates = new List<double>();
            for (var number = 0; number < 20; number++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); basket = await service.GetAsync("owner", "library", basket.Id, default); opens.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart(); basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "rename", "Capacity basket", false, null, null, default))!; updates.Add(clock.Elapsed.TotalMilliseconds);
            }
            opens.Sort(); updates.Sort(); Console.WriteLine($"Export 20-basket/250-item PostgreSQL p95 open={opens[18]:F1}ms update={updates[18]:F1}ms; fixture includes explicit duplicate pins.");
            Check(opens[18] <= 2000 && updates[18] <= 2000, "250-item server interaction timing budget");
        }
        var preparation = new ExportPreparationService(db, service);
        var submit = new ExportSubmit("originals-v1", basket.Revision, true, basket.Items.Count >= 100);
        var submissions = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => preparation.SubmitAsync("owner", "library", basket.Id, submit, "fifty-retries", default)));
        var request = submissions[0];
        Check(submissions.All(r => r.Id == request.Id), "Fifty concurrent retries create exactly one request");
        await RejectAsync(async () => await preparation.SubmitAsync("owner", "library", basket.Id, submit with { MetadataConfirmed = false }, "fifty-retries", default));
        var writer = Guid.NewGuid();
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, request.Id, writer);
        var invalidation = new ExportInvalidationService(db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Exports:Enabled"] = "true", ["Exports:DownloadOrigin"] = "https://downloads.example.test", ["Exports:WebOrigin"] = "https://web.example.test", ["Ingest:Bucket"] = bucket, ["Rendering:DerivativeBucket"] = bucket, ["Ingest:KmsKeyId"] = "fixture-only" }).Build();
        var rendering = new Platform.Api.Features.Rendering.RenderingService(db, null!, configuration);
        var packages = new ExportPackageService(db, s3, configuration, rendering, selection, invalidation);
        Check(await packages.PrepareAsync("owner", "library", request.Id, writer, 1, default), "Original package preparation completes");
        var ready = await preparation.GetAsync("owner", "library", request.Id, default);
        Check(ready.State == "completed" && ready.Outcomes.Count == basket.Items.Count && ready.Outcomes.All(o => o.Sha256 == sourceSha && o.Render is null), "All duplicate original outputs remain byte exact without rendering");
        var downloads = new ExportDownloadService(db, s3, configuration);
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new HostString("downloads.example.test");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("cognito:groups", "owner"), new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())], "fixture"));
        context.Request.Headers.Authorization = "Bearer fixture"; context.Response.Body = new MemoryStream();
        await downloads.StreamAsync(context, "library", request.Id, default);
        var zipped = ((MemoryStream)context.Response.Body).ToArray(); Check(ExportIntegrity.Hash(zipped) == ready.PackageSha256, "Streamed ZIP checksum matches published immutable package");
        using (var zip = new ZipArchive(new MemoryStream(zipped)))
        {
            Check(zip.Entries.Count == basket.Items.Count + 2 && zip.GetEntry("manifest.json") is not null && zip.GetEntry("failures.json") is not null, "ZIP includes every requested output and mandatory evidence");
            foreach (var outcome in ready.Outcomes) { using var data = new MemoryStream(); await zip.GetEntry(outcome.Filename!)!.Open().CopyToAsync(data); Check(data.ToArray().SequenceEqual(originalBytes), "Unchanged original bytes verified inside ZIP"); }
        }
        context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new HostString("downloads.example.test"); context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("cognito:groups", "owner")], "fixture")); context.Request.Headers.Range = "bytes=1-3"; context.Response.Body = new MemoryStream();
        await downloads.StreamAsync(context, "library", request.Id, default);
        Check(context.Response.StatusCode == 206 && ((MemoryStream)context.Response.Body).ToArray().SequenceEqual(zipped[1..4]), "Native range returns exact immutable bytes");
        var mint = new DefaultHttpContext(); mint.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("cognito:groups", "owner"), new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())], "fixture")); mint.Request.Headers.Authorization = "Bearer fixture-session";
        var grant = System.Text.Json.JsonSerializer.SerializeToElement(await downloads.MintAsync(mint, "owner", "library", request.Id, default), ExportDatabase.Json);
        var form = System.Text.Encoding.UTF8.GetBytes("code=" + grant.GetProperty("code").GetString());
        DefaultHttpContext Exchange(string origin)
        { var c = new DefaultHttpContext(); c.Request.Scheme = "https"; c.Request.Host = new HostString("downloads.example.test"); c.Request.ContentType = "application/x-www-form-urlencoded"; c.Request.ContentLength = form.Length; c.Request.Body = new MemoryStream(form); c.Request.Headers.Origin = origin; return c; }
        await RejectAsync(async () => await downloads.ExchangeAsync(Exchange("https://foreign.example.test"), "library", request.Id, default));
        var exchange = Exchange("https://web.example.test"); await downloads.ExchangeAsync(exchange, "library", request.Id, default);
        Check(exchange.Response.StatusCode == 303 && exchange.Response.Headers.SetCookie.ToString().Contains("httponly", StringComparison.OrdinalIgnoreCase), "Native exchange sets protected cookie and credential-free redirect");
        await RejectAsync(async () => await downloads.ExchangeAsync(Exchange("https://web.example.test"), "library", request.Id, default));
        var cookie = exchange.Response.Headers.SetCookie.ToString().Split(';')[0];
        DefaultHttpContext CookieRequest() { var c = new DefaultHttpContext(); c.Request.Scheme = "https"; c.Request.Host = new HostString("downloads.example.test"); c.Request.Headers.Cookie = cookie; c.Request.Method = "HEAD"; return c; }
        await downloads.StreamAsync(CookieRequest(), "library", request.Id, default);
        await downloads.RevokeAsync(mint, "owner", default);
        await RejectAsync(async () => await downloads.StreamAsync(CookieRequest(), "library", request.Id, default));
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "ALTER TABLE gallery_generations RENAME TO fixture_unavailable_gallery_generations", default);
        Check((await service.GetAsync("owner", "library", basket.Id, default)).Items.Count == basket.Items.Count, "Gallery-index outage leaves baskets readable");
        var retained = new List<Guid>(); var acknowledgments = new List<double>();
        for (var number = 0; number < 9; number++) { var clock = System.Diagnostics.Stopwatch.StartNew(); retained.Add((await preparation.SubmitAsync("owner", "library", basket.Id, submit, "retained-" + number, default)).Id); acknowledgments.Add(clock.Elapsed.TotalMilliseconds); }
        Check((await preparation.ListAsync("owner", "library", default)).Count == 10, "Ten requests per basket retained");
        await RejectAsync(async () => await preparation.SubmitAsync("owner", "library", basket.Id, submit, "retained-overflow", default));
        acknowledgments.Sort(); Console.WriteLine($"Export immutable submit p95={acknowledgments[^1]:F1}ms items={basket.Items.Count}; ten retained requests enforced.");
        Check(acknowledgments[^1] <= 3000, "Immutable snapshot acknowledgment timing budget");
        var cleanup = new ExportCleanupService(db, s3, configuration);
        var worker = new ExportPreparationWorker(db, packages, cleanup, configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportPreparationWorker>.Instance);
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE export_requests SET state='failed',expires_at=now()-interval '1 second' WHERE id=$1; UPDATE export_requests SET state='preparing',lease_id=$3,lease_until=now()-interval '1 second' WHERE id=$2", default, retained[0], retained[1], writer);
        await worker.MaintainAsync(default);
        Check((await preparation.GetAsync("owner", "library", retained[0], default)).State == "expired", "Maintenance expires failed requests without blocking preparation");
        Check((await preparation.GetAsync("owner", "library", retained[1], default)).State == "retryable", "Maintenance fences expired worker leases for retry");
        await using (var connection = await db.OpenConnectionAsync())
            Check(Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM audit_events WHERE event_type IN ('exports.expired','exports.lease_recovered')", default)) == 2, "Expiry and lease recovery produce durable audit evidence");
        request = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "purge-race", default);
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, request.Id, writer);
        var intent = await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default);
        var unknown = await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default);
        await invalidation.SettleWriteAsync(unknown, writer, null, false, default);
        var purge = new ExportPurgeEvent(Guid.NewGuid(), "owner", "library", asset, null, "original", sourceSha, 1, Guid.NewGuid());
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var tx = await connection.BeginTransactionAsync();
            await invalidation.ApplyAsync(tx, purge, default);
            await invalidation.ApplyAsync(tx, purge, default);
            Check(!await invalidation.CanAcknowledgeAsync(connection, "owner", "library", purge.EventId, default), "Admitted writer blocks cleanup acknowledgment");
            await tx.CommitAsync();
        }
        await RejectAsync(async () => await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default));
        var lateKey = $"exports/{request.Id:N}/{intent:N}/package.zip";
        var late = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = lateKey, InputStream = new MemoryStream(originalBytes) });
        await invalidation.SettleWriteAsync(intent, writer, late.VersionId, true, default);
        Check((await preparation.GetAsync("owner", "library", request.Id, default)).State == "unavailable", "Late upload cannot restore invalidated request");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "derivatives/shared/preview.jpg", InputStream = new MemoryStream(originalBytes) });
        await cleanup.TickAsync(default);
        Check((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = lateKey } )).Versions?.Any() != true, "Late settled object versions are deleted");
        Check((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = "derivatives/shared/" })).Versions.Count == 1, "Export cleanup preserves shared renderer objects");
        await using (var connection = await db.OpenConnectionAsync()) { await using var tx = await connection.BeginTransactionAsync(); Check(!await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Unknown storage outcomes remain draining after settled cleanup"); await tx.CommitAsync(); }
        await invalidation.SettleWriteAsync(unknown, writer, null, true, default); await cleanup.TickAsync(default);
        await using (var connection = await db.OpenConnectionAsync()) { await using var tx = await connection.BeginTransactionAsync(); Check(await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Acknowledgment follows proven settlement and exact version cleanup"); await tx.CommitAsync(); }
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "clear", null, false, null, null, default))!;
        Check(basket.Items.Count == 0, "Clear is persistent");
        Check((await preparation.GetAsync("owner", "library", request.Id, default)).Snapshot.Items.Count == request.Snapshot.Items.Count, "Basket changes preserve immutable request snapshot");
        await using var hold = db.CreateCommand("SELECT count(*) FROM lifecycle_holds");
        Check((long)(await hold.ExecuteScalarAsync())! == 1, "Basket changes never add or release lifecycle holds");
        Console.WriteLine("Export persistent basket/selection/authorization/revision/hidden/quota checks passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (ExportProblem) { return; } throw new InvalidOperationException("Expected rejection"); }}
// MinIO fixture omits unsupported AWS KMS headers; production service still requires AWS KMS.
public sealed class FixtureS3() : AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"), new AmazonS3Config { ServiceURL = Environment.GetEnvironmentVariable("EXPORT_S3_ENDPOINT") ?? "http://127.0.0.1:9000", ForcePathStyle = true })
{
    public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
    { request.ServerSideEncryptionMethod = null; request.ServerSideEncryptionKeyManagementServiceKeyId = null; return base.InitiateMultipartUploadAsync(request, cancellationToken); }
}
