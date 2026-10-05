using Npgsql;
using Platform.Storage;

internal static class RecoveryChangeLedgerChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage, string connectionString)
    {
        var owner = "ledger-test-" + Guid.NewGuid().ToString("N");
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var firstSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("ledger-first"u8));
        var secondSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("ledger-second"u8));
        await using (var connection = await db.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var content = new NpgsqlCommand("""
                INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
                VALUES ($1,12,'original','sha256/'||$1,'ledger-version','authoritative'),
                       ($2,13,'original','sha256/'||$2,'ledger-version','authoritative')
                """, connection, transaction))
            {
                content.Parameters.AddWithValue(firstSha);
                content.Parameters.AddWithValue(secondSha);
                await content.ExecuteNonQueryAsync();
            }
            await using (var assets = new NpgsqlCommand("""
                INSERT INTO assets(id,owner_id,original_sha256,state,format) VALUES
                    ($1,$3,$4,'active','raw'),($2,$3,$5,'active','raw')
                """, connection, transaction))
            {
                assets.Parameters.AddWithValue(firstAsset);
                assets.Parameters.AddWithValue(secondAsset);
                assets.Parameters.AddWithValue(owner);
                assets.Parameters.AddWithValue(firstSha);
                assets.Parameters.AddWithValue(secondSha);
                await assets.ExecuteNonQueryAsync();
            }
            await using (var paths = new NpgsqlCommand("""
                INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active) VALUES
                    ($1,$3,$4,'ledger/first.dng','ledger/first.dng',true),
                    ($2,$5,$4,'ledger/second.dng','ledger/second.dng',true)
                """, connection, transaction))
            {
                paths.Parameters.AddWithValue(Guid.NewGuid());
                paths.Parameters.AddWithValue(Guid.NewGuid());
                paths.Parameters.AddWithValue(firstAsset);
                paths.Parameters.AddWithValue(owner);
                paths.Parameters.AddWithValue(secondAsset);
                await paths.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        var trackingApplication = "recovery-ledger-" + Guid.NewGuid().ToString("N");
        var trackedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = trackingApplication };
        await using var trackedDb = NpgsqlDataSource.Create(trackedConnectionString.ConnectionString);
        var service = new AssetService(trackedDb, storage);
        await service.ChangeAsync(owner, firstAsset, false, "move", 1, "ledger/first-moved.dng",
            "ledger-idempotency-1", "shared-correlation", default);
        Check(await WatermarkAsync(db, owner) == 1 && await LedgerCountAsync(db, owner) == 1,
            "An accepted lifecycle mutation and its commit-ordered ledger entry commit together.");

        await service.ChangeAsync(owner, firstAsset, false, "move", 1, "ledger/first-moved.dng",
            "ledger-idempotency-1", "shared-correlation", default);
        Check(await WatermarkAsync(db, owner) == 1 && await LedgerCountAsync(db, owner) == 1,
            "Application idempotency suppresses a replay without advancing the change watermark.");

        await using (var trigger = db.CreateCommand("""
            CREATE FUNCTION recovery_ledger_test_reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'recovery_ledger_test_rollback'; END; $$;
            CREATE TRIGGER recovery_ledger_test_reject_mutation BEFORE INSERT ON asset_mutations
                FOR EACH ROW EXECUTE FUNCTION recovery_ledger_test_reject_mutation();
            """))
            await trigger.ExecuteNonQueryAsync();
        try
        {
            try
            {
                await service.ChangeAsync(owner, secondAsset, false, "move", 1, "ledger/second-failed.dng",
                    "ledger-idempotency-rollback", "shared-correlation", default);
                throw new InvalidOperationException("Expected the fixture trigger to abort the accepted mutation.");
            }
            catch (PostgresException error) when (error.MessageText == "recovery_ledger_test_rollback") { }
        }
        finally
        {
            await using var drop = db.CreateCommand("DROP TRIGGER IF EXISTS recovery_ledger_test_reject_mutation ON asset_mutations; DROP FUNCTION IF EXISTS recovery_ledger_test_reject_mutation()");
            await drop.ExecuteNonQueryAsync();
        }
        Check(await WatermarkAsync(db, owner) == 1 && await LedgerCountAsync(db, owner) == 1 &&
            (string?)await ScalarAsync(db, "SELECT display_path FROM asset_paths WHERE asset_id=$1 AND active", secondAsset) == "ledger/second.dng",
            "A downstream database failure rolls back the source mutation, audit row and ledger sequence.");

        await using var blockerConnection = await db.OpenConnectionAsync();
        await using var blocker = await blockerConnection.BeginTransactionAsync();
        await using (var lockWatermark = new NpgsqlCommand("SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id='main' FOR UPDATE", blockerConnection, blocker))
        {
            lockWatermark.Parameters.AddWithValue(owner);
            _ = await lockWatermark.ExecuteScalarAsync();
        }
        var firstWrite = service.ChangeAsync(owner, firstAsset, false, "move", 2, "ledger/first-concurrent.dng",
            "ledger-idempotency-2", "shared-correlation", default);
        await WaitForWaitersAsync(db, trackingApplication, 1, ledgerQueryOnly: true);
        Check(await WatermarkAsync(db, owner) == 1 && await LedgerCountAsync(db, owner) == 1 &&
            (string?)await ScalarAsync(db, "SELECT display_path FROM asset_paths WHERE asset_id=$1 AND active", firstAsset) == "ledger/first-moved.dng",
            "A concurrent reader sees only the committed prefix while the source mutation waits on its watermark lock.");
        var secondWrite = service.ChangeAsync(owner, secondAsset, false, "move", 1, "ledger/second-concurrent.dng",
            "ledger-idempotency-3", "shared-correlation", default);
        await WaitForWaitersAsync(db, trackingApplication, 2, ledgerQueryOnly: false);
        Check(!firstWrite.IsCompleted && !secondWrite.IsCompleted,
            "Both concurrent source writes are observably waiting on PostgreSQL locks before release.");
        await blocker.CommitAsync();
        await Task.WhenAll(firstWrite, secondWrite);
        var order = await LedgerEntitiesAsync(db, owner);
        Check(await WatermarkAsync(db, owner) == 3 && order.SequenceEqual([
                (1L, firstAsset.ToString("D")), (2L, firstAsset.ToString("D")), (3L, secondAsset.ToString("D"))]),
            "Concurrent source mutations receive contiguous ledger order matching their serialized database commits.");
    }

    private static async Task<long> WatermarkAsync(NpgsqlDataSource db, string owner) =>
        Convert.ToInt64(await ScalarAsync(db, "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id='main'", owner));

    private static async Task<long> LedgerCountAsync(NpgsqlDataSource db, string owner) =>
        Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM recovery_change_ledger WHERE owner_id=$1 AND library_id='main'", owner));

    private static async Task<(long Sequence, string EntityId)[]> LedgerEntitiesAsync(NpgsqlDataSource db, string owner)
    {
        await using var command = db.CreateCommand("SELECT sequence,entity_id FROM recovery_change_ledger WHERE owner_id=$1 AND library_id='main' ORDER BY sequence");
        command.Parameters.AddWithValue(owner);
        var entities = new List<(long Sequence, string EntityId)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) entities.Add((reader.GetInt64(0), reader.GetString(1)));
        return entities.ToArray();
    }

    private static async Task WaitForWaitersAsync(NpgsqlDataSource inspector, string applicationName, long expected, bool ledgerQueryOnly)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var sql = ledgerQueryOnly
                ? "SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock' AND query LIKE '%recovery_change_watermarks%'"
                : "SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock'";
            await using var command = inspector.CreateCommand(sql);
            command.Parameters.AddWithValue(applicationName);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) >= expected) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Concurrent ledger test could not observe the expected PostgreSQL lock wait.");
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource db, string sql, params object?[] values)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
