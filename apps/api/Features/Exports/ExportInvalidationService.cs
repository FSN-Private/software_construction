using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;
/// <summary>Trusted in-process feature 011 adapter only. No HTTP publisher is exposed.</summary>
public sealed class ExportInvalidationService(NpgsqlDataSource source)
{
    internal static readonly long ProcessStartTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public async Task ApplyAsync(NpgsqlTransaction transaction, ExportPurgeEvent value, CancellationToken ct)
    {
        var db = transaction.Connection ?? throw new ExportProblem("purge_transaction_required");
        value.Validate(); await ExportDatabase.LockAsync(db, value.OwnerId, value.LibraryId, ct);
        await using (var check = Command(db, "SELECT owner_id,library_id,asset_id,edit_id,category,content_sha256,generation,evidence_id FROM export_purge_tombstones WHERE event_id=$1", value.EventId))
        await using (var r = await check.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) != value.OwnerId || r.GetString(1) != value.LibraryId || r.GetGuid(2) != value.AssetId || (r.IsDBNull(3) ? null : (Guid?)r.GetGuid(3)) != value.EditId || r.GetString(4) != value.Category || r.GetString(5).Trim() != value.ContentSha256 || r.GetInt64(6) != value.Generation || r.GetGuid(7) != value.EvidenceId) throw new ExportProblem("purge_event_conflict");
                return;
            }
        }
        if (await ScalarAsync(db, "SELECT 1 FROM export_purge_tombstones WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id IS NOT DISTINCT FROM $4 AND category=$5 AND generation>=$6 LIMIT 1", ct, value.OwnerId, value.LibraryId, value.AssetId, value.EditId, value.Category, value.Generation) is not null) throw new ExportProblem("stale_purge_event");
        await ExecuteAsync(db, "INSERT INTO export_purge_tombstones(event_id,owner_id,library_id,asset_id,edit_id,category,content_sha256,generation,evidence_id) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)", ct, value.EventId, value.OwnerId, value.LibraryId, value.AssetId, value.EditId, value.Category, value.ContentSha256, value.Generation, value.EvidenceId);
        var requests = new List<Guid>();
        await using (var command = Command(db, "SELECT id,snapshot::text FROM export_requests WHERE owner_id=$1 AND library_id=$2", value.OwnerId, value.LibraryId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) { var snapshot = ExportDatabase.Decode<ExportSnapshot>(reader.GetString(1)); if (snapshot.Items.Any(pin => value.Affects(pin, snapshot.PresetId))) requests.Add(reader.GetGuid(0)); }
        }
        foreach (var request in requests)
        {
            await ExecuteAsync(db, "UPDATE export_requests SET state='unavailable',reason='permanently_unavailable',generation=generation+1 WHERE id=$1; UPDATE export_download_grants SET revoked_at=now() WHERE request_id=$1", ct, request);
            await AuditAsync(db, value.OwnerId, ExportDatabase.OperationId, "exports.permanently_unavailable", request, "accepted", ct);
        }
    }
    public Task<Guid> AdmitWriteAsync(string owner, string library, Guid request, long generation, Guid writer, CancellationToken ct) => AdmitAsync(owner, library, request, generation, writer, null, ct);
    public Task<Guid> AdmitWorkspaceAsync(string owner, string library, Guid request, long generation, Guid writer, string root, CancellationToken ct) => AdmitAsync(owner, library, request, generation, writer, Path.GetFullPath(root), ct);
    private async Task<Guid> AdmitAsync(string owner, string library, Guid request, long generation, Guid writer, string? root, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        if (await ScalarAsync(db, "SELECT 1 FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND generation=$4 AND lease_id=$5 AND state='preparing' AND lease_until>now()", ct, request, owner, library, generation, writer) is null) throw new ExportProblem("cancelled");
        var id = Guid.NewGuid();
        await ExecuteAsync(db, "INSERT INTO export_write_intents(id,request_id,generation,writer_id,object_key,state,category,workspace_path,writer_host,writer_pid,writer_start_ticks) VALUES($1,$2,$3,$4,$5,'admitted',$6,$7,$8,$9,$10)", ct, id, request, generation, writer, root is null ? $"exports/{request:N}/{id:N}/package.zip" : $"workspace:{id:N}", root is null ? "package" : "workspace", root is null ? null : Path.Combine(root, id.ToString("N")), Environment.MachineName, Environment.ProcessId, ProcessStartTicks);
        await tx.CommitAsync(ct); return id;
    }
    public async Task BeginStorageAsync(Guid intent, Guid writer, string phase, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        string owner; string library;
        await using (var cmd = Command(db, "SELECT r.owner_id,r.library_id FROM export_requests r JOIN export_write_intents i ON i.request_id=r.id WHERE i.id=$1 AND i.writer_id=$2", intent, writer))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) throw new ExportProblem("write_intent_conflict"); owner = r.GetString(0); library = r.GetString(1); }
        await ExportDatabase.LockAsync(db, owner, library, ct);
        if (await ExecuteAsync(db, "UPDATE export_write_intents i SET storage_pending=true,storage_phase=$3,provider_receipt=NULL FROM export_requests r WHERE i.id=$1 AND i.writer_id=$2 AND i.request_id=r.id AND i.state='admitted' AND NOT i.writer_stopped AND NOT i.storage_pending AND r.state='preparing' AND r.generation=i.generation AND r.lease_id=i.writer_id AND r.lease_until>now()", ct, intent, writer, phase) == 0) throw new ExportProblem("cancelled");
        await tx.CommitAsync(ct);
    }
    public async Task StorageReturnedAsync(Guid intent, Guid writer, string? receipt, string? upload, string? version, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        if (await ExecuteAsync(db, "UPDATE export_write_intents SET storage_pending=false,provider_receipt=$3,multipart_id=COALESCE($4,multipart_id),object_version=COALESCE($5,object_version),writer_stopped=(storage_phase='complete') WHERE id=$1 AND writer_id=$2 AND storage_pending AND NOT writer_stopped", ct, intent, writer, receipt, upload, version) == 0) throw new ExportProblem("write_intent_conflict");
    }
    public async Task StopWriterAsync(Guid intent, Guid writer, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await ExecuteAsync(db, "UPDATE export_write_intents SET writer_stopped=true WHERE id=$1 AND writer_id=$2", ct, intent, writer);
    }
    public async Task SettleWriteAsync(Guid intent, Guid writer, string? version, bool knownOutcome, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        if (await ExecuteAsync(db, "UPDATE export_write_intents SET state=$3,object_version=COALESCE($4,object_version),settled_at=CASE WHEN $3='settled' THEN now() ELSE NULL END WHERE id=$1 AND writer_id=$2 AND state IN ('admitted','unknown') AND ($3='unknown' OR NOT storage_pending)", ct, intent, writer, knownOutcome ? "settled" : "unknown", version) == 0) throw new ExportProblem("write_intent_conflict");
    }
    public async Task<bool> CanAcknowledgeAsync(NpgsqlConnection db, string owner, string library, Guid eventId, CancellationToken ct)
    {
        await ExportDatabase.LockAsync(db, owner, library, ct);
        // Unknown writes require verifiable writer settlement; an expired lease is not sufficient.
        await using var cmd = Command(db, "SELECT asset_id,edit_id,category,content_sha256,generation,evidence_id FROM export_purge_tombstones WHERE event_id=$1 AND owner_id=$2 AND library_id=$3", eventId, owner, library);
        ExportPurgeEvent value;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) throw new ExportProblem("export_not_found", 404); value = new(eventId, owner, library, r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3).Trim(), r.GetInt64(4), r.GetGuid(5)); }
        var pending = false;
        await using (var requests = Command(db, "SELECT r.snapshot::text,i.state FROM export_requests r LEFT JOIN export_write_intents i ON i.request_id=r.id WHERE r.owner_id=$1 AND r.library_id=$2", owner, library))
        await using (var r = await requests.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { var snapshot = ExportDatabase.Decode<ExportSnapshot>(r.GetString(0)); if (snapshot.Items.Any(pin => value.Affects(pin, snapshot.PresetId)) && !r.IsDBNull(1) && r.GetString(1) != "cleaned") pending = true; }
        }
        return !pending;
    }
    public async Task<bool> AcknowledgeAsync(NpgsqlTransaction transaction, string owner, string library, Guid eventId, CancellationToken ct)
    {
        var db = transaction.Connection ?? throw new ExportProblem("purge_transaction_required");
        if (!await CanAcknowledgeAsync(db, owner, library, eventId, ct)) return false;
        await ExecuteAsync(db, "UPDATE export_purge_tombstones SET acknowledged_at=COALESCE(acknowledged_at,now()) WHERE event_id=$1 AND owner_id=$2 AND library_id=$3", ct, eventId, owner, library);
        return true;
    }
}
