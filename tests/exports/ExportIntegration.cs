using Npgsql;
using Platform.Contracts.Exports;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.Exports;
using static Platform.Storage.IngestDb;
using Amazon.S3;
using Amazon.Runtime;
using Amazon.S3.Model;
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
            await BasketChecksAsync(db);
            using var s3 = new AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"), new AmazonS3Config { ServiceURL = Environment.GetEnvironmentVariable("EXPORT_S3_ENDPOINT") ?? "http://127.0.0.1:9000", ForcePathStyle = true });
            await s3.ListBucketsAsync(new ListBucketsRequest());
            Console.WriteLine("Export PostgreSQL migration and MinIO connectivity checks passed.");
        }
        finally { await using var cleanup = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
    private static async Task BasketChecksAsync(NpgsqlDataSource db)
    {
        var selection = new ExportSelectionService(db); var service = new ExportBasketService(db, selection);
        var basket = (await service.ListAsync("owner", "library", default)).Single();
        var asset = Guid.NewGuid(); var edit = Guid.NewGuid(); var sourceSha = new string('a', 64); var xmpSha = new string('b', 64);
        var manifest = new VersionControlManifest("library", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "root", [new(asset, edit, "added", null, xmpSha, "photos/CON.CR2", "active")]);
        var bytes = VersionControlManifestCodec.Write(manifest); var commit = VersionControlManifestCodec.Digest(bytes);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,4,'original','original/a','v1','authoritative'),($2,4,'xmp','xmp/b','v1','authoritative'); INSERT INTO assets(id,owner_id,original_sha256,state) VALUES($3,'owner',$1,'active'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($4,$3,0,$2,'fixture'); INSERT INTO asset_paths(id,owner_id,asset_id,display_path,collision_key,active,effective_from) VALUES($5,'owner',$3,'photos/CON.CR2','photos/con.cr2',true,now())", default, sourceSha, xmpSha, asset, edit, Guid.NewGuid());
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner','library','owner',$2,$2,'root',1,1,$3); INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES('owner','library','main','main','branch',$1); INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($4,$5,'history',$6)", default, commit, DateTimeOffset.UnixEpoch, bytes, Guid.NewGuid(), asset, "vc:" + commit);
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
        basket = (await service.MutateAsync("owner", "library", basket.Id, basket.Revision, "clear", null, false, null, null, default))!;
        Check(basket.Items.Count == 0, "Clear is persistent");
        await using var hold = db.CreateCommand("SELECT count(*) FROM lifecycle_holds");
        Check((long)(await hold.ExecuteScalarAsync())! == 1, "Basket changes never add or release lifecycle holds");
        Console.WriteLine("Export persistent basket/selection/authorization/revision/hidden/quota checks passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (ExportProblem) { return; } throw new InvalidOperationException("Expected rejection"); }}
