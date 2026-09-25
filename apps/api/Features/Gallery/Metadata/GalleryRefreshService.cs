using System.Globalization;
using Npgsql;
using Platform.Storage;
using Platform.Api.Features.Gallery;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Gallery.Metadata;

public sealed class GalleryRefreshService(NpgsqlDataSource db, S3ObjectStore storage)
{
    public const int SchemaVersion = 1;

    public async Task RefreshAsync(string owner, string library, string branch, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var lockKey = $"gallery:{owner}:{library}:{branch}";
        if (await ScalarAsync(connection, "SELECT pg_try_advisory_lock(hashtextextended($1, 9))", ct, lockKey) is not true) return;
        try
        {
            var generation = await EnsureGenerationAsync(connection, owner, library, branch, ct);
            var total = (long)(await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE owner_id=$1 AND state IN ('active','restored')", ct, owner) ?? 0L);
            string priorState; Guid? after; long revision;
            await using (var checkpoint = Command(connection, "SELECT state,last_asset_id,invalidation_revision FROM gallery_refresh WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", owner, library, branch, generation))
            await using (var reader = await checkpoint.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("gallery_refresh_missing");
                priorState = reader.GetString(0); after = reader.IsDBNull(1) ? null : reader.GetGuid(1); revision = reader.GetInt64(2);
            }
            if (priorState != "running")
            {
                after = null;
                await ExecuteAsync(connection, "UPDATE gallery_refresh SET state='running',total=$5,completed=0,failed=0,last_asset_id=NULL,last_error_code=NULL,updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", ct, owner, library, branch, generation, total);
            }
            while (true)
            {
                var sources = await ReadSourcesAsync(connection, owner, library, branch, after, ct);
                if (sources.Count == 0) break;
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    after = source.AssetId;
                    try
                    {
                        if (!await IsCurrentAsync(connection, owner, library, branch, generation, source, ct))
                        {
                            await using var original = await storage.ReadVerifiedAsync(source.Original.Key, source.Original.Version, source.Original.Address, ct);
                            await using var xmp = source.Xmp is null ? null : await storage.ReadVerifiedAsync(source.Xmp.Key, source.Xmp.Version, source.Xmp.Address, ct);
                            var parsed = GalleryMetadataReader.Read(original, source.Format, xmp);
                            await PublishAsync(connection, owner, library, branch, generation, source, parsed, ct);
                        }
                        await ExecuteAsync(connection, "UPDATE gallery_refresh SET completed=completed+1,last_asset_id=$5,updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", ct, owner, library, branch, generation, source.AssetId);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or NpgsqlException)
                    {
                        await ExecuteAsync(connection, "UPDATE gallery_refresh SET failed=failed+1,last_asset_id=$5,last_error_code=$6,updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", ct, owner, library, branch, generation, source.AssetId, error is NpgsqlException ? "database_error" : "source_error");
                    }
                }
            }
            await using var transaction = await connection.BeginTransactionAsync(ct);
            bool incomplete, invalidated;
            await using (var status = Command(connection, "SELECT total,completed,failed,invalidation_revision,state FROM gallery_refresh WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4 FOR UPDATE", owner, library, branch, generation))
            await using (var reader = await status.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("gallery_refresh_missing");
                incomplete = reader.GetInt64(1) + reader.GetInt64(2) != reader.GetInt64(0) || reader.GetInt64(2) > 0;
                invalidated = reader.GetInt64(3) != revision || reader.GetString(4) != "running";
            }
            if (invalidated) { await transaction.RollbackAsync(ct); return; }
            if (incomplete)
            {
                await ExecuteAsync(connection, "UPDATE gallery_refresh SET state='failed',last_error_code='source_unavailable',updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", ct, owner, library, branch, generation);
                await transaction.CommitAsync(ct);
                GalleryTelemetry.RefreshFailed.Add(1);
                return;
            }
            var finished = await ExecuteAsync(connection, "UPDATE gallery_refresh SET state='complete',updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4 AND invalidation_revision=$5 AND state='running'", ct, owner, library, branch, generation, revision);
            if (finished != 1) { await transaction.RollbackAsync(ct); return; }
            await ExecuteAsync(connection, "UPDATE gallery_generations SET state='retired' WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND state='active' AND generation<>$4", ct, owner, library, branch, generation);
            await ExecuteAsync(connection, "UPDATE gallery_generations SET state='active',activated_at=now() WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4", ct, owner, library, branch, generation);
            await transaction.CommitAsync(ct);
            GalleryTelemetry.RefreshCompleted.Add(1);
        }
        finally
        {
            await ScalarAsync(connection, "SELECT pg_advisory_unlock(hashtextextended($1, 9))", CancellationToken.None, lockKey);
        }
    }

    private static async Task<long> EnsureGenerationAsync(NpgsqlConnection connection, string owner, string library, string branch, CancellationToken ct)
    {
        var existing = await ScalarAsync(connection, "SELECT generation FROM gallery_generations WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND state='active'", ct, owner, library, branch);
        if (existing is long active) return active;
        var generation = (long)(await ScalarAsync(connection, "SELECT COALESCE(max(generation),0)+1 FROM gallery_generations WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3", ct, owner, library, branch) ?? 1L);
        await ExecuteAsync(connection, "INSERT INTO gallery_generations(owner_id,library_id,branch_name,generation,schema_version,state) VALUES($1,$2,$3,$4,$5,'building') ON CONFLICT DO NOTHING", ct, owner, library, branch, generation, SchemaVersion);
        await ExecuteAsync(connection, "INSERT INTO gallery_refresh(owner_id,library_id,branch_name,generation,state) VALUES($1,$2,$3,$4,'queued') ON CONFLICT DO NOTHING", ct, owner, library, branch, generation);
        return generation;
    }

    private static async Task<IReadOnlyList<GallerySource>> ReadSourcesAsync(NpgsqlConnection connection, string owner, string library, string branch, Guid? after, CancellationToken ct, Guid? exact = null)
    {
        await using var command = Command(connection, """
            SELECT a.id,a.format,p.display_path,u.imported_at,
                   oc.sha256,oc.byte_length,oc.storage_key,oc.storage_version,
                   xc.sha256,xc.byte_length,xc.storage_key,xc.storage_version
            FROM assets a
            JOIN content_objects oc ON oc.sha256=a.original_sha256 AND oc.integrity_state='authoritative'
            JOIN LATERAL (SELECT display_path FROM asset_paths WHERE asset_id=a.id AND active ORDER BY effective_from DESC,id DESC LIMIT 1) p ON true
            LEFT JOIN LATERAL (SELECT min(verified_at) imported_at FROM upload_sessions WHERE asset_id=a.id AND state='completed' AND category<>'xmp') u ON true
            LEFT JOIN LATERAL (SELECT id,xmp_sha256 FROM edit_identities WHERE asset_id=a.id AND duplicate_ordinal=0 AND state IN ('active','restored') LIMIT 1) e ON true
            LEFT JOIN LATERAL (SELECT current_content_sha256 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND client_id='__gallery_web__' AND asset_id=a.id AND edit_id=e.id AND change_kind<>'deleted' LIMIT 1) w ON true
            LEFT JOIN LATERAL (SELECT content_sha256 FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3 AND asset_id=a.id AND edit_id=e.id LIMIT 1) v ON true
            LEFT JOIN content_objects xc ON xc.sha256=COALESCE(w.current_content_sha256,v.content_sha256,e.xmp_sha256) AND xc.integrity_state='authoritative'
            WHERE a.owner_id=$1 AND a.state IN ('active','restored') AND ($4::uuid IS NULL OR a.id>$4) AND ($5::uuid IS NULL OR a.id=$5)
              AND (COALESCE(w.current_content_sha256,v.content_sha256,e.xmp_sha256) IS NULL OR xc.sha256 IS NOT NULL)
            ORDER BY a.id LIMIT 100
            """, owner, library, branch, after, exact);
        var result = new List<GallerySource>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var original = new SourceObject(new(reader.GetString(4).Trim(), reader.GetInt64(5)), reader.GetString(6), reader.GetString(7));
            SourceObject? xmp = reader.IsDBNull(8) ? null : new(new(reader.GetString(8).Trim(), reader.GetInt64(9)), reader.GetString(10), reader.GetString(11));
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3), original, xmp));
        }
        return result;
    }

    private static async Task<bool> IsCurrentAsync(NpgsqlConnection connection, string owner, string library, string branch, long generation, GallerySource source, CancellationToken ct)
    {
        await using var command = Command(connection, "SELECT original_sha256,xmp_sha256,display_path,imported_at FROM gallery_projection WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4 AND asset_id=$5 AND projection_state='complete'", owner, library, branch, generation, source.AssetId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && reader.GetString(0).Trim() == source.Original.Address.Sha256 &&
            (reader.IsDBNull(1) ? null : reader.GetString(1).Trim()) == source.Xmp?.Address.Sha256 &&
            reader.GetString(2) == source.Path && (reader.IsDBNull(3) ? null : reader.GetDateTime(3)) == source.ImportedAt;
    }

    private static async Task PublishAsync(NpgsqlConnection connection, string owner, string library, string branch, long generation, GallerySource source, GalleryMetadata parsed, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await ReadSourcesAsync(connection, owner, library, branch, null, ct, source.AssetId);
        if (current.Count == 0 || current[0] != source) { await transaction.RollbackAsync(ct); return; }
        string? Value(string name) => parsed.Fields.GetValueOrDefault(name)?.State == "present" ? parsed.Fields[name].Value : null;
        int? Integer(string name) => int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        decimal? Number(string name) => decimal.TryParse(Value(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        DateTime? Captured() => DateTime.TryParse(Value("captureDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var value) ? value.ToUniversalTime() : null;
        var coordinates = Value("location")?.Split(',');
        decimal? latitude = coordinates is { Length: 2 } && decimal.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : null;
        decimal? longitude = coordinates is { Length: 2 } && decimal.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : null;
        await ExecuteAsync(connection, """
            INSERT INTO gallery_projection(owner_id,library_id,branch_name,generation,asset_id,original_sha256,xmp_sha256,projection_state,parse_error_code,display_path,filename,file_type,imported_at,captured_at,camera,lens,focal_length_mm,aperture,shutter_seconds,iso,width,height,orientation,rating,rejected,title,description,creator,has_location,latitude,longitude)
            VALUES($1,$2,$3,$4,$5,$6,$7,'complete',$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30)
            ON CONFLICT(owner_id,library_id,branch_name,generation,asset_id) DO UPDATE SET original_sha256=excluded.original_sha256,xmp_sha256=excluded.xmp_sha256,projection_state='complete',parse_error_code=excluded.parse_error_code,display_path=excluded.display_path,filename=excluded.filename,file_type=excluded.file_type,imported_at=excluded.imported_at,captured_at=excluded.captured_at,camera=excluded.camera,lens=excluded.lens,focal_length_mm=excluded.focal_length_mm,aperture=excluded.aperture,shutter_seconds=excluded.shutter_seconds,iso=excluded.iso,width=excluded.width,height=excluded.height,orientation=excluded.orientation,rating=excluded.rating,rejected=excluded.rejected,title=excluded.title,description=excluded.description,creator=excluded.creator,has_location=excluded.has_location,latitude=excluded.latitude,longitude=excluded.longitude,refreshed_at=now()
            """, ct, owner, library, branch, generation, source.AssetId, source.Original.Address.Sha256, source.Xmp?.Address.Sha256, parsed.ParseStatus == "complete" ? null : parsed.ParseStatus, source.Path, Path.GetFileName(source.Path), source.Format, source.ImportedAt, Captured(), Value("camera"), Value("lens"), Number("focalLength"), Number("aperture"), Number("shutter"), Integer("iso"), Integer("width"), Integer("height"), Integer("orientation"), Integer("rating"), Value("rejected") == "true" ? true : Value("rejected") == "false" ? false : null, Value("title"), Value("description"), Value("creator"), latitude is not null, latitude, longitude);
        await ExecuteAsync(connection, "DELETE FROM gallery_facet_values WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4 AND asset_id=$5", ct, owner, library, branch, generation, source.AssetId);
        var facets = parsed.Fields.Values.Concat(parsed.Tags).Concat(parsed.Colors).ToList();
        facets.Add(new("importDate", source.ImportedAt?.ToString("O", CultureInfo.InvariantCulture), "derived", source.ImportedAt is null ? "missing" : "present", "verified_import"));
        facets.Add(new("path", source.Path, "derived", "present"));
        facets.Add(new("filename", Path.GetFileName(source.Path), "derived", "present"));
        facets.Add(new("fileType", source.Format, "derived", "present"));
        facets.Add(new("locationPresent", latitude is null ? "false" : "true", "derived", "present"));
        foreach (var group in facets.GroupBy(facet => facet.Name))
        {
            var ordinal = 0;
            foreach (var facet in group)
            {
                var type = facet.Name switch
                {
                    "captureDate" or "importDate" => "date",
                    "rejected" or "locationPresent" => "bool",
                    "focalLength" or "aperture" or "shutter" or "iso" or "width" or "height" or "orientation" or "rating" => "number",
                    _ => "text"
                };
                DateTime? date = type == "date" && facet.State == "present" ? DateTime.Parse(facet.Value!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToUniversalTime() : null;
                decimal? number = type == "number" && facet.State == "present" ? decimal.Parse(facet.Value!, CultureInfo.InvariantCulture) : null;
                bool? boolean = type == "bool" && facet.State == "present" ? bool.Parse(facet.Value!) : null;
                var text = type == "text" && facet.State == "present" ? (facet.Name == "location" ? facet.Value : GalleryMetadataReader.Normalize(facet.Value!)) : null;
                await ExecuteAsync(connection, """
                    INSERT INTO gallery_facet_values(owner_id,library_id,branch_name,generation,asset_id,facet,ordinal,source,value_state,source_sha256,text_value,number_value,date_value,bool_value,timestamp_basis,display_text)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
                    """, ct, owner, library, branch, generation, source.AssetId, facet.Name, ordinal++, facet.Source, facet.State,
                    facet.Source == "xmp" && source.Xmp is not null ? source.Xmp.Address.Sha256 : source.Original.Address.Sha256,
                    text, number, date, boolean, facet.TimestampBasis, type == "text" && facet.State == "present" ? facet.Value : null);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private sealed record SourceObject(ContentAddress Address, string Key, string Version);
    private sealed record GallerySource(Guid AssetId, string Format, string Path, DateTime? ImportedAt, SourceObject Original, SourceObject? Xmp);
}
