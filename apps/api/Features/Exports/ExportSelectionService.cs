using Npgsql;
using Platform.Contracts.Exports;
using Platform.Contracts.Rendering;
using Platform.Api.Features.VersionControl.Query;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed class ExportSelectionService(NpgsqlDataSource source)
{
    public async Task<ExportPin> ResolveAsync(NpgsqlConnection db, string owner, string library, ExportSelection input, CancellationToken ct)
    {
        if (input.AssetId == Guid.Empty || input.EditId == Guid.Empty || string.IsNullOrWhiteSpace(input.SelectedVersion)) throw new ExportProblem("invalid_request", 400);
        if (input.SelectedVersion is "working" || input.SelectedVersion.StartsWith("working:", StringComparison.Ordinal)) throw new ExportProblem("committed_version_required");
        var reference = input.SelectedVersion == "current" ? "main" : input.SelectedVersion;
        var commit = await QueryService.ResolveAsync(db, owner, library, reference, ct);
        var graph = await QueryService.LoadAsync(db, owner, library, ct);
        if (!graph.TryGetValue(commit, out var selected)) throw new ExportProblem("selected_version_unavailable", 404);
        if (selected.Hidden && !input.AdvancedHistory) throw new ExportProblem("advanced_history_required");
        var state = GraphQueries.State(graph, commit);
        if (!state.TryGetValue($"{input.AssetId:D}/{input.EditId:D}", out var change) || change.Change == "deleted" || change.ContentSha256 is null) throw new ExportProblem("selected_version_unavailable", 404);
        await using var command = Command(db, "SELECT a.original_sha256,COALESCE(p.display_path,'image.original') FROM assets a JOIN edit_identities e ON e.asset_id=a.id LEFT JOIN LATERAL(SELECT display_path FROM asset_paths WHERE asset_id=a.id ORDER BY effective_from DESC,id DESC LIMIT 1) p ON true WHERE a.id=$1 AND e.id=$2 AND a.owner_id=$3", input.AssetId, input.EditId, owner);
        string original; string label;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) throw new ExportProblem("source_unavailable", 404);
            original = reader.GetString(0).Trim(); label = reader.GetString(1).Replace('\\', '/').Split('/')[^1];
        }
        var pin = new ExportPin(Guid.NewGuid(), input.AssetId, input.EditId, commit, await ContentAsync(db, original, ct), await ContentAsync(db, change.ContentSha256, ct), label, commit[..12], ExportIntegrity.IsSha256(reference) ? null : reference, input.AdvancedHistory, DateTimeOffset.UtcNow);
        pin.Validate();
        var reason = await AvailabilityAsync(db, owner, library, pin, false, ct);
        if (reason is not null) throw new ExportProblem(reason);
        return pin;
    }
    public async Task<string?> AvailabilityAsync(NpgsqlConnection db, string owner, string library, ExportPin pin, bool originals, CancellationToken ct)
    {
        return (await AvailabilityBatchAsync(db, owner, library, [pin], originals, ct))[pin.ItemId];
    }
    public static async Task<IReadOnlyDictionary<Guid, string?>> AvailabilityBatchAsync(NpgsqlConnection db, string owner, string library, IReadOnlyList<ExportPin> pins, bool originals, CancellationToken ct)
    {
        await using var cmd = Command(db, """
            WITH pins AS (SELECT p, (p->>'assetId')::uuid asset, (p->>'editId')::uuid edit FROM jsonb_array_elements($3::jsonb) p)
            SELECT (p->>'itemId')::uuid,
            CASE
              WHEN EXISTS(SELECT 1 FROM export_purge_tombstones t WHERE t.owner_id=$1 AND t.library_id=$2 AND t.asset_id=asset AND ((t.category='original' AND t.content_sha256=p->'original'->>'sha256') OR (NOT $4 AND t.category='xmp' AND t.edit_id=edit AND t.content_sha256=p->'xmp'->>'sha256'))) THEN 'permanently_unavailable'
              WHEN NOT EXISTS(SELECT 1 FROM assets a JOIN edit_identities e ON e.asset_id=a.id WHERE a.id=asset AND e.id=edit AND a.owner_id=$1 AND (a.state IN ('active','restored') OR a.state='recoverable-deleted' AND a.deleted_until>now() OR EXISTS(SELECT 1 FROM lifecycle_holds h WHERE h.asset_id=a.id AND (h.expires_at IS NULL OR h.expires_at>now()))) AND ($4 OR EXISTS(SELECT 1 FROM lifecycle_holds h WHERE h.asset_id=a.id AND h.source_reference='vc:'||(p->>'commitId') AND (h.expires_at IS NULL OR h.expires_at>now())))) THEN CASE WHEN $4 THEN 'source_unavailable' ELSE 'selected_version_unavailable' END
              WHEN NOT EXISTS(SELECT 1 FROM content_objects c WHERE c.sha256=p->'original'->>'sha256' AND c.storage_key=p->'original'->>'key' AND c.storage_version=p->'original'->>'version' AND c.byte_length=(p->'original'->>'byteLength')::bigint AND c.integrity_state='authoritative') THEN 'source_unavailable'
              WHEN NOT $4 AND NOT EXISTS(SELECT 1 FROM content_objects c WHERE c.sha256=p->'xmp'->>'sha256' AND c.storage_key=p->'xmp'->>'key' AND c.storage_version=p->'xmp'->>'version' AND c.byte_length=(p->'xmp'->>'byteLength')::bigint AND c.integrity_state='authoritative') THEN 'selected_version_unavailable'
              ELSE NULL END
            FROM pins
            """, owner, library, ExportDatabase.Encode(pins), originals);
        await using var reader = await cmd.ExecuteReaderAsync(ct); var result = new Dictionary<Guid, string?>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        return result;
    }
    public async Task<string?> AvailabilityAsync(string owner, string library, ExportPin pin, bool originals, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        return await AvailabilityAsync(db, owner, library, pin, originals, ct);
    }
    private static async Task<RenderInputObject> ContentAsync(NpgsqlConnection db, string sha, CancellationToken ct)
    {
        await using var cmd = Command(db, "SELECT storage_key,storage_version,byte_length FROM content_objects WHERE sha256=$1 AND integrity_state='authoritative' AND storage_version IS NOT NULL", sha);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new ExportProblem("source_unavailable", 404);
        return new(r.GetString(0), r.GetString(1), sha, r.GetInt64(2));
    }
}
