using System.Text.Json;
using Npgsql;
using Platform.Storage.Paths;
using static Platform.Storage.IngestDb;

namespace Platform.Storage;

public sealed class AssetService(NpgsqlDataSource db, S3ObjectStore storage, TimeProvider? clock = null, string libraryId = "main")
{
    private DateTime Now
    {
        get { var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime; return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc); }
    }

    public async Task<IReadOnlyList<AssetView>> ListAsync(string owner, bool deleted, Guid? after, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = Command(connection, """
            SELECT a.id,a.state,a.lifecycle_version,a.deleted_until,p.display_path,a.original_sha256,a.format
            FROM assets a JOIN LATERAL(SELECT display_path FROM asset_paths WHERE asset_id=a.id ORDER BY effective_from DESC,id DESC LIMIT 1) p ON true
            WHERE a.owner_id=$1 AND (CASE WHEN $2 THEN a.state='recoverable-deleted' ELSE a.state IN ('active','restored','integrity-error') END)
            AND ($3::uuid IS NULL OR a.id>$3) ORDER BY a.id LIMIT 100
            """, owner, deleted, after);
        var result = new List<AssetView>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetDateTime(3), r.GetString(4), r.GetString(5).Trim(), r.IsDBNull(6) ? "unknown" : r.GetString(6)));
        return result;
    }

    public async Task<IReadOnlyList<EditView>> EditsAsync(string owner, Guid asset, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT e.id,e.state,e.lifecycle_version,e.deleted_until,e.duplicate_ordinal,e.xmp_sha256 FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE a.id=$1 AND a.owner_id=$2 ORDER BY e.duplicate_ordinal", asset, owner);
        var result = new List<EditView>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetDateTime(3), r.GetInt32(4), r.GetString(5).Trim()));
        return result;
    }

    public async Task<ContentDownload> DownloadAsync(string owner, Guid id, bool edit, string operation, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwnerAsync(connection, owner, ct);
        var sql = edit ? "SELECT c.sha256,c.byte_length,c.storage_version,e.handoff_id FROM edit_identities e JOIN assets a ON a.id=e.asset_id JOIN content_objects c ON c.sha256=e.xmp_sha256 WHERE e.id=$1 AND a.owner_id=$2 AND a.state IN ('active','restored') AND e.state IN ('active','restored') AND c.integrity_state='authoritative'"
            : "SELECT c.sha256,c.byte_length,c.storage_version,a.handoff_id FROM assets a JOIN content_objects c ON c.sha256=a.original_sha256 WHERE a.id=$1 AND a.owner_id=$2 AND a.state IN ('active','restored') AND c.integrity_state='authoritative'";
        ContentAddress address; string version; Guid handoff;
        await using (var command = Command(connection, sql, id, owner))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new IngestException("not_authoritative", 404);
            address = new(r.GetString(0).Trim(), r.GetInt64(1)); version = r.GetString(2); handoff = r.GetGuid(3);
        }
        FileStream bytes;
        try { bytes = await storage.ReadVerifiedAsync(address.AuthoritativeKey, version, address, ct); }
        catch (Exception e) when (e is InvalidDataException || e is Amazon.S3.AmazonS3Exception { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            await ExecuteAsync(connection, "UPDATE content_objects SET integrity_state='integrity-error' WHERE sha256=$1; UPDATE assets SET state='integrity-error',lifecycle_version=lifecycle_version+1 WHERE original_sha256=$1 AND state IN ('active','restored'); UPDATE edit_identities SET state='integrity-error',lifecycle_version=lifecycle_version+1 WHERE xmp_sha256=$1 AND state IN ('active','restored')", ct, address.Sha256);
            await AuditAsync(connection, owner, operation, "integrity.drift", id, "integrity-error", ct, "checksum_mismatch");
            await RecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
                "content.integrity-error", "content", address.Sha256, [address.Sha256], Now, ct);
            await transaction.CommitAsync(ct);
            throw new IngestException("integrity_error", 409);
        }
        try
        {
            await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
                "content.downloaded", id, "accepted", edit ? "edit" : "asset", id.ToString("D"),
                [address.Sha256], ct);
            await transaction.CommitAsync(ct);
            return new(bytes, address, handoff);
        }
        catch { await bytes.DisposeAsync(); throw; }
    }

    public async Task<MutationResult> ChangeAsync(string owner, Guid id, bool edit, string action, long version, string? path, string key, string operation, CancellationToken ct)
    {
        if (action is not ("move" or "delete" or "restore") || edit && action == "move") throw new IngestException("invalid_request", 400);
        if (path is not null) path = LibraryPath.Normalize(path);
        var fingerprint = JsonSerializer.Serialize(new { id, edit, action, version, path });
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwnerAsync(connection, owner, ct);
        await using (var receipt = Command(connection, "SELECT fingerprint,result::text FROM asset_mutations WHERE owner_id=$1 AND idempotency_key=$2", owner, key))
        await using (var r = await receipt.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) != fingerprint) throw new IngestException("idempotency_key_reused");
                return JsonSerializer.Deserialize<MutationResult>(r.GetString(1))!;
            }
        }
        string state; long current; DateTime? deadline; Guid asset; string parentState;
        var select = edit ? "SELECT e.state,e.lifecycle_version,e.deleted_until,a.id,a.state FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE e.id=$1 AND a.owner_id=$2 FOR UPDATE OF e,a"
            : "SELECT state,lifecycle_version,deleted_until,id,state FROM assets WHERE id=$1 AND owner_id=$2 FOR UPDATE";
        await using (var command = Command(connection, select, id, owner))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new IngestException("not_found", 404);
            state = r.GetString(0); current = r.GetInt64(1); deadline = r.IsDBNull(2) ? null : r.GetDateTime(2); asset = r.GetGuid(3); parentState = r.GetString(4);
        }
        if (current != version || edit && parentState is not ("active" or "restored")) throw new IngestException("lifecycle_conflict");
        var contentSha = (string?)await ScalarAsync(connection,
            edit ? "SELECT e.xmp_sha256 FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE e.id=$1 AND a.owner_id=$2"
                : "SELECT original_sha256 FROM assets WHERE id=$1 AND owner_id=$2", ct, id, owner)
            ?? throw new IngestException("not_found", 404);
        var table = edit ? "edit_identities" : "assets";
        string next;
        if (action == "delete")
        {
            if (state is not ("active" or "restored")) throw new IngestException("lifecycle_conflict");
            if (await ScalarAsync(connection, "SELECT 1 FROM lifecycle_holds WHERE asset_id=$1 AND (expires_at IS NULL OR expires_at>$2) LIMIT 1", ct, asset, Now) is not null) throw new IngestException("retention_hold");
            deadline = Now.AddDays(90); next = "recoverable-deleted";
            if (!edit) await ExecuteAsync(connection, "UPDATE asset_paths SET active=false,effective_to=$2 WHERE asset_id=$1 AND active", ct, id, Now);
        }
        else if (action == "restore")
        {
            if (state != "recoverable-deleted" || deadline is null || Now > deadline) throw new IngestException("restore_expired");
            if (!edit)
            {
                path ??= (string?)await ScalarAsync(connection, "SELECT display_path FROM asset_paths WHERE asset_id=$1 ORDER BY effective_from DESC,id DESC LIMIT 1", ct, id) ?? throw new IngestException("not_found", 404);
                await AddPathAsync(connection, owner, id, path, Now, ct);
            }
            next = "restored"; deadline = null;
        }
        else
        {
            if (state is not ("active" or "restored") || path is null) throw new IngestException("lifecycle_conflict");
            await ExecuteAsync(connection, "UPDATE asset_paths SET active=false,effective_to=$2 WHERE asset_id=$1 AND active", ct, id, Now);
            await AddPathAsync(connection, owner, id, path, Now, ct); next = state;
        }
        await ExecuteAsync(connection, $"UPDATE {table} SET state=$2,lifecycle_version=lifecycle_version+1,deleted_until=$3 WHERE id=$1", ct, id, next, deadline);
        await AuditAsync(connection, owner, operation, (edit ? "edit." : "asset.") + action, id, next, ct);
        await RecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
            (edit ? "edit." : "asset.") + action, edit ? "edit" : "asset", id.ToString("D"), [contentSha], Now, ct);
        var result = new MutationResult(id, next, current + 1, deadline, path);
        await ExecuteAsync(connection, "INSERT INTO asset_mutations(owner_id,idempotency_key,fingerprint,result) VALUES($1,$2,$3,$4::jsonb)", ct, owner, key, fingerprint, JsonSerializer.Serialize(result));
        await transaction.CommitAsync(ct);
        return result;
    }

    private static Task<int> AddPathAsync(NpgsqlConnection connection, string owner, Guid asset, string path, DateTime now, CancellationToken ct) =>
        ExecuteAsync(connection, "INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active,effective_from) VALUES($1,$2,$3,$4,$5,true,$6)", ct, Guid.NewGuid(), asset, owner, path, LibraryPath.CollisionKey(path), now);
}

public sealed record AssetView(Guid Id, string State, long LifecycleVersion, DateTime? DeletedUntil, string Path, string Sha256, string Format);
public sealed record EditView(Guid Id, string State, long LifecycleVersion, DateTime? DeletedUntil, int Ordinal, string Sha256);
public sealed record MutationResult(Guid Id, string State, long LifecycleVersion, DateTime? DeletedUntil, string? Path);
public sealed record ContentDownload(FileStream Bytes, ContentAddress Address, Guid HandoffId);
