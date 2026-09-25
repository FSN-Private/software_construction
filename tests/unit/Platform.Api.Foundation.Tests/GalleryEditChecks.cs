using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.Gallery.Metadata;
using Platform.Api.Features.VersionControl.Working;
using Platform.Storage;
using static Platform.Storage.IngestDb;

internal static class GalleryEditChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gallery:LibraryId"] = "main" }).Build();
        var service = new GalleryEditService(db, storage, new WorkingService(db, storage), configuration);
        Guid asset, edit; string baseSha, originalSha;
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = Command(connection, "SELECT a.id,e.id,e.xmp_sha256,a.original_sha256 FROM assets a JOIN edit_identities e ON e.asset_id=a.id AND e.duplicate_ordinal=0 AND e.state IN ('active','restored') WHERE a.owner_id='test-owner' AND a.state IN ('active','restored') AND e.xmp_sha256 IS NOT NULL AND EXISTS(SELECT 1 FROM asset_paths p WHERE p.asset_id=a.id AND p.active) ORDER BY a.id LIMIT 1"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Gallery edit fixture sidecar missing");
            asset = reader.GetGuid(0); edit = reader.GetGuid(1); baseSha = reader.GetString(2).Trim(); originalSha = reader.GetString(3).Trim();
        }
        using var title = JsonDocument.Parse("{\"title\":\"Web title\"}");
        var first = await service.PatchAsync("test-owner", "main", asset,
            new("main", edit, 0, baseSha, Changes(title)), "op_gallery_edit", default, "gallery-edit-first");
        Check(first.Uncommitted && first.WorkingVersion == 1 && first.XmpSha256 != baseSha, "Web edit is an uncommitted versioned XMP object");
        var replay = await service.PatchAsync("test-owner", "main", asset,
            new("main", edit, 0, baseSha, Changes(title)), "op_gallery_edit", default, "gallery-edit-first");
        Check(replay == first, "Idempotent gallery edit replay returns the same working change");
        using var changedTitle = JsonDocument.Parse("{\"title\":\"Different title\"}");
        await RejectAsync(() => service.PatchAsync("test-owner", "main", asset,
            new("main", edit, 0, baseSha, Changes(changedTitle)), "op_gallery_edit", default, "gallery-edit-first"), "idempotency_conflict");
        await RejectAsync(() => service.PatchAsync("test-owner", "main", asset,
            new("main", edit, 0, baseSha, Changes(title)), "op_gallery_edit", default, "gallery-edit-stale"), "stale_working_state");
        using var tags = JsonDocument.Parse("{\"tags\":[\"Web tag\"]}");
        var second = await service.PatchAsync("test-owner", "main", asset,
            new("main", edit, 1, first.XmpSha256, Changes(tags)), "op_gallery_edit", default, "gallery-edit-second");
        Check(second.WorkingVersion == 2, "Second edit advances working version without overwriting it");
        await using (var connection = await db.OpenConnectionAsync())
        {
            Check(await ScalarAsync(connection, "SELECT original_sha256 FROM assets WHERE id=$1", default, asset) as string == originalSha, "Gallery edit preserves original image SHA-256");
            Check(await ScalarAsync(connection, "SELECT current_content_sha256 FROM vc_working_changes WHERE owner_id='test-owner' AND client_id='__gallery_web__' AND asset_id=$1", default, asset) as string == second.XmpSha256, "Feature-006 working row holds web XMP");
            await using var metadata = Command(connection, "SELECT storage_key,storage_version,byte_length FROM content_objects WHERE sha256=$1", second.XmpSha256);
            await using var reader = await metadata.ExecuteReaderAsync();
            Check(await reader.ReadAsync(), "Web XMP is authoritative content");
            await using var bytes = await storage.ReadVerifiedAsync(reader.GetString(0), reader.GetString(1), new(second.XmpSha256, reader.GetInt64(2)), default);
            using var text = new StreamReader(bytes, Encoding.UTF8);
            var xmp = await text.ReadToEndAsync();
            Check(xmp.Contains("history_modversion", StringComparison.Ordinal) && xmp.Contains("Web title", StringComparison.Ordinal) && xmp.Contains("Web tag", StringComparison.Ordinal), "Two patches retain unrelated darktable content");
        }
        Guid bareAsset;
        await using (var connection = await db.OpenConnectionAsync())
            bareAsset = (Guid)(await ScalarAsync(connection, "SELECT a.id FROM assets a WHERE a.owner_id='test-owner' AND a.state IN ('active','restored') AND NOT EXISTS(SELECT 1 FROM edit_identities e WHERE e.asset_id=a.id) ORDER BY a.id LIMIT 1", default) ?? throw new InvalidOperationException("No sidecar-free fixture"));
        using var stars = JsonDocument.Parse("{\"rating\":3}");
        var created = await service.PatchAsync("test-owner", "main", bareAsset,
            new("main", null, 0, null, Changes(stars)), "op_gallery_edit", default, "gallery-edit-created");
        Check(created.EditId != Guid.Empty && created.WorkingVersion == 1, "First web edit creates a sidecar identity and working change");
    }

    private static Dictionary<string, JsonElement> Changes(JsonDocument document) =>
        document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Gallery edit: " + label); }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (GalleryProblem error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Gallery edit: expected " + code);
    }
}
