using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Platform.Api.Features.Gallery.Browse;
using static Platform.Storage.IngestDb;

internal static class GalleryCapacityChecks
{
    public static async Task RunAsync(NpgsqlDataSource db)
    {
        const string owner = "gallery-capacity-owner", library = "capacity", branch = "main";
        await using var connection = await db.OpenConnectionAsync();
        var sha = ((string?)await ScalarAsync(connection,
            "SELECT c.sha256 FROM content_objects c JOIN assets a ON a.original_sha256=c.sha256 WHERE c.integrity_state='authoritative' AND a.state IN ('active','restored') LIMIT 1", default))?.Trim()
            ?? throw new InvalidOperationException("Capacity benchmark requires an authoritative original.");
        await ExecuteAsync(connection, "CREATE TEMP TABLE gallery_capacity_ids AS SELECT gen_random_uuid() id, n FROM generate_series(1,100000) n", default);
        // The disposable superuser fixture bypasses per-row ingest invalidation triggers only while seeding.
        await ExecuteAsync(connection, "SET session_replication_role = replica", default);
        await LongAsync(connection, "INSERT INTO assets(id,owner_id,original_sha256,state,format) SELECT id,$1,$2,'active','dng' FROM gallery_capacity_ids", owner, sha);
        await ExecuteAsync(connection, "INSERT INTO gallery_generations(owner_id,library_id,branch_name,generation,schema_version,state) VALUES($1,$2,$3,1,1,'active')", default, owner, library, branch);
        await LongAsync(connection, """
            INSERT INTO gallery_projection(owner_id,library_id,branch_name,generation,asset_id,original_sha256,projection_state,display_path,filename,file_type,captured_at,rating,width,height)
            SELECT $1,$2,$3,1,id,$4,'complete','capacity/'||n||'.dng',n||'.dng','dng',
                   timestamptz '2024-01-01' + (n||' seconds')::interval,n%6,640,480 FROM gallery_capacity_ids
            """, owner, library, branch, sha);
        await ExecuteAsync(connection, "SET session_replication_role = origin", default);
        await ExecuteAsync(connection, "ANALYZE assets; ANALYZE asset_paths; ANALYZE gallery_projection", default);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gallery:LibraryId"] = library }).Build();
        var browse = new GalleryBrowseService(db, new EphemeralDataProtectionProvider(), configuration);
        var query = new QueryCollection(new Dictionary<string, StringValues> { ["sort"] = "captureDate", ["limit"] = "40", ["rating"] = "4" });
        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var timer = Stopwatch.StartNew();
            var page = await browse.BrowseAsync(owner, library, query, default);
            timer.Stop();
            if (page.Count != 16667 || page.Cards.Count != 40) throw new InvalidOperationException("Capacity benchmark result mismatch.");
            if (i >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Console.WriteLine($"Gallery 100,000-asset server browse p95: {p95:F1} ms (25 measured runs; first 5 warmups; 40-card filtered page).");
        if (p95 >= 2000) throw new InvalidOperationException("Gallery 100,000-asset server browse p95 exceeded two seconds.");
    }

    private static async Task LongAsync(NpgsqlConnection connection, string sql, params object[] args)
    {
        await using var command = Command(connection, sql, args);
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }
}
