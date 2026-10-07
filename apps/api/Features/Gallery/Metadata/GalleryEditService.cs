using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Working;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Gallery.Metadata;

public sealed record GalleryEditRequest(string Branch, Guid? EditId, long ExpectedWorkingVersion, string? ExpectedBaseSha256,
    Dictionary<string, JsonElement> Changes);
public sealed record GalleryEditResult(Guid AssetId, Guid EditId, string Branch, string XmpSha256, long WorkingVersion,
    string WorkingChangeId, bool Uncommitted);

public sealed class GalleryEditService(NpgsqlDataSource db, S3ObjectStore storage, WorkingService working, IConfiguration configuration)
{
    public async Task<GalleryEditResult> PatchAsync(string owner, string library, Guid asset, GalleryEditRequest request, string operation, CancellationToken ct, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(configuration["Gallery:LibraryId"]) || library != configuration["Gallery:LibraryId"])
            throw new GalleryProblem("library_unavailable", 404);
        if (request.ExpectedWorkingVersion < 0 || request.Changes is null) throw new GalleryProblem("invalid_metadata_value");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200) throw new GalleryProblem("invalid_idempotency_key");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { asset, request })));
        var branch = RefNames.Normalize(request.Branch);
        Source source;
        await using (var connection = await db.OpenConnectionAsync(ct))
        {
            var replay = await ReplayAsync(connection, owner, library, idempotencyKey, fingerprint, ct);
            if (replay is not null) return replay;
            source = await ReadSourceAsync(connection, owner, library, branch, asset, request.EditId, ct);
        }
        if (source.WorkingVersion != request.ExpectedWorkingVersion || source.SelectedSha != request.ExpectedBaseSha256)
            throw new GalleryProblem("stale_working_state", 409);
        byte[] before;
        if (source.SelectedSha is null) before = [];
        else
        {
            await using var bytes = await storage.ReadVerifiedAsync(source.Key!, source.Version!, new(source.SelectedSha, source.Length!.Value), ct);
            if (bytes.Length > GalleryMetadataReader.MaximumXmpBytes) throw new GalleryProblem("xmp_too_large", 422);
            before = new byte[bytes.Length];
            await bytes.ReadExactlyAsync(before, ct);
        }
        var after = GalleryXmpPatcher.Patch(before, request.Changes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(after));
        var address = new ContentAddress(digest, after.Length);
        string storedVersion;
        await using (var bytes = new MemoryStream(after, writable: false))
            storedVersion = await storage.PublishVerifiedAsync(address, bytes, ct);
        await using (var connection = await db.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await LockOwnerAsync(connection, owner, ct);
            await VersionControlAdapters.LockAndCheckRecoveryPurgeDigestsAsync(connection, [digest], ct);
            var replay = await ReplayAsync(connection, owner, library, idempotencyKey, fingerprint, ct);
            if (replay is not null) return replay;
            var latest = await ReadSourceAsync(connection, owner, library, branch, asset, request.EditId, ct);
            if (latest.SelectedSha != source.SelectedSha || latest.WorkingVersion != source.WorkingVersion || latest.OriginalSha != source.OriginalSha)
                throw new GalleryProblem("stale_working_state", 409);
            var editId = source.EditId;
            if (editId is null)
            {
                editId = Guid.NewGuid();
                try
                {
                    await ExecuteAsync(connection, "INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,association_provenance) VALUES($1,$2,0,'gallery-web')", ct, editId.Value, asset);
                }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation) { throw new GalleryProblem("stale_working_state", 409); }
            }
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative') ON CONFLICT(sha256) DO NOTHING", ct,
                digest, after.Length, address.AuthoritativeKey, storedVersion);
            var valid = await ScalarAsync(connection, "SELECT 1 FROM content_objects WHERE sha256=$1 AND byte_length=$2 AND category='xmp' AND integrity_state='authoritative'", ct, digest, after.Length);
            if (valid is null) throw new GalleryProblem("content_not_authoritative", 409);
            await ExecuteAsync(connection, "INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING", ct,
                owner, library, branch, asset, editId.Value, digest);
            source = source with { EditId = editId };
            WorkingChangeView changed;
            try
            {
                changed = await working.PutInTransactionAsync(connection, transaction, owner, library, "__gallery_web__", new WorkingChangeRequest(asset, source.EditId, branch,
                    source.SelectedSha is null ? "added" : "modified", source.BaseSha, digest, source.Path, "active", request.ExpectedWorkingVersion), operation, ct);
            }
            catch (VersionControlProblem error) when (error.Code is "working_state_conflict" or "stale_head")
            {
                throw new GalleryProblem("stale_working_state", 409);
            }
            var result = new GalleryEditResult(asset, source.EditId!.Value, branch, digest, changed.Version, changed.Id.ToString("D"), true);
            await ExecuteAsync(connection, "INSERT INTO gallery_edit_idempotency(owner_id,library_id,idempotency_key,fingerprint,result) VALUES($1,$2,$3,$4,$5::jsonb)", ct,
                owner, library, idempotencyKey, fingerprint, JsonSerializer.Serialize(result));
            await AuditAsync(connection, owner, operation, "gallery.metadata_edited", asset, "accepted", ct);
            await transaction.CommitAsync(ct);
            return result;
        }
    }

    private static async Task<GalleryEditResult?> ReplayAsync(NpgsqlConnection connection, string owner, string library, string key, string fingerprint, CancellationToken ct)
    {
        await using var command = Command(connection, "SELECT fingerprint,result::text FROM gallery_edit_idempotency WHERE owner_id=$1 AND library_id=$2 AND idempotency_key=$3", owner, library, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(0).Trim() != fingerprint) throw new GalleryProblem("idempotency_conflict", 409);
        return JsonSerializer.Deserialize<GalleryEditResult>(reader.GetString(1)) ?? throw new GalleryProblem("idempotency_conflict", 409);
    }

    private static async Task<Source> ReadSourceAsync(NpgsqlConnection connection, string owner, string library, string branch, Guid asset, Guid? requestedEdit, CancellationToken ct)
    {
        if (branch != "main" && await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", ct, owner, library, branch) is null)
            throw new GalleryProblem("branch_unavailable", 404);
        await using var command = Command(connection, """
            SELECT a.original_sha256,p.display_path,e.id,e.xmp_sha256,w.current_content_sha256,v.content_sha256,w.version,w.base_content_sha256,
                   c.storage_key,c.storage_version,c.byte_length
            FROM assets a
            JOIN LATERAL (SELECT display_path FROM asset_paths WHERE asset_id=a.id AND active ORDER BY effective_from DESC,id DESC LIMIT 1) p ON true
            LEFT JOIN edit_identities e ON e.asset_id=a.id AND e.duplicate_ordinal=0 AND e.state IN ('active','restored')
            LEFT JOIN vc_working_changes w ON w.owner_id=$2 AND w.library_id=$3 AND w.branch_name=$4 AND w.client_id='__gallery_web__' AND w.asset_id=a.id AND w.edit_id=e.id
            LEFT JOIN LATERAL (SELECT content_sha256 FROM vc_materialized_state WHERE owner_id=$2 AND library_id=$3 AND normalized_ref=$4 AND asset_id=a.id AND edit_id=e.id LIMIT 1) v ON true
            LEFT JOIN content_objects c ON c.sha256=COALESCE(w.current_content_sha256,v.content_sha256,e.xmp_sha256) AND c.integrity_state='authoritative'
            WHERE a.id=$1 AND a.owner_id=$2 AND a.state IN ('active','restored') AND e.id IS NOT DISTINCT FROM $5
            """, asset, owner, library, branch, requestedEdit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new GalleryProblem("asset_unavailable", 404);
        var selected = reader.IsDBNull(4) ? reader.IsDBNull(5) ? reader.IsDBNull(3) ? null : reader.GetString(3).Trim() : reader.GetString(5).Trim() : reader.GetString(4).Trim();
        if (selected is not null && reader.IsDBNull(8)) throw new GalleryProblem("content_not_authoritative", 409);
        return new(reader.GetString(0).Trim(), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            selected, reader.IsDBNull(6) ? 0 : reader.GetInt64(6), reader.IsDBNull(7) ? selected : reader.GetString(7).Trim(),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt64(10));
    }

    private sealed record Source(string OriginalSha, string Path, Guid? EditId, string? SelectedSha, long WorkingVersion, string? BaseSha,
        string? Key, string? Version, long? Length);
}
