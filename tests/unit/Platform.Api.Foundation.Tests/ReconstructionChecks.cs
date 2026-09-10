using Npgsql;
using Platform.Storage;
using Platform.Storage.Reconstruction;
using System.Text.Json;
using System.Text.Json.Nodes;
using static IngestChecks;

internal static class ReconstructionChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        var service = new ManifestService(db);
        var manifest = await service.ExportAsync("test-owner", "op_manifest", default);
        var downloaded = await service.GetAsync("test-owner", manifest.Id, default);
        var objects = manifest.Inventory.GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("storage_key").GetString()!, o => new ContentAddress(o.GetProperty("sha256").GetString()!.Trim(), o.GetProperty("byte_length").GetInt64()));
        Check(ManifestVerifier.Verify(manifest, downloaded, objects).Count == 0, "Canonical manifest survives PostgreSQL JSONB representation");
        await RejectAsync<IngestException>(() => service.GetAsync("another-owner", manifest.Id, default));
        foreach (var category in new[] { "assets", "edits", "paths", "holds", "audit", "provenance", "objects" })
        {
            var node = JsonNode.Parse(manifest.Inventory.GetRawText())!;
            node[category]!.AsArray().RemoveAt(0);
            var altered = manifest with { Inventory = JsonSerializer.SerializeToElement(node) };
            Check(ManifestVerifier.Verify(manifest, altered, objects).Contains(category + "_altered"), $"Detect missing {category}");
        }
        foreach (var (category, property) in new[] { ("assets", "handoff_id"), ("edits", "association_provenance"), ("edits", "asset_id"), ("paths", "display_path"), ("holds", "source_reference"), ("audit", "resource_id") })
        {
            var node = JsonNode.Parse(manifest.Inventory.GetRawText())!;
            node[category]![0]![property] = "tampered";
            Check(ManifestVerifier.Verify(manifest, manifest with { Inventory = JsonSerializer.SerializeToElement(node) }, objects).Contains(category + "_altered"), $"Detect altered {property}");
        }
        var missing = new Dictionary<string, ContentAddress>(objects); missing.Remove(missing.Keys.First());
        Check(ManifestVerifier.Verify(manifest, manifest, missing).Any(e => e.StartsWith("object_missing:")), "Missing bytes detected");
        var changed = new Dictionary<string, ContentAddress>(objects); changed[changed.Keys.First()] = new(new string('0', 64), 1);
        Check(ManifestVerifier.Verify(manifest, manifest, changed).Any(e => e.StartsWith("object_altered:")), "Changed bytes detected");
        changed = new(objects) { ["sha256/extra"] = new(new string('0', 64), 1) };
        Check(ManifestVerifier.Verify(manifest, manifest, changed).Any(e => e.StartsWith("object_extra:")), "Extra object detected");
        Check((await storage.InventoryAsync(default)).Count >= objects.Count, "Object store inventory enumerates recovery data");
        Console.WriteLine("Reconstruction checks passed: missing/altered/extra bytes and every required relationship/handoff/audit category.");
    }
}
