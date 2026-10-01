using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Platform.Api.Features.Gallery.Metadata;
using Platform.Api.Features.Gallery;
using Platform.Api.Features.VersionControl.Refs;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Gallery.Browse;

public sealed record GalleryCard(Guid AssetId, string Path, string Filename, string FileType, DateTime? CapturedAt,
    string? Camera, string? Lens, int? Rating, bool? Rejected, int? Width, int? Height, string ProjectionState,
    string PreviewState, Guid? PreviewRequestId);
public sealed record GalleryPage(IReadOnlyList<GalleryCard> Cards, long Count, string? NextCursor, long Generation,
    string Sort, string Direction, IReadOnlyDictionary<string, string[]> Filters);
public sealed record GalleryFilterValue(string Value, string Label);
public sealed record GalleryFilterValues(IReadOnlyList<GalleryFilterValue> Values, bool HasMore);
public sealed class GalleryProblem(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class GalleryBrowseService(NpgsqlDataSource db, IDataProtectionProvider protection, IConfiguration configuration)
{
    private readonly IDataProtector cursorProtector = protection.CreateProtector("gallery.v1.cursor");
    private static readonly HashSet<string> Controls = ["branch", "sort", "direction", "limit", "cursor"];
    private static readonly HashSet<string> Filters = ["captureFrom", "captureTo", "importFrom", "importTo", "path", "fileType",
        "camera", "lens", "focalLengthMin", "focalLengthMax", "apertureMin", "apertureMax", "shutterMin", "shutterMax",
        "isoMin", "isoMax", "widthMin", "widthMax", "heightMin", "heightMax", "orientation", "rating", "rejected",
        "color", "tag", "title", "description", "creator", "locationPresent", "latitudeMin", "latitudeMax", "longitudeMin", "longitudeMax"];
    private static readonly Dictionary<string, string> NumericColumns = new(StringComparer.Ordinal)
    {
        ["focalLength"] = "g.focal_length_mm",
        ["aperture"] = "g.aperture",
        ["shutter"] = "g.shutter_seconds",
        ["iso"] = "g.iso",
        ["width"] = "g.width",
        ["height"] = "g.height"
    };
    private sealed record Cursor(long Generation, long Revision, string Hash, string Sort, string Direction, string? Value1, string? Value2,
        Guid AssetId, DateTimeOffset ExpiresAt);

    public async Task<GalleryFilterValues> FilterValuesAsync(string owner, string library, string branchName, string facet, string? search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["Gallery:LibraryId"]) || library != configuration["Gallery:LibraryId"])
            throw new GalleryProblem("library_unavailable", 404);
        if (search?.Length > 100) throw new GalleryProblem("invalid_filter");
        var branch = RefNames.Normalize(branchName);
        var column = facet switch
        {
            "camera" => "g.camera",
            "lens" => "g.lens",
            "fileType" => "g.file_type",
            "rating" => "g.rating::text",
            "orientation" => "g.orientation::text",
            "focalLength" => "g.focal_length_mm::text",
            "aperture" => "g.aperture::text",
            "shutter" => "g.shutter_seconds::text",
            "iso" => "g.iso::text",
            "width" => "g.width::text",
            "height" => "g.height::text",
            "latitude" => "g.latitude::text",
            "longitude" => "g.longitude::text",
            "tag" or "color" => "f.text_value",
            _ => throw new GalleryProblem("invalid_filter")
        };
        var label = facet is "tag" or "color" ? "COALESCE(f.display_text,f.text_value)" : column;
        await using var connection = await db.OpenConnectionAsync(ct);
        if (facet is "latitude" or "longitude")
        {
            var hidden = await ScalarAsync(connection, "SELECT hide_geolocation FROM gallery_preferences WHERE owner_id=$1", ct, owner) as bool? ?? true;
            if (hidden) throw new GalleryProblem("location_hidden");
        }
        var generation = await ScalarAsync(connection,
            "SELECT generation FROM gallery_generations WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND state='active'",
            ct, owner, library, branch) as long?;
        if (generation is null) return new([], false);
        var multi = facet is "tag" or "color";
        var join = multi
            ? " JOIN gallery_facet_values f ON f.owner_id=g.owner_id AND f.library_id=g.library_id AND f.branch_name=g.branch_name AND f.generation=g.generation AND f.asset_id=g.asset_id AND f.facet=$5 AND f.value_state='present'"
            : "";
        var searchParameter = multi ? "$6" : "$5";
        var sql = $"""
            SELECT value,MIN(label) FROM (
              SELECT {column} AS value,{label} AS label
              FROM gallery_projection g JOIN assets a ON a.id=g.asset_id AND a.owner_id=g.owner_id{join}
              WHERE g.owner_id=$1 AND g.library_id=$2 AND g.branch_name=$3 AND g.generation=$4
                AND a.state IN ('active','restored') AND g.projection_state IN ('complete','stale')
            ) choices WHERE value IS NOT NULL AND value<>'' AND ({searchParameter}='' OR strpos(lower(value),lower({searchParameter}))>0)
            GROUP BY value ORDER BY value LIMIT 101
            """;
        var values = new List<GalleryFilterValue>();
        await using var command = multi
            ? Command(connection, sql, owner, library, branch, generation.Value, facet, search?.Trim() ?? "")
            : Command(connection, sql, owner, library, branch, generation.Value, search?.Trim() ?? "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values.Add(new(reader.GetString(0), reader.GetString(1)));
        var hasMore = values.Count > 100;
        if (hasMore) values.RemoveAt(values.Count - 1);
        return new(values, hasMore);
    }

    public async Task<GalleryPage> BrowseAsync(string owner, string library, IQueryCollection query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["Gallery:LibraryId"]) || library != configuration["Gallery:LibraryId"])
            throw new GalleryProblem("library_unavailable", 404);
        foreach (var key in query.Keys)
            if (!Controls.Contains(key) && !Filters.Contains(key)) throw new GalleryProblem("invalid_filter");
        foreach (var pair in query)
            if (pair.Value.Count > 1 && pair.Key is not ("tag" or "color")) throw new GalleryProblem("invalid_filter");
        var branch = RefNames.Normalize(Single(query, "branch") ?? "main");
        var sort = Single(query, "sort") ?? "captureDate";
        var direction = Single(query, "direction") ?? "desc";
        if (sort is not ("captureDate" or "importDate" or "filename" or "rating" or "dimensions") || direction is not ("asc" or "desc")) throw new GalleryProblem("invalid_sort");
        var limit = int.TryParse(Single(query, "limit") ?? "40", NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit) ? parsedLimit : 0;
        if (limit is < 1 or > 100) throw new GalleryProblem("invalid_limit");
        var filters = query.Where(pair => Filters.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Select(value => value ?? "").OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var key in filters.Keys) GalleryTelemetry.FacetUses.Add(1, new KeyValuePair<string, object?>("facet", key));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { owner, library, branch, filters, sort, direction }))));
        Cursor? cursor = null;
        var token = Single(query, "cursor");
        if (token is not null)
        {
            try { cursor = JsonSerializer.Deserialize<Cursor>(cursorProtector.Unprotect(token)); }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { throw new GalleryProblem("cursor_expired", 409); }
            if (cursor is null || cursor.Hash != hash || cursor.Sort != sort || cursor.Direction != direction || cursor.ExpiresAt < DateTimeOffset.UtcNow)
                throw new GalleryProblem("cursor_expired", 409);
        }
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var hideLocation = await ScalarAsync(connection, "SELECT hide_geolocation FROM gallery_preferences WHERE owner_id=$1", ct, owner) as bool? ?? true;
        if (hideLocation && filters.Keys.Any(key => key is "locationPresent" or "latitudeMin" or "latitudeMax" or "longitudeMin" or "longitudeMax"))
            throw new GalleryProblem("location_hidden");
        var generation = await ScalarAsync(connection, "SELECT generation FROM gallery_generations WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND state='active'", ct, owner, library, branch) as long?;
        if (generation is null) return new([], 0, null, 0, sort, direction, filters);
        var revision = (long)(await ScalarAsync(connection, "SELECT COALESCE((SELECT invalidation_revision FROM gallery_refresh WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND generation=$4),0)", ct, owner, library, branch, generation.Value) ?? 0L);
        if (cursor is not null && (cursor.Generation != generation || cursor.Revision != revision)) throw new GalleryProblem("cursor_expired", 409);
        var args = new List<object?> { owner, library, branch, generation.Value };
        string Param(object? value) { args.Add(value); return "$" + args.Count.ToString(CultureInfo.InvariantCulture); }
        var where = new StringBuilder("g.owner_id=$1 AND g.library_id=$2 AND g.branch_name=$3 AND g.generation=$4 AND a.state IN ('active','restored') AND g.projection_state IN ('complete','stale')");
        foreach (var filter in filters)
        {
            var key = filter.Key;
            var raw = filter.Value[0];
            if (key is "tag" or "color")
            {
                var values = filter.Value.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(GalleryMetadataReader.Normalize).Distinct(StringComparer.Ordinal).Take(20).ToArray();
                if (values.Length == 0) throw new GalleryProblem("invalid_filter");
                var conditions = values.Select(value => "f.text_value=" + Param(value));
                where.Append(" AND EXISTS (SELECT 1 FROM gallery_facet_values f WHERE f.owner_id=g.owner_id AND f.library_id=g.library_id AND f.branch_name=g.branch_name AND f.generation=g.generation AND f.asset_id=g.asset_id AND f.facet='" + key + "' AND f.value_state='present' AND (" + string.Join(" OR ", conditions) + "))");
            }
            else if (key is "path" or "camera" or "lens" or "title" or "description" or "creator")
            {
                if (raw.Length is < 1 or > 200) throw new GalleryProblem("invalid_filter");
                var text = GalleryMetadataReader.Normalize(raw).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
                where.Append(" AND EXISTS (SELECT 1 FROM gallery_facet_values f WHERE f.owner_id=g.owner_id AND f.library_id=g.library_id AND f.branch_name=g.branch_name AND f.generation=g.generation AND f.asset_id=g.asset_id AND f.facet='" + key + "' AND f.value_state='present' AND f.text_value LIKE " + Param("%" + text + "%") + " ESCAPE '\\')");
            }
            else if (key is "fileType")
            {
                if (raw is not ("jpeg" or "png" or "tiff" or "dng")) throw new GalleryProblem("invalid_filter");
                where.Append(" AND g.file_type=" + Param(raw));
            }
            else if (key is "captureFrom" or "captureTo" or "importFrom" or "importTo")
            {
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) throw new GalleryProblem("invalid_filter");
                var column = key.StartsWith("capture", StringComparison.Ordinal) ? "g.captured_at" : "g.imported_at";
                where.Append(" AND " + column + (key.EndsWith("From", StringComparison.Ordinal) ? ">=" : "<=") + Param(value.UtcDateTime));
            }
            else if (key.EndsWith("Min", StringComparison.Ordinal) || key.EndsWith("Max", StringComparison.Ordinal))
            {
                var baseName = key[..^3];
                if (key.EndsWith("Max", StringComparison.Ordinal)) baseName = key[..^3];
                var column = NumericColumns.GetValueOrDefault(baseName) ?? baseName switch { "latitude" => "g.latitude", "longitude" => "g.longitude", _ => throw new GalleryProblem("invalid_filter") };
                if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > 10000000) throw new GalleryProblem("invalid_filter");
                where.Append(" AND " + column + (key.EndsWith("Min", StringComparison.Ordinal) ? ">=" : "<=") + Param(value));
            }
            else if (key is "orientation" or "rating")
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < (key == "rating" ? 0 : 1) || value > (key == "rating" ? 5 : 8)) throw new GalleryProblem("invalid_filter");
                where.Append(" AND g." + key + "=" + Param(value));
            }
            else if (key is "rejected" or "locationPresent")
            {
                if (!bool.TryParse(raw, out var value)) throw new GalleryProblem("invalid_filter");
                where.Append(" AND g." + (key == "rejected" ? "rejected" : "has_location") + "=" + Param(value));
            }
        }
        var from = " FROM gallery_projection g JOIN assets a ON a.id=g.asset_id AND a.owner_id=g.owner_id WHERE " + where;
        long count;
        await using (var countCommand = Command(connection, "SELECT count(*)" + from, args.ToArray()))
            count = (long)(await countCommand.ExecuteScalarAsync(ct) ?? 0L);
        var dimensionsPresent = "g.width IS NOT NULL AND g.height IS NOT NULL";
        var sort1 = sort switch { "captureDate" => "g.captured_at", "importDate" => "g.imported_at", "filename" => "g.filename", "rating" => "g.rating", _ => $"CASE WHEN {dimensionsPresent} THEN g.width END" };
        var sort2 = sort == "dimensions" ? $"CASE WHEN {dimensionsPresent} THEN g.height END" : null;
        var valueExpression = sort1;
        var pageWhere = new StringBuilder(where.ToString());
        if (cursor is not null)
        {
            var idParam = Param(cursor.AssetId);
            if (cursor.Value1 is null) pageWhere.Append($" AND {valueExpression} IS NULL AND g.asset_id>{idParam}");
            else
            {
                object first = sort is "captureDate" or "importDate" ? DateTime.Parse(cursor.Value1, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToUniversalTime() :
                    sort == "filename" ? cursor.Value1 : int.Parse(cursor.Value1, CultureInfo.InvariantCulture);
                var firstParam = Param(first);
                var op = direction == "asc" ? ">" : "<";
                if (sort2 is null)
                    pageWhere.Append($" AND ({sort1} IS NULL OR {sort1}{op}{firstParam} OR ({sort1}={firstParam} AND g.asset_id>{idParam}))");
                else
                {
                    var secondParam = Param(int.Parse(cursor.Value2!, CultureInfo.InvariantCulture));
                    pageWhere.Append($" AND ({valueExpression} IS NULL OR {sort1}{op}{firstParam} OR ({sort1}={firstParam} AND ({sort2}{op}{secondParam} OR ({sort2}={secondParam} AND g.asset_id>{idParam}))))");
                }
            }
        }
        var limitParam = Param(limit + 1);
        var order = $"{valueExpression} IS NULL,{sort1} {direction.ToUpperInvariant()}" + (sort2 is null ? "" : $",{sort2} {direction.ToUpperInvariant()}") + ",g.asset_id ASC";
        var sql = "SELECT g.asset_id,g.display_path,g.filename,g.file_type,g.captured_at,g.camera,g.lens,g.rating,g.rejected,g.width,g.height,g.projection_state," + sort1 + (sort2 is null ? "" : "," + sort2) +
            ",thumb.id,thumb.state,thumb.source_sha256,thumb.xmp_sha256,g.original_sha256,g.xmp_sha256" +
            " FROM gallery_projection g JOIN assets a ON a.id=g.asset_id AND a.owner_id=g.owner_id" +
            " LEFT JOIN LATERAL (SELECT id FROM edit_identities WHERE asset_id=g.asset_id AND duplicate_ordinal=0 AND state IN ('active','restored') LIMIT 1) e ON true" +
            " LEFT JOIN LATERAL (SELECT r.id,r.state,p.source_sha256,p.xmp_sha256 FROM render_requests r LEFT JOIN renditions d ON d.request_id=r.id LEFT JOIN render_provenance p ON p.rendition_id=d.id WHERE r.owner_id=g.owner_id AND r.library_id=g.library_id AND r.asset_id=g.asset_id AND r.edit_id=e.id AND r.rendition='thumbnail-512' ORDER BY r.created_at DESC,r.id DESC LIMIT 1) thumb ON true" +
            " WHERE " + pageWhere + " ORDER BY " + order + " LIMIT " + limitParam;
        var cards = new List<GalleryCard>();
        string? last1 = null, last2 = null;
        Guid lastId = Guid.Empty;
        await using (var pageCommand = Command(connection, sql, args.ToArray()))
        await using (var reader = await pageCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var previewIndex = sort2 is null ? 13 : 14;
                var requestId = reader.IsDBNull(previewIndex) ? (Guid?)null : reader.GetGuid(previewIndex);
                var requestState = reader.IsDBNull(previewIndex + 1) ? null : reader.GetString(previewIndex + 1);
                var matching = requestState == "completed" && !reader.IsDBNull(previewIndex + 2) && !reader.IsDBNull(previewIndex + 3) && !reader.IsDBNull(previewIndex + 5) &&
                    reader.GetString(previewIndex + 2).Trim() == reader.GetString(previewIndex + 4).Trim() &&
                    reader.GetString(previewIndex + 3).Trim() == reader.GetString(previewIndex + 5).Trim();
                var previewState = matching ? "available" : requestState is "failed" or "unavailable" or "cancelled" ? "failed" : "pending";
                cards.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt16(7),
                    reader.IsDBNull(8) ? null : reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.GetString(11), previewState, requestId));
                if (cards.Count <= limit)
                {
                    lastId = reader.GetGuid(0);
                    last1 = reader.IsDBNull(12) ? null : reader.GetValue(12) is DateTime date
                        ? date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        : Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture);
                    last2 = sort2 is null || reader.IsDBNull(13) ? null : Convert.ToString(reader.GetValue(13), CultureInfo.InvariantCulture);
                }
            }
        }
        await transaction.CommitAsync(ct);
        var hasMore = cards.Count > limit;
        if (hasMore) cards.RemoveAt(cards.Count - 1);
        var next = hasMore ? cursorProtector.Protect(JsonSerializer.Serialize(new Cursor(generation.Value, revision, hash, sort, direction, last1, last2, lastId, DateTimeOffset.UtcNow.AddMinutes(30)))) : null;
        foreach (var group in cards.GroupBy(card => card.PreviewState))
            GalleryTelemetry.PreviewStates.Add(group.LongCount(), new KeyValuePair<string, object?>("state", group.Key));
        return new(cards, count, next, generation.Value, sort, direction, filters);
    }

    private static string? Single(IQueryCollection query, string key) => query.TryGetValue(key, out StringValues values) ? values.ToString() : null;
}
