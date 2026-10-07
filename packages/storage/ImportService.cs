using System.Text.Json;
using Npgsql;
using Platform.Storage.Paths;
using static Platform.Storage.IngestDb;

namespace Platform.Storage;

public sealed class ImportService(NpgsqlDataSource db, S3ObjectStore storage, long quotaBytes = 1024L * 1024 * 1024 * 1024, string libraryId = "main")
{
    public async Task<BatchStatus> CreateAsync(string owner, string key, string operation, ImportRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 1000 || request.Items.Any(i => i is null || !new ContentAddress(i.Sha256, i.Length).IsValid || i.Length is < 1 or > S3ObjectStore.MaxObjectSize)) throw new IngestException("invalid_batch", 400);
        var paths = request.Items.Select(i => LibraryPath.Normalize(i.Path)).ToArray();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwnerAsync(connection, owner, ct);
        var existing = await ScalarAsync(connection, "SELECT id FROM import_batches WHERE owner_id=$1 AND idempotency_key=$2", ct, owner, key);
        if (existing is Guid existingId) { await transaction.CommitAsync(ct); return await StatusAsync(owner, existingId, ct); }
        if (await LockAndCheckRecoveryPurgeDigestsAsync(connection,
                request.Items.Select(item => item.Sha256), ct))
            throw new IngestException("recovery_purge_frozen", 409);
        var used = (decimal)(await ScalarAsync(connection, "SELECT COALESCE(sum(expected_length),0) FROM upload_sessions WHERE owner_id=$1 AND (state NOT IN ('failed','cancelled') OR quarantine_until>now() OR expires_at>now())", ct, owner))!;
        if (used + request.Items.Sum(i => (decimal)i.Length) > quotaBytes) throw new IngestException("quota_exceeded", 413);
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO import_batches(id,owner_id,idempotency_key,atomic,state) VALUES($1,$2,$3,$4,'created')", ct, id, owner, key, request.Atomic);
        var rows = request.Items.Select((item, i) => new { id = Guid.NewGuid(), sha = item.Sha256, length = item.Length, path = paths[i] });
        await ExecuteAsync(connection, """
            WITH sessions AS (
                INSERT INTO upload_sessions(id,batch_id,owner_id,target_key,expected_sha256,expected_length,expires_at,logical_path,operation_id)
                SELECT id,$1,$2,'staging/'||replace(id::text,'-',''),sha,length,now()+interval '24 hours',path,$3
                FROM jsonb_to_recordset($4::jsonb) AS item(id uuid,sha text,length bigint,path text) RETURNING id
            )
            INSERT INTO import_batch_items(id,batch_id,upload_session_id,state) SELECT gen_random_uuid(),$1,id,'uploading' FROM sessions
            """, ct, id, owner, operation, JsonSerializer.Serialize(rows));
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
            "import.batch_created", id, "accepted", "import-batch", id.ToString("D"),
            request.Items.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).ToArray(), ct);
        await transaction.CommitAsync(ct);
        return await StatusAsync(owner, id, ct);
    }

    public async Task<BatchStatus> StatusAsync(string owner, Guid id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var batch = Command(connection, "SELECT state,atomic,(SELECT COALESCE(sum(c.byte_length),0) FROM content_objects c WHERE c.sha256 IN (SELECT expected_sha256 FROM upload_sessions WHERE batch_id=$1 AND state='completed')),(SELECT count(DISTINCT expected_sha256) FROM upload_sessions WHERE batch_id=$1 AND state='completed') FROM import_batches WHERE id=$1 AND owner_id=$2", id, owner);
        string state; bool atomic; decimal storedBytes; long objectCount;
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new IngestException("not_found", 404);
            state = reader.GetString(0); atomic = reader.GetBoolean(1); storedBytes = reader.GetDecimal(2); objectCount = reader.GetInt64(3);
        }
        await using var command = Command(connection, "SELECT s.id,s.logical_path,i.state,s.lifecycle_version,s.expected_length,s.received_parts::text,s.outcome_code,s.asset_id,s.edit_id,s.quarantine_until FROM upload_sessions s JOIN import_batch_items i ON i.upload_session_id=s.id WHERE s.batch_id=$1 ORDER BY s.id", id);
        var items = new List<SessionStatus>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(new(r.GetGuid(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), JsonSerializer.Deserialize<ReceivedPart[]>(r.GetString(5)) ?? [], r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetDateTime(9)));
        return new(id, state, atomic, S3ObjectStore.PartSize, items, storedBytes, objectCount);
    }

    public async Task<SessionStatus> PutPartAsync(string owner, Guid id, long version, int number, byte[] bytes, string hash, string operation, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var session = await LoadLockedAsync(connection, owner, id, ct);
        if (await LockAndCheckRecoveryPurgeDigestsAsync(connection, [session.Address.Sha256], ct))
            throw new IngestException("recovery_purge_frozen", 409);
        if (session.Version != version || session.State != "uploading" || session.Expires <= DateTime.UtcNow) throw new IngestException("lifecycle_conflict");
        if (session.TransferAuthorizedUntil is null || session.TransferAuthorizedUntil <= DateTime.UtcNow) throw new IngestException("transfer_authorization_required", 403);
        var requiredLength = Math.Min(S3ObjectStore.PartSize, session.Length - (number - 1L) * S3ObjectStore.PartSize);
        if (number < 1 || requiredLength <= 0 || bytes.LongLength != requiredLength) throw new IngestException("invalid_part", 400);
        using var stream = new MemoryStream(bytes, false);
        await ContentAddress.VerifyAsync(stream, new(hash, requiredLength), ct); stream.Position = 0;
        var parts = session.Parts.ToList();
        var previous = parts.Find(p => p.Number == number);
        if (previous is not null && previous.Sha256 != hash) throw new IngestException("part_conflict");
        if (previous is null)
        {
            var uploadId = session.MultipartId ?? await storage.StartUploadAsync(id, ct);
            var etag = await storage.UploadPartAsync(id, uploadId, number, stream, ct);
            parts.Add(new(number, etag, hash, requiredLength));
            await ExecuteAsync(connection, "UPDATE upload_sessions SET multipart_id=$2,received_parts=$3::jsonb WHERE id=$1", ct, id, uploadId, JsonSerializer.Serialize(parts));
            await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
                "upload.part_received", id, "accepted", "upload-session", id.ToString("D"), [], ct);
        }
        await transaction.CommitAsync(ct);
        return (await StatusAsync(owner, session.BatchId, ct)).Items.Single(i => i.Id == id);
    }

    public async Task<SessionStatus> CompleteAsync(string owner, Guid id, long version, string operation, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var session = await LoadLockedAsync(connection, owner, id, ct);
        if ((session.State is "uploading" or "verifying") &&
            await LockAndCheckRecoveryPurgeDigestsAsync(connection, [session.Address.Sha256], ct))
            throw new IngestException("recovery_purge_frozen", 409);
        if (session.State is "verifying" or "completed") { await transaction.CommitAsync(ct); return (await StatusAsync(owner, session.BatchId, ct)).Items.Single(i => i.Id == id); }
        if (session.Version != version || session.State != "uploading" || session.Expires <= DateTime.UtcNow) throw new IngestException("lifecycle_conflict");
        if (session.MultipartId is null || session.Parts.Sum(p => p.Length) != session.Length) throw new IngestException("upload_incomplete");
        var storageVersion = await storage.CompleteUploadAsync(id, session.MultipartId, session.Parts, ct);
        await ExecuteAsync(connection, "UPDATE upload_sessions SET state='verifying',staging_version=$2,lifecycle_version=lifecycle_version+1,operation_id=$3 WHERE id=$1; UPDATE import_batch_items SET state='verifying' WHERE upload_session_id=$1; UPDATE import_batches SET state='processing' WHERE id=$4; INSERT INTO ingest_publications(batch_id) VALUES($4) ON CONFLICT DO NOTHING", ct, id, storageVersion, operation, session.BatchId);
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
            "upload.completed", id, "verifying", "upload-session", id.ToString("D"), [], ct);
        await transaction.CommitAsync(ct);
        return (await StatusAsync(owner, session.BatchId, ct)).Items.Single(i => i.Id == id);
    }

    public async Task CancelAsync(string owner, Guid id, long version, string operation, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var session = await LoadLockedAsync(connection, owner, id, ct);
        if (session.State == "cancelled") return;
        if (session.Version != version || session.State != "uploading") throw new IngestException("lifecycle_conflict");
        if (session.MultipartId is not null) await storage.AbortUploadAsync(id, session.MultipartId, ct);
        await ExecuteAsync(connection, "UPDATE upload_sessions SET state='cancelled',outcome_code='cancelled',lifecycle_version=lifecycle_version+1 WHERE id=$1; UPDATE import_batch_items SET state='failed',outcome_code='cancelled' WHERE upload_session_id=$1; INSERT INTO ingest_publications(batch_id) VALUES($2) ON CONFLICT DO NOTHING", ct, id, session.BatchId);
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, operation,
            "upload.cancelled", id, "accepted", "upload-session", id.ToString("D"), [], ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<UploadSession> LoadLockedAsync(NpgsqlConnection connection, string owner, Guid id, CancellationToken ct)
    {
        await LockOwnerAsync(connection, owner, ct);
        await using var command = Command(connection, "SELECT batch_id,state,lifecycle_version,expected_length,expires_at,multipart_id,received_parts::text,transfer_authorized_until,expected_sha256 FROM upload_sessions WHERE id=$1 AND owner_id=$2 FOR UPDATE", id, owner);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new IngestException("not_found", 404);
        return new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetString(5), JsonSerializer.Deserialize<ReceivedPart[]>(r.GetString(6)) ?? [], r.IsDBNull(7) ? null : r.GetDateTime(7), r.GetString(8).Trim());
    }
}

public sealed record ImportRequest(bool Atomic, IReadOnlyList<ImportItem> Items);
public sealed record ImportItem(string Path, string Sha256, long Length);
public sealed record BatchStatus(Guid Id, string State, bool Atomic, int PartSize, IReadOnlyList<SessionStatus> Items, decimal StoredBytes, long ObjectCount);
public sealed record SessionStatus(Guid Id, string Path, string State, long LifecycleVersion, long Length, IReadOnlyList<ReceivedPart> Parts, string? Code, Guid? AssetId, Guid? EditId, DateTime? QuarantineUntil);
internal sealed record UploadSession(Guid BatchId, string State, long Version, long Length, DateTime Expires, string? MultipartId, ReceivedPart[] Parts, DateTime? TransferAuthorizedUntil, string ExpectedSha256)
{
    public ContentAddress Address => new(ExpectedSha256, Length);
}
