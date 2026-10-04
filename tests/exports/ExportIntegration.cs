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
            await ExportHttpChecks.RunAsync(db);
            await using (var oldReader = db.CreateCommand("SELECT count(*) FROM assets WHERE state='active'"))
                if ((long)(await oldReader.ExecuteScalarAsync())! != 0) throw new InvalidOperationException("Additive migration old-reader compatibility");
            await using var count = db.CreateCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name LIKE 'export_%'");
            if ((long)(await count.ExecuteScalarAsync())! != 9) throw new InvalidOperationException("Export migration table count");
            using var s3 = new FixtureS3();
            await s3.ListBucketsAsync(new ListBucketsRequest());
            var bucket = "exports-" + Guid.NewGuid().ToString("N");
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
            try { await BasketChecksAsync(db, s3, bucket); await UnknownInitiationChecksAsync(db, s3, bucket); }
            finally
            {
                var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
                foreach (var upload in uploads.MultipartUploads ?? []) await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = upload.Key, UploadId = upload.UploadId });
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
        if (Environment.GetEnvironmentVariable("EXPORT_PERFORMANCE") != "1") { originalBytes = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(originalBytes); }
        var sourceVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "original/a", InputStream = new MemoryStream(originalBytes) })).VersionId;
        byte[] firstXmp = [4, 3, 2, 1]; var xmpSha = ExportIntegrity.Hash(firstXmp);
        var xmpVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "xmp/b", InputStream = new MemoryStream(firstXmp) })).VersionId;
        var asset = Guid.NewGuid(); var edit = Guid.NewGuid(); var sourceSha = ExportIntegrity.Hash(originalBytes);
        var manifest = new VersionControlManifest("library", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "root", [new(asset, edit, "added", null, xmpSha, "photos/CON.CR2", "active")]);
        var bytes = VersionControlManifestCodec.Write(manifest); var commit = VersionControlManifestCodec.Digest(bytes);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$6,'original','original/a','v1','authoritative'),($2,4,'xmp','xmp/b','v1','authoritative'); INSERT INTO assets(id,owner_id,original_sha256,state) VALUES($3,'owner',$1,'active'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($4,$3,0,$2,'fixture'); INSERT INTO asset_paths(id,owner_id,asset_id,display_path,collision_key,active,effective_from) VALUES($5,'owner',$3,'photos/CON.CR2','photos/con.cr2',true,now())", default, sourceSha, xmpSha, asset, edit, Guid.NewGuid(), (long)originalBytes.Length);
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner','library','owner',$2,$2,'root',1,1,$3); INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES('owner','library','main','main','branch',$1); INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($4,$5,'history',$6)", default, commit, DateTimeOffset.UnixEpoch, bytes, Guid.NewGuid(), asset, "vc:" + commit);
            await ExecuteAsync(connection, "UPDATE content_objects SET storage_version=$2 WHERE sha256=$1", default, sourceSha, sourceVersion);
            await ExecuteAsync(connection, "UPDATE content_objects SET storage_version=$2 WHERE sha256=$1", default, xmpSha, xmpVersion);
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
        var parentCommit = commit; var priorXmp = xmpSha;
        foreach (var number in Enumerable.Range(1, 3))
        {
            var nextXmpBytes = System.Text.Encoding.UTF8.GetBytes("historical-xmp-" + number); var nextXmp = ExportIntegrity.Hash(nextXmpBytes);
            var nextKey = "xmp/history-" + number;
            var nextVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = nextKey, InputStream = new MemoryStream(nextXmpBytes) })).VersionId;
            var nextManifest = new VersionControlManifest("library", [parentCommit], "owner", DateTimeOffset.UnixEpoch.AddMinutes(number), DateTimeOffset.UnixEpoch.AddMinutes(number), "history " + number, [new(asset, edit, "modified", priorXmp, nextXmp, "photos/CON.CR2", "active")]);
            var nextBytes = VersionControlManifestCodec.Write(nextManifest); var nextCommit = VersionControlManifestCodec.Digest(nextBytes);
            await using (var connection = await db.OpenConnectionAsync())
            {
                await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative'); INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($5,'owner','library','owner',$6,$6,$12,1,1,$7); INSERT INTO vc_commit_parents(commit_id,parent_id,ordinal) VALUES($5,$13,0); UPDATE vc_refs SET head_commit_id=$5 WHERE owner_id='owner' AND library_id='library' AND normalized_name='main'; UPDATE edit_identities SET xmp_sha256=$1 WHERE id=$8; INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($9,$10,'history',$11)", default, nextXmp, (long)nextXmpBytes.Length, nextKey, nextVersion, nextCommit, DateTimeOffset.UnixEpoch.AddMinutes(number), nextBytes, edit, Guid.NewGuid(), asset, "vc:" + nextCommit, "history " + number, parentCommit);
            }
            basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, "current"), null, default))!;
            Check(basket.Items.Single(i => i.Pin.CommitId == nextCommit).Pin.Xmp.Sha256 == nextXmp, "Each moving ref captures its own exact committed XMP");
            parentCommit = nextCommit; priorXmp = nextXmp;
        }
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, commit, true), null, default))!;
        Check(basket.Items.Where(i => i.Pin.CommitId == commit).All(i => i.Pin.Xmp == originalPin.Xmp) && basket.Items.Single(i => i.Pin.ItemId == originalPin.ItemId).Pin == originalPin && basket.Items.Select(i => i.Pin.CommitId).Distinct().Count() == 4, "Current plus three historical identities survive ref movement without XMP substitution");
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE vc_refs SET head_commit_id=$1 WHERE owner_id='owner' AND library_id='library' AND normalized_name='main'", default, commit);
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(asset, edit, parentCommit), null, default))!;
        Check(basket.Items.Count(i => i.Pin.CommitId == parentCommit && i.Pin.Xmp.Sha256 == priorXmp) == 2, "Explicit retained displaced commit remains exact after ref rewrite removes its reachability");
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "UPDATE assets SET state='recoverable-deleted',deleted_until=now()+interval '1 day' WHERE id=$1", default, asset);
            Check(await selection.AvailabilityAsync(connection, "owner", "library", originalPin, false, default) is null, "Retained soft-deleted bytes remain eligible");
            await ExecuteAsync(connection, "UPDATE assets SET deleted_until=now()-interval '1 second' WHERE id=$1; UPDATE lifecycle_holds SET expires_at=now()-interval '1 second' WHERE asset_id=$1", default, asset);
            Check(await selection.AvailabilityAsync(connection, "owner", "library", originalPin, true, default) == "source_unavailable", "Expired recovery bytes without an active hold are unavailable");
            Check(await selection.AvailabilityAsync(connection, "owner", "library", originalPin, false, default) == "selected_version_unavailable", "Expired historical selection cannot render");
            await ExecuteAsync(connection, "UPDATE assets SET state='active',deleted_until=NULL WHERE id=$1; UPDATE lifecycle_holds SET expires_at=NULL WHERE asset_id=$1; UPDATE content_objects SET integrity_state='integrity-error' WHERE sha256=$2", default, asset, xmpSha);
            Check(await selection.AvailabilityAsync(connection, "owner", "library", originalPin, false, default) == "selected_version_unavailable" && await selection.AvailabilityAsync(connection, "owner", "library", originalPin, true, default) is null, "Unavailable XMP prevents rendered export while original-only eligibility remains independent");
            await ExecuteAsync(connection, "UPDATE content_objects SET integrity_state='authoritative' WHERE sha256=$1", default, xmpSha);
        }
        await PinnedRenderChecksAsync(db, originalPin, s3, bucket);
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
        if (Environment.GetEnvironmentVariable("EXPORT_PERFORMANCE") != "1")
        {
            Check(zipped.Length > 10 * 1024 * 1024, "Real local package exceeds Gateway body limit through bounded native API streaming");
            var stopped = new DefaultHttpContext(); stopped.Request.Scheme = "https"; stopped.Request.Host = new HostString("downloads.example.test"); stopped.User = context.User; stopped.Request.Headers.Authorization = "Bearer fixture";
            using var sink = new ExpiringExportSink(async () => { await using var connection = await db.OpenConnectionAsync(); await ExecuteAsync(connection, "UPDATE export_requests SET expires_at=now()-interval '1 second' WHERE id=$1", default, ready.Id); await Task.Delay(1100); });
            stopped.Response.Body = sink;
            await RejectAsync(async () => await downloads.StreamAsync(stopped, "library", ready.Id, default));
            Check(sink.Length > 0 && sink.Length < zipped.Length && sink.LargestWrite <= 81920, "Active transfer stops at expiry with bounded writes before remaining package bytes are sent");
            await using var connection = await db.OpenConnectionAsync(); await ExecuteAsync(connection, "UPDATE export_requests SET expires_at=now()+interval '24 hours' WHERE id=$1", default, ready.Id);
        }
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
        Check((await preparation.ListAsync("owner", "library", default)).Count(r => r.BasketId == basket.Id) == 10, "Ten requests per basket retained");
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
        configuration["Exports:OperationsUsername"] = "export-operator";
        var operations = new ExportOperationsService(db, configuration);
        var operatorUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("cognito:groups", "operator"), new Claim("cognito:username", "export-operator")], "fixture"));
        await RejectAsync(async () => await operations.GetAsync(new ClaimsPrincipal(), default));
        await RejectAsync(async () => await operations.GetAsync(mint.User, default));
        Check((await operations.SetAsync(operatorUser, "pause", default)).Paused, "Operator pause is visible in aggregate state");
        await RejectAsync(async () => await preparation.SubmitAsync("owner", "library", basket.Id, submit, "paused-request", default));
        await worker.TickAsync(default);
        Check((await operations.GetAsync(operatorUser, default)).Active == 0, "Paused worker claims no pending preparations");
        Check((await operations.SetAsync(operatorUser, "drain", default)).Draining, "Operator drain retains readable aggregate state");
        Check(!(await operations.SetAsync(operatorUser, "resume", default)).Paused, "Operator resume reopens intake");
        await operations.SetAsync(operatorUser, "cancel-pending", default); await worker.MaintainAsync(default);
        var cancelled = await preparation.GetAsync("owner", "library", retained[1], default);
        Check(cancelled.State == "cancelled" && cancelled.Outcomes.All(o => o.Status != "pending"), "Operator cancellation drains pending jobs and their item outcomes");
        Check((await preparation.GetAsync("owner", "library", ready.Id, default)).State == "completed", "Operator cancellation preserves completed package provenance");
        await operations.SetAsync(operatorUser, "resume", default);
        await using (var connection = await db.OpenConnectionAsync())
            Check(Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM audit_events WHERE event_type IN ('exports.expired','exports.lease_recovered')", default)) == 2, "Expiry and lease recovery produce durable audit evidence");
        if (s3 is FixtureS3 readFaults)
        {
            foreach (var fault in new[] { "corrupt", "missing" })
            {
                var faultRequest = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "input-" + fault, default);
                await using (var connection = await db.OpenConnectionAsync())
                    await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, faultRequest.Id, writer);
                readFaults.CorruptNextRead = fault == "corrupt"; readFaults.FailNextRead = fault == "missing";
                Check(await packages.PrepareAsync("owner", "library", faultRequest.Id, writer, 1, default), "Faulted item preparation reaches a complete mixed outcome");
                var mixed = await preparation.GetAsync("owner", "library", faultRequest.Id, default);
                Check(mixed.State == "partial" && mixed.Outcomes.Count(o => o.Status == "completed") == basket.Items.Count - 1 && mixed.Outcomes.Count(o => o.Reason == (fault == "corrupt" ? "integrity_error" : "storage_unavailable")) == 1, "Corrupt/missing exact input never appears as a successful output");
                var mixedContext = new DefaultHttpContext(); mixedContext.Request.Scheme = "https"; mixedContext.Request.Host = new HostString("downloads.example.test"); mixedContext.User = context.User; mixedContext.Response.Body = new MemoryStream();
                await downloads.StreamAsync(mixedContext, "library", mixed.Id, default);
                using var mixedZip = new ZipArchive(new MemoryStream(((MemoryStream)mixedContext.Response.Body).ToArray()));
                Check(mixedZip.Entries.Count == basket.Items.Count + 1, "Mixed ZIP contains successful outputs plus full manifest and failures");
                using var failureData = new StreamReader(mixedZip.GetEntry("failures.json")!.Open());
                Check((await failureData.ReadToEndAsync()).Contains(fault == "corrupt" ? "integrity_error" : "storage_unavailable"), "Failures summary records the exact safe failed-input reason");
            }
            var oversize = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "oversize", default);
            await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET created_at=now()-interval '3 seconds' WHERE id=$1", default, oversize.Id);
            configuration["Exports:PackageBytes"] = "1";
            var measured = new ExportMetricTestLogger();
            using (var collector = new Platform.Api.Telemetry.ExportMetricLoggingService(measured)) { await collector.StartAsync(default); await worker.TickAsync(default); await collector.StopAsync(default); }
            Check(measured.Events.Any(e => Equals(e["MetricName"], "platform.exports.failed") && Equals(e["MetricValue"], 1d)) && !measured.Events.Any(e => Equals(e["MetricName"], "platform.exports.prepared")), "Terminal failure is measured as failed rather than successfully prepared");
            Check(measured.Events.Any(e => Equals(e["MetricName"], "platform.exports.preparation.duration") && Convert.ToDouble(e["MetricValue"]) >= 3000), "Terminal preparation duration includes elapsed request waiting, not only the last worker attempt");
            configuration["Exports:PackageBytes"] = null;
            var limited = await preparation.GetAsync("owner", "library", oversize.Id, default);
            Check(limited.State == "failed" && limited.Reason == "resource_limit" && limited.PackageSha256 is null && limited.Outcomes.All(o => o.Status != "pending"), "Oversize preparation cannot publish a package and terminalizes all pending outcomes");
            var outage = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "all-inputs-unavailable", default);
            var outageMeasurements = new ExportMetricTestLogger();
            readFaults.FailAllReads = true;
            try { using var collector = new Platform.Api.Telemetry.ExportMetricLoggingService(outageMeasurements); await collector.StartAsync(default); await worker.TickAsync(default); await collector.StopAsync(default); }
            finally { readFaults.FailAllReads = false; }
            var allFailed = await preparation.GetAsync("owner", "library", outage.Id, default);
            Check(allFailed.State == "failed" && allFailed.PackageSha256 is null && allFailed.Outcomes.All(o => o.Status == "failed" && o.Reason == "storage_unavailable"), "All-input provider outage retains complete failed outcomes without a media package");
            Check(outageMeasurements.Events.Any(e => Equals(e["MetricName"], "platform.exports.failed")) && !outageMeasurements.Events.Any(e => Equals(e["MetricName"], "platform.exports.prepared")), "Published all-failed manifests feed failure measurements rather than success counts");
        }
        var rollbackPending = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "rollback-pending", default);
        configuration["Exports:Enabled"] = "false";
        await worker.TickAsync(default);
        Check((await preparation.GetAsync("owner", "library", rollbackPending.Id, default)).State == "pending", "Disabled capability admits no worker claims during rollback");
        await operations.SetAsync(operatorUser, "cancel-pending", default); await worker.MaintainAsync(default);
        Check((await preparation.GetAsync("owner", "library", rollbackPending.Id, default)).State == "cancelled", "Operator cancellation and maintenance drain queued work while capability is disabled");
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET expires_at=now()-interval '1 second' WHERE id=$1", default, ready.Id);
        await worker.MaintainAsync(default);
        Check((await preparation.GetAsync("owner", "library", ready.Id, default)).State == "expired", "Completed package expiry becomes visible and cleanup removes its settled media");
        configuration["Exports:Enabled"] = "true";
        await operations.SetAsync(operatorUser, "resume", default);
        await RejectAsync(async () => await downloads.StreamAsync(context, "library", ready.Id, default));
        var beforeRace = basket;
        async Task<ExportRequestView?> RaceSubmission()
        {
            try { return await preparation.SubmitAsync("owner", "library", beforeRace.Id, submit, "concurrent-basket-edit", default); }
            catch (ExportProblem problem) when (problem.Code == "stale_basket") { return null; }
        }
        var raceSubmission = RaceSubmission();
        var raceEdit = service.MutateAsync("owner", "library", basket.Id, basket.Revision, "remove", null, false, null, originalPin.ItemId, default);
        await Task.WhenAll(raceSubmission, raceEdit); basket = (await raceEdit)!;
        if (await raceSubmission is { } captured)
        {
            Check(captured.Snapshot.Items.Select(i => i.ItemId).SequenceEqual(beforeRace.Items.Select(i => i.Pin.ItemId)), "Concurrent edit cannot partially change an acknowledged immutable snapshot");
            await preparation.CancelAsync("owner", "library", captured.Id, default); await worker.MaintainAsync(default);
        }
        Check(basket.Items.Count == beforeRace.Items.Count - 1, "Concurrent basket edit is persisted independently of immutable submission");
        submit = submit with { Revision = basket.Revision };
        request = await preparation.SubmitAsync("owner", "library", basket.Id, submit, "purge-race", default);
        var downloadableBeforePurge = (await preparation.ListAsync("owner", "library", default)).First(r => r.State == "partial" && r.Snapshot.PresetId == "originals-v1");
        var purgeMint = new DefaultHttpContext(); purgeMint.User = mint.User; purgeMint.Request.Headers.Authorization = "Bearer purge-fixture-session";
        var purgeGrant = System.Text.Json.JsonSerializer.SerializeToElement(await downloads.MintAsync(purgeMint, "owner", "library", downloadableBeforePurge.Id, default), ExportDatabase.Json);
        var purgeExchange = new DefaultHttpContext(); purgeExchange.Request.Scheme = "https"; purgeExchange.Request.Host = new HostString("downloads.example.test"); purgeExchange.Request.Headers.Origin = "https://web.example.test"; purgeExchange.Request.ContentType = "application/x-www-form-urlencoded";
        var purgeForm = System.Text.Encoding.UTF8.GetBytes("code=" + purgeGrant.GetProperty("code").GetString()); purgeExchange.Request.ContentLength = purgeForm.Length; purgeExchange.Request.Body = new MemoryStream(purgeForm);
        await downloads.ExchangeAsync(purgeExchange, "library", downloadableBeforePurge.Id, default);
        var purgeCookie = purgeExchange.Response.Headers.SetCookie.ToString().Split(';')[0];
        DefaultHttpContext PurgeCookieRequest() { var c = new DefaultHttpContext(); c.Request.Scheme = "https"; c.Request.Host = new HostString("downloads.example.test"); c.Request.Headers.Cookie = purgeCookie; c.Request.Method = "HEAD"; return c; }
        await downloads.StreamAsync(PurgeCookieRequest(), "library", downloadableBeforePurge.Id, default);
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, request.Id, writer);
        var intent = await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default);
        var lateKey = $"exports/{request.Id:N}/{intent:N}/package.zip";
        await invalidation.BeginStorageAsync(intent, writer, "initiate", default);
        var lateUpload = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = lateKey });
        await invalidation.StorageReturnedAsync(intent, writer, lateUpload.ResponseMetadata?.RequestId, lateUpload.UploadId, null, default);
        await invalidation.BeginStorageAsync(intent, writer, "part", default);
        var latePart = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = lateKey, UploadId = lateUpload.UploadId, PartNumber = 1, InputStream = new MemoryStream(originalBytes), PartSize = originalBytes.Length });
        await invalidation.StorageReturnedAsync(intent, writer, latePart.ResponseMetadata?.RequestId, null, null, default);
        await invalidation.BeginStorageAsync(intent, writer, "complete", default);
        var unknown = await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default);
        await invalidation.SettleWriteAsync(unknown, writer, null, false, default);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "platform-exports");
        var workspaceIntent = await invalidation.AdmitWorkspaceAsync("owner", "library", request.Id, 1, writer, workspaceRoot, default);
        var workspacePath = Path.Combine(workspaceRoot, workspaceIntent.ToString("N"));
        Directory.CreateDirectory(workspacePath); await File.WriteAllBytesAsync(Path.Combine(workspacePath, "input"), originalBytes);
        var faultRoot = Path.Combine(workspaceRoot, "fault-" + Guid.NewGuid().ToString("N"));
        var faultWorkspace = await invalidation.AdmitWorkspaceAsync("owner", "library", request.Id, 1, writer, faultRoot, default);
        var faultPath = Path.Combine(faultRoot, faultWorkspace.ToString("N"));
        Directory.CreateDirectory(faultPath); await File.WriteAllBytesAsync(Path.Combine(faultPath, "input"), originalBytes);
        var deadWorkspace = await invalidation.AdmitWorkspaceAsync("owner", "library", request.Id, 1, writer, workspaceRoot, default);
        var deadPath = Path.Combine(workspaceRoot, deadWorkspace.ToString("N"));
        Directory.CreateDirectory(deadPath); await File.WriteAllBytesAsync(Path.Combine(deadPath, "package.zip"), originalBytes);
        using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "-NoProfile -Command Start-Sleep -Seconds 2" : "-c \"sleep 2\"",
            UseShellExecute = false, CreateNoWindow = true,
        })!)
        {
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "UPDATE export_write_intents SET writer_pid=$2,writer_start_ticks=$3 WHERE id=$1", default, deadWorkspace, child.Id, child.StartTime.ToUniversalTime().Ticks);
            await child.WaitForExitAsync();
        }
        var lostCallback = await invalidation.AdmitWriteAsync("owner", "library", request.Id, 1, writer, default);
        var callbackKey = $"exports/{request.Id:N}/{lostCallback:N}/package.zip";
        await invalidation.BeginStorageAsync(lostCallback, writer, "initiate", default);
        var initiated = await s3.InitiateMultipartUploadAsync(new() { BucketName = bucket, Key = callbackKey });
        await invalidation.StorageReturnedAsync(lostCallback, writer, initiated.ResponseMetadata.RequestId, initiated.UploadId, null, default);
        await invalidation.BeginStorageAsync(lostCallback, writer, "part", default);
        var part = await s3.UploadPartAsync(new() { BucketName = bucket, Key = callbackKey, UploadId = initiated.UploadId, PartNumber = 1, InputStream = new MemoryStream(originalBytes), PartSize = originalBytes.Length });
        await invalidation.StorageReturnedAsync(lostCallback, writer, part.ResponseMetadata.RequestId, null, null, default);
        await invalidation.BeginStorageAsync(lostCallback, writer, "complete", default);
        var completedUpload = await s3.CompleteMultipartUploadAsync(new() { BucketName = bucket, Key = callbackKey, UploadId = initiated.UploadId, PartETags = [new(1, part.ETag)] });
        await invalidation.StorageReturnedAsync(lostCallback, writer, completedUpload.ResponseMetadata.RequestId, null, completedUpload.VersionId, default);
        // No SettleWrite callback: production cleanup must recover from the durable provider completion receipt.
        // Completion seals this intent's storage admission in the same transaction, including across host loss.
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
        await RejectAsync(async () => await downloads.StreamAsync(PurgeCookieRequest(), "library", downloadableBeforePurge.Id, default));
        Check((await preparation.GetAsync("owner", "library", downloadableBeforePurge.Id, default)).State == "unavailable", "Consumer tombstone revokes a previously working native grant and fences its completed package");
        Check((await service.GetAsync("owner", "library", basket.Id, default)).Items.All(i => i.Availability == "unavailable" && i.Reason == "permanently_unavailable"), "Consumer tombstone marks saved exact selections permanently unavailable without deleting evidence");
        var late = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = lateKey, UploadId = lateUpload.UploadId, PartETags = [new PartETag(1, latePart.ETag)] });
        await invalidation.StorageReturnedAsync(intent, writer, late.ResponseMetadata?.RequestId, null, late.VersionId, default);
        await RejectAsync(async () => await invalidation.BeginStorageAsync(intent, writer, "part", default));
        await invalidation.SettleWriteAsync(intent, writer, late.VersionId, true, default);
        Check((await preparation.GetAsync("owner", "library", request.Id, default)).State == "unavailable", "Late upload cannot restore invalidated request");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "derivatives/shared/preview.jpg", InputStream = new MemoryStream(originalBytes) });
        if (s3 is FixtureS3 faultStorage)
        {
            faultStorage.FailDeletes = true;
            try { await cleanup.TickAsync(default); throw new InvalidOperationException("Expected provider delete denial"); }
            catch (AmazonS3Exception) { }
            finally { faultStorage.FailDeletes = false; }
            await using var connection = await db.OpenConnectionAsync(); await using var tx = await connection.BeginTransactionAsync();
            Check(!await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Provider deletion failure cannot acknowledge purge cleanup"); await tx.CommitAsync();
        }
        await cleanup.TickAsync(default);
        Check(Directory.Exists(workspacePath), "Live local media writer remains inventoried and blocks cleanup");
        Check(!Directory.Exists(deadPath), "OS-proven terminated local writer workspace recovers without a callback");
        Check((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = callbackKey })).Versions?.Any() != true, "Lost settlement callback is recovered and its exact versions removed");
        await invalidation.StopWriterAsync(workspaceIntent, writer, default); await cleanup.TickAsync(default);
        Check(!Directory.Exists(workspacePath), "Stopped inventoried workspace is verified removed before acknowledgment");
        await invalidation.StopWriterAsync(faultWorkspace, writer, default);
        // Make this owned parent unavailable without deleting its media, exercising a real filesystem inspection error.
        Directory.Move(faultRoot, faultRoot + ".unavailable"); await File.WriteAllTextAsync(faultRoot, "fixture blocked parent");
        var faultConfiguration = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?> { ["Exports:WorkspaceRoot"] = faultRoot }).Build();
        try { await new ExportCleanupService(db, s3, faultConfiguration).TickAsync(default); throw new InvalidOperationException("Expected workspace inspection failure"); }
        catch (IOException) { }
        await using (var connection = await db.OpenConnectionAsync())
        {
            Check(await ScalarAsync(connection, "SELECT 1 FROM export_write_intents WHERE id=$1 AND state<>'cleaned'", default, faultWorkspace) is not null, "Unavailable workspace remains draining");
            await using var tx = await connection.BeginTransactionAsync(); Check(!await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Inaccessible inventoried media cannot acknowledge cleanup"); await tx.CommitAsync();
        }
        File.Delete(faultRoot); Directory.Move(faultRoot + ".unavailable", faultRoot);
        await new ExportCleanupService(db, s3, faultConfiguration).TickAsync(default);
        Check(!Directory.Exists(faultPath), "Restored accessible workspace removes media through production recovery");
        Directory.Delete(faultRoot);
        Check((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = lateKey } )).Versions?.Any() != true, "Late settled object versions are deleted");
        Check((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = "derivatives/shared/" })).Versions.Count == 1, "Export cleanup preserves shared renderer objects");
        await using (var connection = await db.OpenConnectionAsync()) { await using var tx = await connection.BeginTransactionAsync(); Check(!await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Unknown storage outcomes remain draining after settled cleanup"); await tx.CommitAsync(); }
        await invalidation.SettleWriteAsync(unknown, writer, null, true, default); await cleanup.TickAsync(default);
        await using (var connection = await db.OpenConnectionAsync()) { await using var tx = await connection.BeginTransactionAsync(); Check(await invalidation.AcknowledgeAsync(tx, "owner", "library", purge.EventId, default), "Acknowledgment follows proven settlement and exact version cleanup"); await tx.CommitAsync(); }
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "clear", null, false, null, null, default))!;
        Check(basket.Items.Count == 0, "Clear is persistent");
        Check((await preparation.GetAsync("owner", "library", request.Id, default)).Snapshot.Items.Count == request.Snapshot.Items.Count, "Basket changes preserve immutable request snapshot");
        await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "delete", null, false, null, null, default);
        Check((await preparation.GetAsync("owner", "library", request.Id, default)).Snapshot.Items.Count == request.Snapshot.Items.Count, "Basket deletion preserves immutable request evidence");
        await using var hold = db.CreateCommand("SELECT count(*) FROM lifecycle_holds");
        Check((long)(await hold.ExecuteScalarAsync())! == 4, "Basket changes never add or release the four pre-existing history holds");
        Console.WriteLine("Export persistent basket/selection/authorization/revision/hidden/quota checks passed.");
    }
    private static async Task PinnedRenderChecksAsync(NpgsqlDataSource db, ExportPin pin, IAmazonS3 s3, string bucket)
    {
        const string digest = "sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Rendering:DeployedArtifactDigest"] = digest }).Build();
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE rendering_compatibility SET renderer_artifact_digest=$1,corpus_evidence_id='isolated-fixture-not-registry-promotion',approved_at=now(),approved_by='fixture',state='approved' WHERE matrix_version='rendering-v1'", default, digest);
        var queue = new ExportPinnedQueue(); var service = new Platform.Api.Features.Rendering.RenderingService(db, null!, config, queue);
        ExportRenderer renderer; await using (var connection = await db.OpenConnectionAsync()) renderer = await service.ExportCompatibilityAsync(connection, default);
        var first = await service.RequestPinnedAsync("owner", "library", pin, renderer, default);
        var duplicate = await service.RequestPinnedAsync("owner", "library", pin with { ItemId = Guid.NewGuid() }, renderer, default);
        Check(first.RequestId == duplicate.RequestId && queue.Messages.Count == 1, "Equivalent exact pinned renders coalesce across distinct basket selections");
        Check(await service.RedispatchPendingAsync(default) == 1, "Stored pinned rendering supports pending redispatch");
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE render_requests SET state='retryable' WHERE id=$1; UPDATE render_attempts SET state='retryable',failure_code='renderer_timeout',completed_at=now() WHERE request_id=$1", default, first.RequestId);
        await service.RetryAsync("owner", "library", first.RequestId, default);
        Check(queue.Messages.Count == 3 && queue.Messages.All(m => m.Original == pin.Original && m.Xmp == pin.Xmp && m.Request.SelectedVersion == pin.CommitId && m.Request.XmpSha256 == pin.Xmp.Sha256), "Initial dispatch, redispatch and retry preserve exact old XMP/object versions after edit head and refs move");
        config["Rendering:DeployedArtifactDigest"] = "sha256:" + new string('f', 64);
        try { await service.RequestPinnedAsync("owner", "library", pin, renderer, default); throw new InvalidOperationException("Expected changed renderer admission denial"); }
        catch (Platform.Api.Features.Rendering.RenderingProblem problem) { Check(problem.Code == "incompatible_renderer", "Captured renderer compatibility cannot silently change"); }
        config["Rendering:DeployedArtifactDigest"] = digest; config["Ingest:Bucket"] = bucket; config["Rendering:DerivativeBucket"] = bucket; config["Ingest:KmsKeyId"] = "fixture-only";
        // Synthetic JPEG segment framing isolates transform/packaging; the separate native corpus proves real encoding.
        byte[] encoded = [0xff, 0xd8, 0xff, 0xe1, 0, 8, 71, 80, 83, 0, 1, 2, 0xff, 0xda, 0, 2, 12, 0xff, 0, 15, 0xff, 0xd9];
        var queued = queue.Messages[^1]; var output = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = queued.DerivativeKey, InputStream = new MemoryStream(encoded) });
        var callbacks = new Platform.Api.Features.Rendering.RenderWorkerCallbacks(db);
        Check(await callbacks.ClaimAsync(queued.RequestId, queued.AttemptId), "Pinned callback claims its current retry attempt");
        var provenance = new Platform.Contracts.Rendering.RenderProvenance(pin.Original.Sha256, pin.Xmp.Sha256, pin.CommitId, renderer.Release, renderer.Digest, renderer.Profile, renderer.Matrix, DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        try { await callbacks.CompleteAsync(queued.RequestId, queued.AttemptId, queued.DerivativeKey, output.VersionId, ExportIntegrity.Hash(encoded), encoded.Length, "image/jpeg", provenance with { XmpSha256 = new string('e', 64) }); throw new InvalidOperationException("Expected mismatched callback provenance denial"); }
        catch (Platform.Api.Features.Rendering.RenderingProblem problem) { Check(problem.Code == "render_provenance_mismatch", "Mismatched XMP callback cannot publish a rendition"); }
        await callbacks.CompleteAsync(queued.RequestId, queued.AttemptId, queued.DerivativeKey, output.VersionId, ExportIntegrity.Hash(encoded), encoded.Length, "image/jpeg", provenance);
        var selections = new ExportSelectionService(db); var baskets = new ExportBasketService(db, selections); var preparations = new ExportPreparationService(db, baskets, service);
        var basket = await baskets.CreateAsync("owner", "library", "Rendered fixture", default);
        for (var i = 0; i < 2; i++) basket = (await baskets.MutateAsync("owner", "library", basket.Id, basket.Revision, "add", null, false, new(pin.AssetId, pin.EditId, pin.CommitId, true), null, default))!;
        await RejectAsync(async () => await preparations.SubmitAsync("owner", "library", basket.Id, new("jpeg-2048-retain-v1", basket.Revision, false), "no-consent", default));
        var packages = new ExportPackageService(db, s3, config, service, selections, new ExportInvalidationService(db));
        foreach (var mode in new[] { "private", "retain", "mixed", "incompatible" })
        {
            var retained = mode == "retain";
            var request = await preparations.SubmitAsync("owner", "library", basket.Id, new(retained ? "jpeg-2048-retain-v1" : "jpeg-2048-private-v1", basket.Revision, retained), "rendered-" + mode, default);
            var writer = Guid.NewGuid(); await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, request.Id, writer);
            if (mode == "incompatible") config["Rendering:DeployedArtifactDigest"] = "sha256:" + new string('f', 64);
            if (mode == "mixed" && s3 is FixtureS3 faults) faults.CorruptNextRead = true;
            Check(await packages.PrepareAsync("owner", "library", request.Id, writer, 1, default), "Rendered package preparation completes");
            var result = await preparations.GetAsync("owner", "library", request.Id, default);
            if (mode == "incompatible") { config["Rendering:DeployedArtifactDigest"] = digest; Check(result.State == "failed" && result.PackageSha256 is null && result.Outcomes.All(o => o.Reason == "incompatible_renderer"), "Changed/unavailable captured renderer records all failures without publishing a package or substituting another renderer"); continue; }
            var expected = JpegMetadata.Transform(encoded, retained);
            Check(result.State == (mode == "mixed" ? "partial" : "completed") && result.Outcomes.Where(o => o.Status == "completed").All(o => o.Render == provenance && o.Transform?.InputSha256 == ExportIntegrity.Hash(encoded) && o.Transform.OutputSha256 == ExportIntegrity.Hash(expected) && o.Sha256 == ExportIntegrity.Hash(expected)), "Rendered package captures exact source/render/transform provenance and verified output hashes");
            if (mode == "mixed") Check(result.Outcomes.Count(o => o.Reason == "integrity_error") == 1, "Corrupt rendered output remains a failed item");
            string key; string version; await using (var connection = await db.OpenConnectionAsync()) { key = (string)(await ScalarAsync(connection, "SELECT package_key FROM export_requests WHERE id=$1", default, request.Id))!; version = (string)(await ScalarAsync(connection, "SELECT package_version FROM export_requests WHERE id=$1", default, request.Id))!; }
            using var stored = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = version }); using var bytes = new MemoryStream(); await stored.ResponseStream.CopyToAsync(bytes);
            Check(ExportIntegrity.Hash(bytes.ToArray()) == result.PackageSha256, "Rendered ZIP checksum matches exact stored version");
            using var zip = new ZipArchive(new MemoryStream(bytes.ToArray()));
            foreach (var item in result.Outcomes.Where(o => o.Status == "completed")) { using var actual = new MemoryStream(); await zip.GetEntry(item.Filename!)!.Open().CopyToAsync(actual); Check(actual.ToArray().SequenceEqual(expected), "Private strips descriptive segments; consented retain preserves encoded bytes inside ZIP"); }
        }
        await baskets.MutateAsync("owner", "library", basket.Id, basket.Revision, "delete", null, false, null, null, default);
        Console.WriteLine("Pinned render admission/coalescing/redispatch/retry checks passed; isolated fixture approval is not trusted registry promotion.");
    }
    private static async Task UnknownInitiationChecksAsync(NpgsqlDataSource db, IAmazonS3 s3, string bucket)
    {
        const string library = "unknown-initiation-fixture";
        byte[] bytes = [5, 6, 7, 8]; var sha = ExportIntegrity.Hash(bytes); var xmpSha = ExportIntegrity.Hash([8, 7, 6, 5]);
        var version = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "original/unknown", InputStream = new MemoryStream(bytes) })).VersionId;
        var asset = Guid.NewGuid(); var edit = Guid.NewGuid();
        var manifest = new VersionControlManifest(library, [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "unknown initiation", [new(asset, edit, "added", null, xmpSha, "photos/unknown.raw", "active")]);
        var canonical = VersionControlManifestCodec.Write(manifest); var commit = VersionControlManifestCodec.Digest(canonical);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,4,'original','original/unknown',$2,'authoritative'),($3,4,'xmp','xmp/unknown','fixture','authoritative'); INSERT INTO assets(id,owner_id,original_sha256,state) VALUES($4,'owner',$1,'active'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($5,$4,0,$3,'fixture'); INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($6,'owner',$7,'owner',$8,$8,'unknown initiation',1,1,$9); INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES('owner',$7,'main','main','branch',$6); INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($10,$4,'history',$11)", default, sha, version, xmpSha, asset, edit, commit, library, DateTimeOffset.UnixEpoch, canonical, Guid.NewGuid(), "vc:" + commit);
        }
        var selections = new ExportSelectionService(db); var baskets = new ExportBasketService(db, selections); var preparations = new ExportPreparationService(db, baskets);
        var basket = (await baskets.ListAsync("owner", library, default)).Single();
        basket = (await baskets.MutateAsync("owner", library, basket.Id, basket.Revision, "add", null, false, new(asset, edit, "current"), null, default))!;
        var request = await preparations.SubmitAsync("owner", library, basket.Id, new("originals-v1", basket.Revision, true), "unknown-initiation", default);
        var writer = Guid.NewGuid();
        await using (var connection = await db.OpenConnectionAsync()) await ExecuteAsync(connection, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", default, request.Id, writer);
        var journal = new ExportInvalidationService(db); var intent = await journal.AdmitWriteAsync("owner", library, request.Id, 1, writer, default);
        var key = $"exports/{request.Id:N}/{intent:N}/package.zip";
        await journal.BeginStorageAsync(intent, writer, "initiate", default);
        var actual = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        // The provider succeeded, but its callback never reached the durable journal.
        await journal.SettleWriteAsync(intent, writer, null, false, default); await journal.StopWriterAsync(intent, writer, default);
        var purge = new ExportPurgeEvent(Guid.NewGuid(), "owner", library, asset, null, "original", sha, 1, Guid.NewGuid());
        await using (var connection = await db.OpenConnectionAsync()) { await using var tx = await connection.BeginTransactionAsync(); await journal.ApplyAsync(tx, purge, default); await tx.CommitAsync(); }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Rendering:DerivativeBucket"] = bucket }).Build();
        await new ExportCleanupService(db, s3, config).TickAsync(default);
        await RejectAsync(async () => await journal.BeginStorageAsync(intent, writer, "part", default));
        await using (var connection = await db.OpenConnectionAsync())
        {
            Check(await ScalarAsync(connection, "SELECT 1 FROM export_write_intents WHERE id=$1 AND state='unknown' AND storage_pending AND writer_stopped", default, intent) is not null, "Stopped writer cannot settle unknown multipart initiation from process/timeout inference");
            await using var tx = await connection.BeginTransactionAsync(); Check(!await journal.AcknowledgeAsync(tx, "owner", library, purge.EventId, default), "Actual unknown provider initiation withholds purge acknowledgment"); await tx.CommitAsync();
        }
        Check((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key })).MultipartUploads.Any(u => u.UploadId == actual.UploadId), "Unknown multipart remains inventoried and draining until positive external evidence exists");
        Console.WriteLine("Actual callback-lost multipart initiation remains draining; fixture teardown is not production settlement evidence.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (ExportProblem) { return; } throw new InvalidOperationException("Expected rejection"); }}
// MinIO fixture omits unsupported AWS KMS headers; production service still requires AWS KMS.
public sealed class FixtureS3() : AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"), new AmazonS3Config { ServiceURL = Environment.GetEnvironmentVariable("EXPORT_S3_ENDPOINT") ?? "http://127.0.0.1:9000", ForcePathStyle = true, MaxErrorRetry = 0 })
{
    public bool FailDeletes { get; set; }
    public bool CorruptNextRead { get; set; }
    public bool FailNextRead { get; set; }
    public bool FailAllReads { get; set; }
    public override async Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (FailNextRead || FailAllReads) { FailNextRead = false; throw new AmazonS3Exception("fixture exact object missing") { StatusCode = System.Net.HttpStatusCode.NotFound }; }
        var response = await base.GetObjectAsync(request, cancellationToken);
        if (CorruptNextRead) { CorruptNextRead = false; response.ResponseStream.Dispose(); response.ResponseStream = new MemoryStream([9, 9, 9, 9]); }
        return response;
    }
    public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default) => FailDeletes ? Task.FromException<DeleteObjectResponse>(new AmazonS3Exception("fixture deletion unavailable") { StatusCode = System.Net.HttpStatusCode.Forbidden }) : base.DeleteObjectAsync(request, cancellationToken);
    public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
    { request.ServerSideEncryptionMethod = null; request.ServerSideEncryptionKeyManagementServiceKeyId = null; return base.InitiateMultipartUploadAsync(request, cancellationToken); }
}

sealed class ExpiringExportSink(Func<Task> expire) : MemoryStream
{
    public int LargestWrite { get; private set; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        LargestWrite = Math.Max(LargestWrite, buffer.Length);
        await base.WriteAsync(buffer, cancellationToken);
        if (Length == buffer.Length) await expire();
    }
}

sealed class ExportPinnedQueue : Platform.Contracts.Rendering.IRenderQueueDispatcher
{
    public List<Platform.Contracts.Rendering.RenderQueueMessage> Messages { get; } = [];
    public Task EnqueueAsync(Platform.Contracts.Rendering.RenderQueueMessage message, CancellationToken cancellationToken = default) { message.Validate(); Messages.Add(message); return Task.CompletedTask; }
}
