using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Gallery.Browse;

public sealed record GalleryFieldValue(string Name, string? Text, decimal? Number, DateTime? Date, bool? Boolean,
    string Source, string State, string SourceSha256, string? TimestampBasis);
public sealed record GalleryWorkingContext(string Branch, long? WebVersion, string? WebXmpSha256, int DeviceConflictCount);
public sealed record GalleryPreview(string State, Guid? RequestId, string? FailureCode, string? SourceSha256,
    string? XmpSha256, string? SelectedVersion, string? RendererRelease, string? RendererArtifactDigest,
    string? Profile, string? MatrixVersion, DateTime? CompletedAt);
public sealed record GalleryAssetDetail(Guid AssetId, Guid? EditId, string Path, string FileType, string OriginalSha256,
    string? XmpSha256, long LifecycleVersion, string ProjectionState, string? ParseErrorCode,
    IReadOnlyList<GalleryFieldValue> Fields, GalleryWorkingContext Working, GalleryPreview Thumbnail);

public sealed class GalleryDetailService(NpgsqlDataSource db, IConfiguration configuration)
{
    public async Task<GalleryAssetDetail> GetAsync(string owner, string library, string branch, Guid asset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["Gallery:LibraryId"]) || library != configuration["Gallery:LibraryId"])
            throw new GalleryProblem("library_unavailable", 404);
        await using var connection = await db.OpenConnectionAsync(ct);
        var hideLocation = await ScalarAsync(connection, "SELECT hide_geolocation FROM gallery_preferences WHERE owner_id=$1", ct, owner) as bool? ?? true;
        Guid? edit; string path, format, original, state; string? xmp, error; long generation, lifecycle;
        await using (var command = Command(connection, """
            SELECT g.generation,g.display_path,g.file_type,g.original_sha256,g.xmp_sha256,g.projection_state,g.parse_error_code,a.lifecycle_version,e.id
            FROM gallery_projection g JOIN gallery_generations gen ON gen.owner_id=g.owner_id AND gen.library_id=g.library_id AND gen.branch_name=g.branch_name AND gen.generation=g.generation AND gen.state='active'
            JOIN assets a ON a.id=g.asset_id AND a.owner_id=g.owner_id AND a.state IN ('active','restored')
            LEFT JOIN edit_identities e ON e.asset_id=a.id AND e.duplicate_ordinal=0 AND e.state IN ('active','restored')
            WHERE g.owner_id=$1 AND g.library_id=$2 AND g.branch_name=$3 AND g.asset_id=$4
            """, owner, library, branch, asset))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new GalleryProblem("asset_unavailable", 404);
            generation = reader.GetInt64(0); path = reader.GetString(1); format = reader.GetString(2);
            original = reader.GetString(3).Trim(); xmp = reader.IsDBNull(4) ? null : reader.GetString(4).Trim();
            state = reader.GetString(5); error = reader.IsDBNull(6) ? null : reader.GetString(6);
            lifecycle = reader.GetInt64(7); edit = reader.IsDBNull(8) ? null : reader.GetGuid(8);
        }
        var fields = new List<GalleryFieldValue>();
        await using (var command = Command(connection, """
            SELECT facet,COALESCE(display_text,text_value),number_value,date_value,bool_value,source,value_state,source_sha256,timestamp_basis
            FROM gallery_facet_values WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4 AND asset_id=$5
              AND ($6=false OR facet NOT IN ('location','locationPresent'))
            ORDER BY facet,ordinal
            """, owner, library, branch, generation, asset, hideLocation))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                fields.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetBoolean(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7).Trim(), reader.IsDBNull(8) ? null : reader.GetString(8)));
        long? webVersion = null; string? webXmp = null;
        await using (var command = Command(connection, "SELECT version,current_content_sha256 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND asset_id=$4 AND edit_id IS NOT DISTINCT FROM $5 AND client_id='__gallery_web__'", owner, library, branch, asset, edit))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) { webVersion = reader.GetInt64(0); webXmp = reader.IsDBNull(1) ? null : reader.GetString(1).Trim(); }
        var deviceConflicts = (long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes d WHERE d.owner_id=$1 AND d.library_id=$2 AND d.branch_name=$3 AND d.asset_id=$4 AND d.edit_id IS NOT DISTINCT FROM $5 AND d.client_id<>'__gallery_web__' AND (d.change_kind='conflicted' OR EXISTS(SELECT 1 FROM vc_working_changes w WHERE w.owner_id=d.owner_id AND w.library_id=d.library_id AND w.branch_name=d.branch_name AND w.asset_id=d.asset_id AND w.edit_id IS NOT DISTINCT FROM d.edit_id AND w.client_id='__gallery_web__' AND w.current_content_sha256 IS DISTINCT FROM d.current_content_sha256))", ct, owner, library, branch, asset, edit) ?? 0L);
        var preview = await ThumbnailAsync(connection, owner, library, asset, edit, original, xmp, ct);
        return new(asset, edit, path, format, original, xmp, lifecycle, state, error, fields,
            new(branch, webVersion, webXmp, checked((int)deviceConflicts)), preview);
    }

    private static async Task<GalleryPreview> ThumbnailAsync(NpgsqlConnection connection, string owner, string library, Guid asset,
        Guid? edit, string original, string? xmp, CancellationToken ct)
    {
        if (edit is null || xmp is null) return Pending();
        await using var command = Command(connection, """
            SELECT r.id,r.state,a.failure_code,p.source_sha256,p.xmp_sha256,p.selected_version,p.renderer_release,
                   p.renderer_artifact_digest,p.profile,p.matrix_version,p.completed_at
            FROM render_requests r
            LEFT JOIN LATERAL (SELECT failure_code FROM render_attempts WHERE request_id=r.id ORDER BY attempt_number DESC LIMIT 1) a ON true
            LEFT JOIN renditions d ON d.request_id=r.id
            LEFT JOIN render_provenance p ON p.rendition_id=d.id
            WHERE r.owner_id=$1 AND r.library_id=$2 AND r.asset_id=$3 AND r.edit_id=$4 AND r.rendition='thumbnail-512'
            ORDER BY r.created_at DESC,r.id DESC LIMIT 1
            """, owner, library, asset, edit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Pending();
        var request = reader.GetGuid(0); var state = reader.GetString(1);
        var failure = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (state == "completed" && !reader.IsDBNull(3) && reader.GetString(3).Trim() == original &&
            !reader.IsDBNull(4) && reader.GetString(4).Trim() == xmp)
            return new("available", request, null, original, xmp, reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetDateTime(10));
        if (state is "failed" or "unavailable" or "cancelled") return new("failed", request, failure, null, null, null, null, null, null, null, null);
        return new("pending", request, null, null, null, null, null, null, null, null, null);
    }

    private static GalleryPreview Pending() => new("pending", null, null, null, null, null, null, null, null, null, null);
}
