using System.Diagnostics;
using Npgsql;
using Platform.Api.Features.Recovery;
using static Platform.Storage.IngestDb;

internal static class RecoveryHealthCapacityChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, RecoveryHealthService health, string owner)
    {
        // Database-only capacity fixture: these rows are not byte-verification or cloud restore evidence.
        // The enclosing integration runner owns a disposable schema and destroys it after this check.
        await using var connection = await db.OpenConnectionAsync();
        var original = ((string?)await ScalarAsync(connection,
            "SELECT original_sha256 FROM assets WHERE owner_id=$1 AND original_sha256 IS NOT NULL LIMIT 1",
            default, owner))?.Trim() ?? throw new InvalidOperationException("Recovery capacity fixture needs an existing original.");
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "SET LOCAL session_replication_role = replica", default);
            await LongAsync(connection, "CREATE TEMP TABLE recovery_capacity_assets AS SELECT gen_random_uuid() id, n FROM generate_series(1,100000) n");
            await LongAsync(connection, """
                INSERT INTO assets(id,owner_id,original_sha256,state)
                SELECT id,$1,$2,'active' FROM recovery_capacity_assets
                """, owner, original);
            await LongAsync(connection, """
                CREATE TEMP TABLE recovery_capacity_xmp AS
                SELECT n, md5('recovery-capacity-a-'||n)||md5('recovery-capacity-b-'||n) sha
                FROM generate_series(1,1000000) n
                """);
            await LongAsync(connection, """
                INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
                SELECT sha,0,'xmp','sha256/'||sha,'database-only-capacity','authoritative'
                FROM recovery_capacity_xmp
                """);
            await LongAsync(connection, """
                INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance)
                SELECT gen_random_uuid(),a.id,(x.n-1)%10,x.sha,'database-only-capacity'
                FROM recovery_capacity_xmp x
                JOIN recovery_capacity_assets a ON a.n=(x.n-1)/10+1
                """);
            await transaction.CommitAsync();
        }
        await LongAsync(connection, "ANALYZE assets; ANALYZE content_objects; ANALYZE edit_identities; ANALYZE recovery_points; ANALYZE recovery_observations");
        var images = (long)(await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE owner_id=$1", default, owner))!;
        var records = (long)(await ScalarAsync(connection, "SELECT count(*) FROM content_objects", default))!;
        if (images < 100_000 || records < 1_000_000)
            throw new InvalidOperationException("Recovery capacity fixture is below the approved database row envelope.");

        var samples = new List<double>();
        for (var index = 0; index < 30; index++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var timer = Stopwatch.StartNew();
            var result = await health.GetAsync(owner, timeout.Token);
            timer.Stop();
            if (result.Status != "healthy" || result.Classes.Count != 7)
                throw new InvalidOperationException("Recovery capacity query returned an unexpected health result.");
            if (index >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Console.WriteLine($"Recovery health database capacity p95: {p95:F1} ms; {images} assets, {records} content records; 25 measured queries after 5 warmups. Database-only fixture; no cloud/restore throughput claim.");
        if (p95 > 5000) throw new InvalidOperationException("Recovery health p95 exceeded five seconds.");
    }

    private static async Task LongAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }
}
