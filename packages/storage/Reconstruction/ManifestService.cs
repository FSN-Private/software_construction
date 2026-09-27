using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Storage.Reconstruction;

public sealed class ManifestService(NpgsqlDataSource db)
{
    public async Task<Manifest> ExportAsync(string owner, string operation, CancellationToken ct, string? key = null)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await LockOwnerAsync(connection, owner, ct);
        if (key is not null && await ScalarAsync(connection, "SELECT id FROM reconstruction_manifests WHERE owner_id=$1 AND idempotency_key=$2", ct, owner, key) is Guid existing)
        {
            await transaction.CommitAsync(ct);
            return await GetAsync(owner, existing, ct);
        }
        var id = Guid.NewGuid();
        await AuditAsync(connection, owner, operation, "manifest.exported", id, "accepted", ct);
        var inventory = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var queries = new Dictionary<string, string>
        {
            ["assets"] = "SELECT a.* FROM assets a WHERE owner_id=$1 ORDER BY id",
            ["edits"] = "SELECT e.* FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE a.owner_id=$1 ORDER BY e.id",
            ["paths"] = "SELECT p.* FROM asset_paths p WHERE owner_id=$1 ORDER BY id",
            ["holds"] = "SELECT h.* FROM lifecycle_holds h JOIN assets a ON a.id=h.asset_id WHERE a.owner_id=$1 ORDER BY h.id",
            ["audit"] = "SELECT e.* FROM audit_events e WHERE owner_id=$1 ORDER BY id",
            ["provenance"] = "SELECT s.id,s.batch_id,s.asset_id,s.edit_id,s.expected_sha256,s.expected_length,s.category,s.operation_id,s.verified_at FROM upload_sessions s WHERE s.owner_id=$1 AND s.state='completed' ORDER BY s.id",
            ["merge_provenance"] = "SELECT m.* FROM lineage_merge_content m WHERE m.owner_id=$1 ORDER BY m.library_id,m.asset_id,m.edit_id,m.sha256",
            ["objects"] = "SELECT c.* FROM content_objects c WHERE c.sha256 IN (SELECT original_sha256 FROM assets WHERE owner_id=$1 UNION SELECT e.xmp_sha256 FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE a.owner_id=$1 UNION SELECT g.sha256 FROM gallery_web_content g WHERE g.owner_id=$1 UNION SELECT m.sha256 FROM lineage_merge_content m WHERE m.owner_id=$1 UNION SELECT ce.content_sha256 FROM vc_commit_entries ce JOIN vc_commits vc ON vc.id=ce.commit_id WHERE vc.owner_id=$1 AND ce.content_sha256 IS NOT NULL UNION SELECT ce.base_content_sha256 FROM vc_commit_entries ce JOIN vc_commits vc ON vc.id=ce.commit_id WHERE vc.owner_id=$1 AND ce.base_content_sha256 IS NOT NULL) ORDER BY c.sha256"
        };
        foreach (var (category, sql) in queries)
        {
            var json = (string)(await ScalarAsync(connection, $"SELECT COALESCE(json_agg(row_to_json(records)), '[]')::text FROM ({sql}) records", ct, owner))!;
            inventory[category] = JsonSerializer.Deserialize<JsonElement>(json);
        }
        var canonical = ManifestVerifier.Canonical(JsonSerializer.SerializeToElement(inventory));
        var digest = ManifestVerifier.Digest(canonical);
        await ExecuteAsync(connection, "INSERT INTO reconstruction_manifests(id,version,owner_id,inventory_sha256,inventory,idempotency_key) VALUES($1,1,$2,$3,$4::jsonb,$5)", ct, id, owner, digest, canonical, key);
        await transaction.CommitAsync(ct);
        return new(id, 1, digest, JsonSerializer.Deserialize<JsonElement>(canonical));
    }

    public async Task<Manifest> GetAsync(string owner, Guid id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT version,inventory_sha256,inventory::text FROM reconstruction_manifests WHERE id=$1 AND owner_id=$2", id, owner);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new IngestException("not_found", 404);
        return new(id, r.GetInt32(0), r.GetString(1).Trim(), JsonSerializer.Deserialize<JsonElement>(r.GetString(2)));
    }
}

public sealed record Manifest(Guid Id, int Version, string Sha256, JsonElement Inventory);

public static class ManifestVerifier
{
    public static string Digest(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    public static string Canonical(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    public static IReadOnlyList<string> Verify(Manifest trusted, Manifest recovered, IReadOnlyDictionary<string, ContentAddress> observed)
    {
        var errors = new List<string>();
        if (trusted.Version != 1 || recovered.Version != trusted.Version) errors.Add("manifest_version");
        if (Digest(Canonical(trusted.Inventory)) != trusted.Sha256) errors.Add("trusted_manifest_corrupt");
        if (recovered.Sha256 != trusted.Sha256 || Digest(Canonical(recovered.Inventory)) != trusted.Sha256) errors.Add("inventory_altered");
        foreach (var section in trusted.Inventory.EnumerateObject())
            if (!recovered.Inventory.TryGetProperty(section.Name, out var candidate) || Canonical(candidate) != Canonical(section.Value)) errors.Add(section.Name + "_altered");
        var expected = trusted.Inventory.GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("storage_key").GetString()!, o => new ContentAddress(o.GetProperty("sha256").GetString()!.Trim(), o.GetProperty("byte_length").GetInt64()), StringComparer.Ordinal);
        foreach (var (key, address) in expected)
            if (!observed.TryGetValue(key, out var actual)) errors.Add("object_missing:" + address.Sha256);
            else if (actual != address) errors.Add("object_altered:" + address.Sha256);
        foreach (var key in observed.Keys) if (!expected.ContainsKey(key)) errors.Add("object_extra:" + key);
        return errors;
    }
}
