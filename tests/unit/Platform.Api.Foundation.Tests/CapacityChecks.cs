using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using Platform.Storage;
using static Platform.Storage.IngestDb;
using static IngestChecks;

internal static class CapacityChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        if (Environment.GetEnvironmentVariable("INGEST_CAPACITY") != "1") return;
        var bytes = File.ReadAllBytes("tests/fixtures/ingest/sample.png");
        var sha = Hash(bytes); var xmp = Hash(File.ReadAllBytes("tests/fixtures/ingest/sample.dng.xmp"));
        var imports = new ImportService(db, storage);
        var timer = Stopwatch.StartNew();
        var batch = await imports.CreateAsync("capacity-owner", Guid.NewGuid().ToString("N"), "op_capacity", new(false, Enumerable.Range(0, 1000).Select(i => new ImportItem($"batch/{i}.png", sha, bytes.Length)).ToArray()), default);
        var progressMs = timer.Elapsed.TotalMilliseconds;
        Check(batch.Items.Count == 1000 && progressMs < 2000, "1000-item progress must be visible within two seconds");
        Console.WriteLine($"Capacity: 1000-item creation and status {progressMs:F1} ms.");
        await using var connection = await db.OpenConnectionAsync();
        timer.Restart();
        await using (var seed = Command(connection, """
            INSERT INTO assets(id,owner_id,original_sha256,state,format)
            SELECT md5('capacity-asset-'||n)::uuid,'capacity-owner',$1,'active','png' FROM generate_series(1,100000) n;
            INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active)
            SELECT md5('capacity-path-'||n)::uuid,md5('capacity-asset-'||n)::uuid,'capacity-owner','capacity/'||n||'.png','CAPACITY/'||n||'.PNG',true FROM generate_series(1,100000) n;
            INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance)
            SELECT md5('capacity-edit-'||n)::uuid,md5('capacity-asset-'||((n-1)/10+1))::uuid,(n-1)%10,$2,'capacity-fixture' FROM generate_series(1,1000000) n;
            ANALYZE assets; ANALYZE asset_paths; ANALYZE edit_identities;
            """, sha, xmp))
        {
            seed.CommandTimeout = 300;
            await seed.ExecuteNonQueryAsync();
        }
        var seedMs = timer.Elapsed.TotalMilliseconds;
        var assets = new AssetService(db, storage);
        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            timer.Restart();
            var page = await assets.ListAsync("capacity-owner", false, null, default);
            await using var file = (await assets.DownloadAsync("capacity-owner", page[0].Id, false, "op_capacity", default)).Bytes;
            times.Add(timer.Elapsed.TotalMilliseconds);
        }
        var ids = new List<Guid>();
        await using (var query = Command(connection, "SELECT id FROM assets WHERE owner_id='capacity-owner' ORDER BY id LIMIT 10000"))
        await using (var r = await query.ExecuteReaderAsync()) while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        timer.Restart();
        for (var i = 0; i < ids.Count; i++)
            await assets.ChangeAsync("capacity-owner", ids[i], false, "move", 1, $"Moved/{i}.png", "capacity-move-" + i, "op_capacity", default);
        var movesMs = timer.Elapsed.TotalMilliseconds;
        var moved = (long)(await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE owner_id='capacity-owner' AND lifecycle_version=2", default))!;
        var associated = (long)(await ScalarAsync(connection, "SELECT count(*) FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE a.owner_id='capacity-owner'", default))!;
        Check(moved == 10000 && associated == 1000000, "10k moves preserve all 1m edit relationships");
        var p95 = times.Order().ElementAt(18);
        Check(p95 < 2000, "Indexed listing and verified retrieval stays within two seconds at capacity");
        var evidence = new { assets = 100000, edits = associated, moves = moved, batchProgressMs = progressMs, seedMs, movesMs, retrievalP95Ms = p95, uniqueSourceBytes = bytes.Length, uniqueSourceObjects = 1 };
        Directory.CreateDirectory("artifacts");
        await File.WriteAllTextAsync("artifacts/ingest-capacity.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(evidence));
    }
}
