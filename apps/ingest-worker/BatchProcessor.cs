using System.Diagnostics;
using Npgsql;
using Platform.Ingest.Worker.Observability;
using Platform.Ingest.Worker.Verification;
using Platform.Storage;
using Platform.Storage.Paths;
using static Platform.Storage.IngestDb;

namespace Platform.Ingest.Worker;

public sealed class BatchProcessor(NpgsqlDataSource db, S3ObjectStore storage, ILogger<BatchProcessor>? logger = null, string libraryId = "main")
{
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var cleanup = new List<(Guid Id, string Key, string Version, bool Quarantine, string Owner, string Operation, string Sha256)>();
        var expiredUploads = new List<(Guid Id, string? UploadId)>();
        await using (var command = Command(connection, "SELECT id,multipart_id FROM upload_sessions WHERE state='uploading' AND expires_at<=now()"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) expiredUploads.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        foreach (var item in expiredUploads) await storage.DeleteStagingAsync(item.Id, item.UploadId, ct);
        await using (var command = Command(connection, """
            SELECT id,target_key,staging_version,false,owner_id,operation_id,expected_sha256
              FROM upload_sessions WHERE staging_version IS NOT NULL AND state IN ('completed','failed')
            UNION ALL
            SELECT id,'quarantine/'||replace(id::text,'-',''),quarantine_version,true,owner_id,operation_id,expected_sha256
              FROM upload_sessions WHERE quarantine_version IS NOT NULL AND quarantine_until<=now()
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) cleanup.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5), reader.GetString(6).Trim()));
        foreach (var item in cleanup)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await LockOwnerAsync(connection, item.Owner, ct);
            await storage.DeleteVersionAsync(item.Key, item.Version, ct);
            var removed = await ExecuteAsync(connection, item.Quarantine ? "UPDATE upload_sessions SET quarantine_version=NULL WHERE id=$1 AND quarantine_version=$2" : "UPDATE upload_sessions SET staging_version=NULL WHERE id=$1 AND staging_version=$2", ct, item.Id, item.Version);
            if (removed == 1)
                await AuditAndRecordRecoveryChangeAsync(connection, transaction, item.Owner, libraryId, item.Operation,
                    item.Quarantine ? "ingest.quarantine_expired" : "ingest.staging_cleaned", item.Id,
                    "completed", "upload-session", item.Id.ToString("D"), [item.Sha256], ct);
            await transaction.CommitAsync(ct);
        }

        await using var expireTransaction = await connection.BeginTransactionAsync(ct);
        var owners = new List<string>();
        await using (var ownerQuery = Command(connection, "SELECT DISTINCT owner_id FROM upload_sessions WHERE state='uploading' AND expires_at<=now() ORDER BY owner_id"))
        await using (var reader = await ownerQuery.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) owners.Add(reader.GetString(0));
        foreach (var owner in owners) await LockOwnerAsync(connection, owner, ct);

        var expired = new List<(Guid Id, string Owner, string Operation, string Sha256)>();
        await using (var expire = new NpgsqlCommand("""
            WITH expired AS (
                UPDATE upload_sessions SET state='failed',outcome_code='upload_expired',lifecycle_version=lifecycle_version+1
                WHERE state='uploading' AND expires_at<=now() RETURNING id,batch_id,owner_id,operation_id,expected_sha256
            ), items AS (
                UPDATE import_batch_items SET state='failed',outcome_code='upload_expired' WHERE upload_session_id IN (SELECT id FROM expired)
            ), queued AS (
                INSERT INTO ingest_publications(batch_id) SELECT DISTINCT batch_id FROM expired ON CONFLICT DO NOTHING
            )
            SELECT id,owner_id,operation_id,expected_sha256 FROM expired ORDER BY owner_id,id
            """, connection, expireTransaction))
        await using (var reader = await expire.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) expired.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3).Trim()));
        foreach (var item in expired)
            await AuditAndRecordRecoveryChangeAsync(connection, expireTransaction, item.Owner, libraryId, item.Operation,
                "upload.expired", item.Id, "failed", "upload-session", item.Id.ToString("D"), [item.Sha256], ct,
                "upload_expired");
        await expireTransaction.CommitAsync(ct);
        return expired.Count;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        Guid batchId = default;
        try
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = Command(connection, """
                SELECT b.id,b.owner_id,b.atomic,b.created_at FROM ingest_publications p
                JOIN import_batches b ON b.id=p.batch_id
                WHERE b.state IN ('created','processing') AND (p.last_error IS NULL OR p.updated_at < now()-interval '5 seconds')
                AND (NOT b.atomic OR NOT EXISTS(SELECT 1 FROM upload_sessions s WHERE s.batch_id=b.id AND s.state='uploading'))
                AND (EXISTS(SELECT 1 FROM upload_sessions s WHERE s.batch_id=b.id AND s.state='verifying')
                     OR NOT EXISTS(SELECT 1 FROM upload_sessions s WHERE s.batch_id=b.id AND s.state='uploading'))
                ORDER BY b.created_at FOR UPDATE OF p SKIP LOCKED LIMIT 1
                """);
            string owner; bool atomic; DateTime created;
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return false;
                batchId = r.GetGuid(0); owner = r.GetString(1); atomic = r.GetBoolean(2); created = r.GetDateTime(3);
            }
            await LockOwnerAsync(connection, owner, ct);
            var items = new List<PendingItem>();
            await using (var query = Command(connection, "SELECT id,logical_path,expected_sha256,expected_length,staging_version,state,operation_id FROM upload_sessions WHERE batch_id=$1 ORDER BY (lower(logical_path) LIKE '%.xmp'),id FOR UPDATE", batchId))
            await using (var r = await query.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) items.Add(new(r.GetGuid(0), r.IsDBNull(1) ? "" : r.GetString(1), new(r.GetString(2).Trim(), r.GetInt64(3)), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)));

            var failures = new Dictionary<Guid, string>();
            await transaction.SaveAsync("publication_set", ct);
            foreach (var item in items.Where(i => i.State == "verifying"))
            {
                var start = Stopwatch.GetTimestamp();
                await transaction.SaveAsync("publication_item", ct);
                try
                {
                    await PublishItemAsync(connection, transaction, owner, batchId, item, ct);
                    IngestTelemetry.VerifiedBytes.Add(item.Address.Length);
                }
                catch (Exception e) when (e is InvalidDataException or IngestException || e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    await transaction.RollbackAsync("publication_item", ct);
                    failures[item.Id] = e is IngestException ingest ? ingest.Code : e is PostgresException ? "path_collision" : e.Message is "malformed_xmp" or "unsupported_format" ? e.Message : "checksum_mismatch";
                }
                IngestTelemetry.VerificationDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
            }
            if (atomic && (failures.Count > 0 || items.Any(i => i.State is "failed" or "cancelled")))
            {
                await transaction.RollbackAsync("publication_set", ct);
                foreach (var item in items.Where(i => i.State == "verifying")) failures.TryAdd(item.Id, "atomic_batch_failed");
            }
            foreach (var item in items.Where(i => failures.ContainsKey(i.Id)))
            {
                var quarantineVersion = item.Version is null ? null : await storage.QuarantineAsync(item.Id, item.Version, ct);
                await ExecuteAsync(connection, "UPDATE upload_sessions SET state='failed',outcome_code=$2,quarantine_until=now()+interval '30 days',quarantine_version=$3,lifecycle_version=lifecycle_version+1 WHERE id=$1; UPDATE import_batch_items SET state='quarantined',outcome_code=$2 WHERE upload_session_id=$1", ct, item.Id, failures[item.Id], quarantineVersion);
                await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, item.Operation,
                    "ingest.quarantined", item.Id, "quarantined", "upload-session", item.Id.ToString("D"),
                    [item.Address.Sha256], ct, failures[item.Id]);
                IngestTelemetry.Quarantined.Add(1);
                logger?.LogWarning("Ingest item quarantined {AuditEventType} {ReasonCode}", "ingest.quarantined", failures[item.Id]);
            }
            await ExecuteAsync(connection, """
                UPDATE import_batches SET state=CASE
                  WHEN EXISTS(SELECT 1 FROM upload_sessions WHERE batch_id=$1 AND state IN ('uploading','verifying')) THEN 'processing'
                  WHEN EXISTS(SELECT 1 FROM upload_sessions WHERE batch_id=$1 AND state IN ('failed','cancelled')) THEN 'failed'
                  ELSE 'completed' END WHERE id=$1;
                UPDATE ingest_publications SET last_error=NULL,updated_at=now() WHERE batch_id=$1
                """, ct, batchId);
            await transaction.CommitAsync(ct);
            IngestTelemetry.BatchAge.Record((DateTime.UtcNow - created).TotalSeconds);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            IngestTelemetry.StorageFailures.Add(1);
            logger?.LogError("Ingest publication will retry {AuditEventType}", "ingest.storage.failure");
            if (batchId != default)
            {
                await using var connection = await db.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                var owner = (string?)await ScalarAsync(connection, "SELECT owner_id FROM import_batches WHERE id=$1", ct, batchId);
                if (owner is not null)
                {
                    await LockOwnerAsync(connection, owner, ct);
                    await ExecuteAsync(connection, "UPDATE ingest_publications SET attempts=attempts+1,last_error='storage_retryable',updated_at=now() WHERE batch_id=$1", ct, batchId);
                    await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, libraryId, "op_repair",
                        "publication.retry", batchId, "failed", "import-batch", batchId.ToString("D"), [], ct,
                        "storage_retryable");
                    await transaction.CommitAsync(ct);
                }
            }
            throw;
        }
    }

    private async Task PublishItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string owner, Guid batch, PendingItem item, CancellationToken ct)
    {
        if (item.Version is null) throw new IngestException("upload_incomplete");
        await using var bytes = await storage.ReadVerifiedAsync(storage.StagingKey(item.Id), item.Version, item.Address, ct);
        var category = ContentInspector.Inspect(bytes, item.Path);
        Guid asset; Guid? edit = null; var ordinal = 0;
        if (category == "xmp")
        {
            var candidates = new List<(Guid Id, int Ordinal)>();
            foreach (var source in ContentInspector.SourceCandidates(item.Path))
            {
                var found = await ScalarAsync(connection, "SELECT a.id FROM assets a JOIN asset_paths p ON p.asset_id=a.id WHERE a.owner_id=$1 AND a.state IN ('active','restored') AND p.active AND p.collision_key=$2", ct, owner, LibraryPath.CollisionKey(source.Path));
                if (found is Guid foundId) candidates.Add((foundId, source.Ordinal));
            }
            if (candidates.Count != 1) throw new IngestException(candidates.Count == 0 ? "unassociated_xmp" : "ambiguous_association");
            asset = candidates[0].Id; ordinal = candidates[0].Ordinal; edit = Guid.NewGuid();
            if (await ScalarAsync(connection, "SELECT 1 FROM edit_identities WHERE asset_id=$1 AND duplicate_ordinal=$2", ct, asset, ordinal) is not null) throw new IngestException("edit_collision");
        }
        else
        {
            if (await ScalarAsync(connection, "SELECT 1 FROM asset_paths WHERE owner_id=$1 AND collision_key=$2 AND active", ct, owner, LibraryPath.CollisionKey(item.Path)) is not null) throw new IngestException("path_collision");
            asset = Guid.NewGuid();
        }
        // This also rejects new references to the same SHA from a different owner. The
        // digest-row lock serializes admission with the freeze transaction.
        if (await IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
                connection, [item.Address.Sha256], ct))
            throw new IngestException("recovery_purge_frozen", 409);
        var version = await storage.PublishVerifiedAsync(item.Address, bytes, ct);
        var recorded = await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,$3,$4,$5,'authoritative') ON CONFLICT(sha256) DO UPDATE SET storage_version=EXCLUDED.storage_version WHERE content_objects.byte_length=EXCLUDED.byte_length AND content_objects.integrity_state='authoritative'", ct, item.Address.Sha256, item.Address.Length, category, item.Address.AuthoritativeKey, version);
        if (recorded != 1) throw new IngestException("integrity_error");
        if (edit is null)
        {
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,original_sha256,state,format) VALUES($1,$2,$3,'active',$4); INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active) VALUES($5,$1,$2,$6,$7,true)", ct, asset, owner, item.Address.Sha256, category, Guid.NewGuid(), item.Path, LibraryPath.CollisionKey(item.Path));
        }
        else
        {
            await ExecuteAsync(connection, "INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($1,$2,$3,$4,$5)", ct, edit.Value, asset, ordinal, item.Address.Sha256, $"darktable-name-v1/session/{item.Id:D}");
            await AuditAsync(connection, owner, item.Operation, "xmp.associated", edit.Value, "accepted", ct);
        }
        await ExecuteAsync(connection, "UPDATE upload_sessions SET state='completed',category=$2,asset_id=$3,edit_id=$4,verified_at=now(),lifecycle_version=lifecycle_version+1 WHERE id=$1; UPDATE import_batch_items SET state='active',outcome_code=NULL WHERE upload_session_id=$1", ct, item.Id, category, asset, edit);
        await AuditAsync(connection, owner, item.Operation, "publication.completed", edit ?? asset, "active", ct);
        await AuditAsync(connection, owner, item.Operation, "upload.verified", item.Id, "active", ct);
        await RecordRecoveryChangeAsync(connection, transaction, owner, libraryId, item.Operation,
            "content.imported", edit is null ? "asset" : "edit", (edit ?? asset).ToString("D"), [item.Address.Sha256],
            DateTimeOffset.UtcNow, ct);
    }
}

internal sealed record PendingItem(Guid Id, string Path, ContentAddress Address, string? Version, string State, string Operation);
