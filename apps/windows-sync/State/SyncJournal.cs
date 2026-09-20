using Microsoft.Data.Sqlite;

namespace Platform.WindowsSync.State;

public enum DeviceHealth { Online, Offline, AuthenticationRequired, Revoked, Error }
public enum SyncItemState { OnlineOnly, Hydrating, Available, Pinned, Dirty, Uploading, Synchronized, Paused, QuotaError, Error, Conflicted }
public enum JournalOperationKind { Upload, Move, Delete, Restore }

public sealed record SyncDevice(string DeviceId, string LibraryId, string Branch, string? Cursor, DeviceHealth Health, bool Revoked, long Version = 0);
public sealed record SyncItem(string Identity, string LogicalPath, string LocalPath, string Sha256, long Length, SyncItemState State, bool Pinned, string? BaseSha256, long LocalSequence, long AssetVersion = 1, long? EditVersion = null);
public sealed record JournalOperation(long Sequence, string IdempotencyKey, string Identity, JournalOperationKind Kind, string? BaseSha256, string? CurrentSha256, string? Destination, long ExpectedVersion, bool Acknowledged, int Attempts);
public sealed record SyncConflict(Guid Id, string Identity, string? BaseSha256, string LocalSha256, string RemoteSha256, string PreservedPath, string Reason, bool Resolved);

public sealed class SyncJournal : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SyncJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken);
        var version = Convert.ToInt32(await ScalarAsync("PRAGMA user_version;", cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (version == 0) await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS device(singleton INTEGER PRIMARY KEY CHECK(singleton=1), device_id TEXT NOT NULL, library_id TEXT NOT NULL, branch TEXT NOT NULL, cursor TEXT, health INTEGER NOT NULL, revoked INTEGER NOT NULL, version INTEGER NOT NULL DEFAULT 0, schema_version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS items(identity TEXT PRIMARY KEY, logical_path TEXT NOT NULL, local_path TEXT NOT NULL, sha256 TEXT NOT NULL, length INTEGER NOT NULL, state INTEGER NOT NULL, pinned INTEGER NOT NULL, base_sha256 TEXT, local_sequence INTEGER NOT NULL DEFAULT 0, asset_version INTEGER NOT NULL DEFAULT 1, edit_version INTEGER, collision_key TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS operations(sequence INTEGER PRIMARY KEY AUTOINCREMENT, idempotency_key TEXT NOT NULL UNIQUE, identity TEXT NOT NULL, kind INTEGER NOT NULL, base_sha256 TEXT, current_sha256 TEXT, destination TEXT, expected_version INTEGER NOT NULL DEFAULT 0, acknowledged INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS conflicts(id TEXT PRIMARY KEY, identity TEXT NOT NULL, base_sha256 TEXT, local_sha256 TEXT NOT NULL, remote_sha256 TEXT NOT NULL, preserved_path TEXT NOT NULL, reason TEXT NOT NULL, resolved INTEGER NOT NULL DEFAULT 0);
            PRAGMA user_version=4;
            """, cancellationToken);
        else if (version == 1) await ExecuteAsync("ALTER TABLE operations ADD COLUMN expected_version INTEGER NOT NULL DEFAULT 0; ALTER TABLE device ADD COLUMN version INTEGER NOT NULL DEFAULT 0; ALTER TABLE items ADD COLUMN asset_version INTEGER NOT NULL DEFAULT 1; ALTER TABLE items ADD COLUMN edit_version INTEGER; PRAGMA user_version=4;", cancellationToken);
        else if (version == 2) await ExecuteAsync("ALTER TABLE device ADD COLUMN version INTEGER NOT NULL DEFAULT 0; ALTER TABLE items ADD COLUMN asset_version INTEGER NOT NULL DEFAULT 1; ALTER TABLE items ADD COLUMN edit_version INTEGER; PRAGMA user_version=4;", cancellationToken);
        else if (version == 3) await ExecuteAsync("ALTER TABLE items ADD COLUMN asset_version INTEGER NOT NULL DEFAULT 1; ALTER TABLE items ADD COLUMN edit_version INTEGER; PRAGMA user_version=4;", cancellationToken);
        else if (version != 4) throw new InvalidDataException($"unsupported_journal_version:{version}");
    }

    public Task SaveDeviceAsync(SyncDevice value, CancellationToken cancellationToken = default) => ExecuteAsync("""
        INSERT INTO device(singleton,device_id,library_id,branch,cursor,health,revoked,version) VALUES(1,$device,$library,$branch,$cursor,$health,$revoked,$version)
        ON CONFLICT(singleton) DO UPDATE SET device_id=excluded.device_id,library_id=excluded.library_id,branch=excluded.branch,cursor=excluded.cursor,health=excluded.health,revoked=excluded.revoked,version=excluded.version
        """, cancellationToken, ("$device", value.DeviceId), ("$library", value.LibraryId), ("$branch", value.Branch), ("$cursor", value.Cursor), ("$health", (int)value.Health), ("$revoked", value.Revoked ? 1 : 0), ("$version", value.Version));

    public async Task<SyncDevice?> GetDeviceAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT device_id,library_id,branch,cursor,health,revoked,version FROM device WHERE singleton=1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), (DeviceHealth)reader.GetInt32(4), reader.GetBoolean(5), reader.GetInt64(6)) : null;
        }
        finally { gate.Release(); }
    }

    public Task SaveItemAsync(SyncItem value, CancellationToken cancellationToken = default) => ExecuteAsync("""
        INSERT INTO items(identity,logical_path,local_path,sha256,length,state,pinned,base_sha256,local_sequence,asset_version,edit_version,collision_key) VALUES($identity,$logical,$local,$sha,$length,$state,$pinned,$base,$sequence,$assetVersion,$editVersion,$collision)
        ON CONFLICT(identity) DO UPDATE SET logical_path=excluded.logical_path,local_path=excluded.local_path,sha256=excluded.sha256,length=excluded.length,state=excluded.state,pinned=excluded.pinned,base_sha256=excluded.base_sha256,local_sequence=excluded.local_sequence,asset_version=excluded.asset_version,edit_version=excluded.edit_version,collision_key=excluded.collision_key
        """, cancellationToken, ("$identity", value.Identity), ("$logical", value.LogicalPath), ("$local", value.LocalPath), ("$sha", value.Sha256), ("$length", value.Length), ("$state", (int)value.State), ("$pinned", value.Pinned ? 1 : 0), ("$base", value.BaseSha256), ("$sequence", value.LocalSequence), ("$assetVersion", value.AssetVersion), ("$editVersion", value.EditVersion), ("$collision", value.LocalPath.Normalize().ToUpperInvariant()));

    public async Task SaveItemsAsync(IReadOnlyList<SyncItem> values, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var value in values)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO items(identity,logical_path,local_path,sha256,length,state,pinned,base_sha256,local_sequence,asset_version,edit_version,collision_key) VALUES($identity,$logical,$local,$sha,$length,$state,$pinned,$base,$sequence,$assetVersion,$editVersion,$collision)
                    ON CONFLICT(identity) DO UPDATE SET logical_path=excluded.logical_path,local_path=excluded.local_path,sha256=excluded.sha256,length=excluded.length,state=excluded.state,pinned=excluded.pinned,base_sha256=excluded.base_sha256,local_sequence=excluded.local_sequence,asset_version=excluded.asset_version,edit_version=excluded.edit_version,collision_key=excluded.collision_key
                    """;
                foreach (var parameter in new (string Name, object? Value)[] { ("$identity", value.Identity), ("$logical", value.LogicalPath), ("$local", value.LocalPath), ("$sha", value.Sha256), ("$length", value.Length), ("$state", (int)value.State), ("$pinned", value.Pinned ? 1 : 0), ("$base", value.BaseSha256), ("$sequence", value.LocalSequence), ("$assetVersion", value.AssetVersion), ("$editVersion", value.EditVersion), ("$collision", value.LocalPath.Normalize().ToUpperInvariant()) })
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<SyncItem?> GetItemAsync(string identity, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT identity,logical_path,local_path,sha256,length,state,pinned,base_sha256,local_sequence,asset_version,edit_version FROM items WHERE identity=$identity";
            command.Parameters.AddWithValue("$identity", identity);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SyncItem>> ListItemsAsync(string? localPrefix = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = localPrefix is null ? "SELECT identity,logical_path,local_path,sha256,length,state,pinned,base_sha256,local_sequence,asset_version,edit_version FROM items ORDER BY local_path" : "SELECT identity,logical_path,local_path,sha256,length,state,pinned,base_sha256,local_sequence,asset_version,edit_version FROM items WHERE local_path=$prefix OR local_path LIKE $child ORDER BY local_path";
            if (localPrefix is not null) { command.Parameters.AddWithValue("$prefix", localPrefix); command.Parameters.AddWithValue("$child", localPrefix.TrimEnd('/', '\\') + "/%"); }
            var result = new List<SyncItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadItem(reader));
            return result;
        }
        finally { gate.Release(); }
    }

    public Task SetItemStateAsync(string identity, SyncItemState state, bool? pinned = null, string? sha256 = null, CancellationToken cancellationToken = default) => ExecuteAsync(
        "UPDATE items SET state=$state,pinned=COALESCE($pinned,pinned),sha256=COALESCE($sha,sha256),local_sequence=local_sequence+1 WHERE identity=$identity",
        cancellationToken, ("$state", (int)state), ("$pinned", pinned.HasValue ? pinned.Value ? 1 : 0 : null), ("$sha", sha256), ("$identity", identity));
    public Task SetItemBaseAsync(string identity, string sha256, CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE items SET sha256=$sha,base_sha256=$sha WHERE identity=$identity", cancellationToken, ("$sha", sha256), ("$identity", identity));
    public Task SetItemPathAsync(string identity, string logicalPath, string localPath, CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE items SET logical_path=$logical,local_path=$local,collision_key=$collision,local_sequence=local_sequence+1 WHERE identity=$identity", cancellationToken, ("$logical", logicalPath), ("$local", localPath), ("$collision", localPath.Normalize().ToUpperInvariant()), ("$identity", identity));
    public Task SetItemRemoteVersionAsync(string identity, bool edit, long version, CancellationToken cancellationToken = default) => ExecuteAsync(edit ? "UPDATE items SET edit_version=$version WHERE identity=$identity" : "UPDATE items SET asset_version=$version WHERE identity=$identity", cancellationToken, ("$version", version), ("$identity", identity));
    public Task DeleteItemAsync(string identity, CancellationToken cancellationToken = default) => ExecuteAsync("DELETE FROM items WHERE identity=$identity", cancellationToken, ("$identity", identity));

    public async Task<long> QueueAsync(string idempotencyKey, string identity, JournalOperationKind kind, string? baseSha256, string? currentSha256, string? destination, CancellationToken cancellationToken = default, long expectedVersion = 0)
    {
        await ExecuteAsync("INSERT INTO operations(idempotency_key,identity,kind,base_sha256,current_sha256,destination,expected_version) VALUES($key,$identity,$kind,$base,$current,$destination,$version) ON CONFLICT(idempotency_key) DO NOTHING", cancellationToken, ("$key", idempotencyKey), ("$identity", identity), ("$kind", (int)kind), ("$base", baseSha256), ("$current", currentSha256), ("$destination", destination), ("$version", expectedVersion));
        return Convert.ToInt64(await ScalarAsync("SELECT sequence FROM operations WHERE idempotency_key=$key", cancellationToken, ("$key", idempotencyKey)), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<JournalOperation>> PendingAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sequence,idempotency_key,identity,kind,base_sha256,current_sha256,destination,expected_version,acknowledged,attempts FROM operations WHERE acknowledged=0 ORDER BY sequence";
            var result = new List<JournalOperation>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (JournalOperationKind)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetBoolean(8), reader.GetInt32(9)));
            return result;
        }
        finally { gate.Release(); }
    }

    public Task MarkAttemptAsync(long sequence, CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE operations SET attempts=attempts+1 WHERE sequence=$sequence", cancellationToken, ("$sequence", sequence));
    public Task AcknowledgeAsync(long sequence, CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE operations SET acknowledged=1 WHERE sequence=$sequence", cancellationToken, ("$sequence", sequence));
    public async Task<bool> HasPendingAsync(string identity, CancellationToken cancellationToken = default) => Convert.ToInt64(await ScalarAsync("SELECT count(*) FROM operations WHERE identity=$identity AND acknowledged=0", cancellationToken, ("$identity", identity)), System.Globalization.CultureInfo.InvariantCulture) > 0;

    public Task SaveConflictAsync(SyncConflict value, CancellationToken cancellationToken = default) => ExecuteAsync("INSERT OR REPLACE INTO conflicts(id,identity,base_sha256,local_sha256,remote_sha256,preserved_path,reason,resolved) VALUES($id,$identity,$base,$local,$remote,$path,$reason,$resolved)", cancellationToken, ("$id", value.Id.ToString()), ("$identity", value.Identity), ("$base", value.BaseSha256), ("$local", value.LocalSha256), ("$remote", value.RemoteSha256), ("$path", value.PreservedPath), ("$reason", value.Reason), ("$resolved", value.Resolved ? 1 : 0));
    public async Task<IReadOnlyList<SyncConflict>> ListConflictsAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,identity,base_sha256,local_sha256,remote_sha256,preserved_path,reason,resolved FROM conflicts ORDER BY id";
            var result = new List<SyncConflict>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
            return result;
        }
        finally { gate.Release(); }
    }

    public Task ReplaceItemAsync(string oldIdentity, SyncItem value, CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE items SET identity=$new,logical_path=$logical,local_path=$local,sha256=$sha,length=$length,state=$state,pinned=$pinned,base_sha256=$base,asset_version=$assetVersion,edit_version=$editVersion,collision_key=$collision WHERE identity=$old", cancellationToken, ("$new", value.Identity), ("$logical", value.LogicalPath), ("$local", value.LocalPath), ("$sha", value.Sha256), ("$length", value.Length), ("$state", (int)value.State), ("$pinned", value.Pinned ? 1 : 0), ("$base", value.BaseSha256), ("$assetVersion", value.AssetVersion), ("$editVersion", value.EditVersion), ("$collision", value.LocalPath.Normalize().ToUpperInvariant()), ("$old", oldIdentity));
    public Task ClearUnsafeStateAsync(CancellationToken cancellationToken = default) => ExecuteAsync("UPDATE operations SET acknowledged=1 WHERE acknowledged=0; UPDATE conflicts SET resolved=1 WHERE resolved=0; UPDATE items SET state=$online,pinned=0 WHERE state IN ($dirty,$uploading,$conflicted)", cancellationToken, ("$online", (int)SyncItemState.OnlineOnly), ("$dirty", (int)SyncItemState.Dirty), ("$uploading", (int)SyncItemState.Uploading), ("$conflicted", (int)SyncItemState.Conflicted));
    public async Task<bool> HasUnsafeStateAsync(CancellationToken cancellationToken = default) => Convert.ToInt64(await ScalarAsync("SELECT (SELECT count(*) FROM operations WHERE acknowledged=0)+(SELECT count(*) FROM conflicts WHERE resolved=0)+(SELECT count(*) FROM items WHERE state IN ($dirty,$uploading,$conflicted))", cancellationToken, ("$dirty", (int)SyncItemState.Dirty), ("$uploading", (int)SyncItemState.Uploading), ("$conflicted", (int)SyncItemState.Conflicted)), System.Globalization.CultureInfo.InvariantCulture) > 0;

    private static SyncItem ReadItem(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), (SyncItemState)reader.GetInt32(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10));

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        gate.Dispose();
    }
}
