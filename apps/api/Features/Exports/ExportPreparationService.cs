using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;
public sealed class ExportPreparationService(NpgsqlDataSource source, ExportBasketService baskets, ExportSelectionService selections)
{
    public async Task<ExportRequestView> SubmitAsync(string owner, string library, Guid basketId, ExportSubmit input, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ExportProblem("idempotency_key_required", 400);
        var preset = ExportPreset.Get(input.PresetId); var fingerprint = ExportIntegrity.Fingerprint(input);
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        await using (var cmd = Command(db, "SELECT id,fingerprint FROM export_requests WHERE owner_id=$1 AND library_id=$2 AND idempotency_key=$3", owner, library, key))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct)) { var id = r.GetGuid(0); if (r.GetString(1).Trim() != fingerprint) throw new ExportProblem("idempotency_key_reused"); await r.DisposeAsync(); return await GetAsync(db, owner, library, id, ct); }
        }
        if (await ScalarAsync(db, "SELECT 1 FROM export_operations WHERE singleton AND paused", ct) is not null) throw new ExportProblem("export_paused");
        if (preset.ConfirmationRequired && !input.MetadataConfirmed) throw new ExportProblem("metadata_confirmation_required");
        var basket = await baskets.GetAsync(db, owner, library, basketId, ct); if (basket.Revision != input.Revision) throw new ExportProblem("stale_basket");
        if (basket.Items.Count == 0) throw new ExportProblem("empty_basket");
        if ((basket.Items.Count >= 100 || basket.Items.Sum(i => i.Pin.Original.ByteLength) >= 1024L * 1024 * 1024) && !input.WarningAcknowledged) throw new ExportProblem("cost_warning_required");
        if (Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM export_requests WHERE owner_id=$1 AND library_id=$2 AND basket_id=$3 AND state NOT IN ('expired','cancelled','unavailable')", ct, owner, library, basketId)) >= 10) throw new ExportProblem("quota_exceeded");
        var requestId = Guid.NewGuid(); var snapshot = new ExportSnapshot(1, requestId, preset.Id, input.MetadataConfirmed, DateTimeOffset.UtcNow, basket.Items.Select(i => i.Pin).ToArray());
        await ExecuteAsync(db, "INSERT INTO export_requests(id,owner_id,library_id,basket_id,idempotency_key,fingerprint,snapshot) VALUES($1,$2,$3,$4,$5,$6,$7::jsonb)", ct, requestId, owner, library, basketId, key, fingerprint, ExportDatabase.Encode(snapshot));
        foreach (var pin in snapshot.Items)
        {
            var reason = await selections.AvailabilityAsync(db, owner, library, pin, preset.Format == "original", ct);
            await ExecuteAsync(db, "INSERT INTO export_results(request_id,item_id,outcome) VALUES($1,$2,$3::jsonb)", ct, requestId, pin.ItemId, ExportDatabase.Encode(new ExportOutcome(pin.ItemId, reason is null ? "pending" : "unavailable", reason, null, null, null)));
        }
        await AuditAsync(db, owner, "op_export", "exports.requested", requestId, "accepted", ct);
        var result = await GetAsync(db, owner, library, requestId, ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<ExportRequestView> GetAsync(string owner, string library, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); return await GetAsync(db, owner, library, id, ct);
    }
    public async Task<IReadOnlyList<ExportRequestView>> ListAsync(string owner, string library, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); var ids = new List<Guid>();
        await using (var cmd = Command(db, "SELECT id FROM export_requests WHERE owner_id=$1 AND library_id=$2 ORDER BY created_at DESC LIMIT 200", owner, library))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0)); }
        var result = new List<ExportRequestView>(); foreach (var id in ids) result.Add(await GetAsync(db, owner, library, id, ct)); return result;
    }
    public async Task<ExportRequestView> CancelAsync(string owner, string library, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        var current = await GetAsync(db, owner, library, id, ct);
        if (current.State is "completed" or "partial" or "failed") throw new ExportProblem("export_terminal");
        if (current.State is "pending" or "preparing" or "retryable" or "cancelling")
        {
            await ExecuteAsync(db, "UPDATE export_requests SET state='cancelling',reason='cancelled',generation=generation+1 WHERE id=$1; UPDATE export_download_grants SET revoked_at=now() WHERE request_id=$1", ct, id);
            foreach (var item in current.Outcomes.Where(i => i.Status == "pending")) await ExecuteAsync(db, "UPDATE export_results SET outcome=$3::jsonb WHERE request_id=$1 AND item_id=$2", ct, id, item.ItemId, ExportDatabase.Encode(new ExportOutcome(item.ItemId, "cancelled", "cancelled", null, null, null)));
            await AuditAsync(db, owner, "op_export", "exports.cancelled", id, "accepted", ct);
        }
        var result = await GetAsync(db, owner, library, id, ct); await tx.CommitAsync(ct); return result;
    }
    public static async Task<ExportRequestView> GetAsync(NpgsqlConnection db, string owner, string library, Guid id, CancellationToken ct)
    {
        Guid basket; string state; ExportSnapshot snapshot; DateTimeOffset created; DateTimeOffset? expiry; string? sha; long? length; string? reason;
        await using (var cmd = Command(db, "SELECT basket_id,state,snapshot::text,created_at,expires_at,package_sha256,package_bytes,reason FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3", id, owner, library))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new ExportProblem("export_not_found", 404);
            basket = r.GetGuid(0); state = r.GetString(1); snapshot = ExportDatabase.Decode<ExportSnapshot>(r.GetString(2)); created = r.GetFieldValue<DateTimeOffset>(3); expiry = r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4); sha = r.IsDBNull(5) ? null : r.GetString(5).Trim(); length = r.IsDBNull(6) ? null : r.GetInt64(6); reason = r.IsDBNull(7) ? null : r.GetString(7);
        }
        if (state is "completed" or "partial" && expiry <= DateTimeOffset.UtcNow) { state = "expired"; reason = "expired"; }
        var outcomes = new List<ExportOutcome>();
        await using (var cmd = Command(db, "SELECT outcome::text FROM export_results WHERE request_id=$1 ORDER BY item_id", id))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) outcomes.Add(ExportDatabase.Decode<ExportOutcome>(r.GetString(0))); }
        return new(id, basket, state, snapshot, outcomes, created, expiry, sha, length, reason);
    }
}
