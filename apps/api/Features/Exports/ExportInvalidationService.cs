using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;
/// <summary>Trusted in-process feature 011 adapter only. No HTTP publisher is exposed.</summary>
public sealed class ExportInvalidationService(NpgsqlDataSource source)
{
    public async Task ApplyAsync(NpgsqlConnection db, ExportPurgeEvent value, CancellationToken ct)
    {
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
            await AuditAsync(db, value.OwnerId, "op_export", "exports.permanently_unavailable", request, "accepted", ct);
        }
    }
    public async Task<Guid> AdmitWriteAsync(string owner, string library, Guid request, long generation, Guid writer, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        if (await ScalarAsync(db, "SELECT 1 FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND generation=$4 AND lease_id=$5 AND state='preparing' AND lease_until>now()", ct, request, owner, library, generation, writer) is null) throw new ExportProblem("cancelled");
        var id = Guid.NewGuid();
        await ExecuteAsync(db, "INSERT INTO export_write_intents(id,request_id,generation,writer_id,object_key,state) VALUES($1,$2,$3,$4,$5,'admitted')", ct, id, request, generation, writer, $"exports/{request:N}/{id:N}/package.zip");
        await tx.CommitAsync(ct); return id;
    }
    public async Task SettleWriteAsync(Guid intent, Guid writer, string? version, bool knownOutcome, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        if (await ExecuteAsync(db, "UPDATE export_write_intents SET state=$3,object_version=$4,settled_at=CASE WHEN $3='settled' THEN now() ELSE NULL END WHERE id=$1 AND writer_id=$2 AND state IN ('admitted','unknown')", ct, intent, writer, knownOutcome ? "settled" : "unknown", version) == 0) throw new ExportProblem("write_intent_conflict");
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
}
