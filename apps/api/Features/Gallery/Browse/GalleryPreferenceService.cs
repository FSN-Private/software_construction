using Npgsql;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Gallery.Browse;

public sealed record GalleryPreference(bool HideGeolocation, long Version);
public sealed record GalleryLocation(decimal Latitude, decimal Longitude);
public sealed record GalleryPreferenceRequest(bool HideGeolocation, long ExpectedVersion);

public sealed class GalleryPreferenceService(NpgsqlDataSource db, IConfiguration configuration)
{
    public async Task<GalleryPreference> GetAsync(string owner, string library, CancellationToken ct)
    {
        CheckLibrary(library);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT hide_geolocation,version FROM gallery_preferences WHERE owner_id=$1", owner);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetBoolean(0), reader.GetInt64(1)) : new(true, 1);
    }

    public async Task<GalleryPreference> SetAsync(string owner, string library, GalleryPreferenceRequest request, CancellationToken ct, string? idempotencyKey = null)
    {
        CheckLibrary(library);
        if (request.ExpectedVersion < 1) throw new GalleryProblem("invalid_version");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200) throw new GalleryProblem("invalid_idempotency_key");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwnerAsync(connection, owner, ct);
        await using (var replay = Command(connection, "SELECT hide_geolocation,expected_version,result_version FROM gallery_preference_idempotency WHERE owner_id=$1 AND library_id=$2 AND idempotency_key=$3", owner, library, idempotencyKey))
        await using (var reader = await replay.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetBoolean(0) != request.HideGeolocation || reader.GetInt64(1) != request.ExpectedVersion) throw new GalleryProblem("idempotency_conflict", 409);
                return new(request.HideGeolocation, reader.GetInt64(2));
            }
        await ExecuteAsync(connection, "INSERT INTO gallery_preferences(owner_id) VALUES($1) ON CONFLICT DO NOTHING", ct, owner);
        var changed = await ExecuteAsync(connection, "UPDATE gallery_preferences SET hide_geolocation=$2,version=version+1,updated_at=now() WHERE owner_id=$1 AND version=$3", ct, owner, request.HideGeolocation, request.ExpectedVersion);
        if (changed != 1) throw new GalleryProblem("preference_conflict", 409);
        await ExecuteAsync(connection, "INSERT INTO gallery_preference_idempotency(owner_id,library_id,idempotency_key,hide_geolocation,expected_version,result_version) VALUES($1,$2,$3,$4,$5,$6)", ct,
            owner, library, idempotencyKey, request.HideGeolocation, request.ExpectedVersion, request.ExpectedVersion + 1);
        await RecordRecoveryChangeAsync(connection, transaction, owner, library, idempotencyKey,
            "gallery.preference.updated", "gallery-preference", owner, [], DateTimeOffset.UtcNow, ct);
        await transaction.CommitAsync(ct);
        return new(request.HideGeolocation, request.ExpectedVersion + 1);
    }

    public async Task<GalleryLocation> RevealAsync(string owner, string library, string branch, Guid asset, string operation, CancellationToken ct)
    {
        CheckLibrary(library);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockOwnerAsync(connection, owner, ct);
        decimal latitude, longitude;
        await using (var command = Command(connection, """
            SELECT g.latitude,g.longitude FROM gallery_projection g
            JOIN gallery_generations gen ON gen.owner_id=g.owner_id AND gen.library_id=g.library_id AND gen.branch_name=g.branch_name AND gen.generation=g.generation AND gen.state='active'
            JOIN assets a ON a.id=g.asset_id AND a.owner_id=g.owner_id AND a.state IN ('active','restored')
            WHERE g.owner_id=$1 AND g.library_id=$2 AND g.branch_name=$3 AND g.asset_id=$4 AND g.has_location
            """, owner, library, branch, asset))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new GalleryProblem("location_unavailable", 404);
            latitude = reader.GetDecimal(0); longitude = reader.GetDecimal(1);
        }
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, library, operation,
            "gallery.location_revealed", asset, "accepted", "gallery-location", asset.ToString("D"), [], ct);
        await transaction.CommitAsync(ct);
        return new(latitude, longitude);
    }

    private void CheckLibrary(string library)
    {
        if (string.IsNullOrWhiteSpace(configuration["Gallery:LibraryId"]) || library != configuration["Gallery:LibraryId"])
            throw new GalleryProblem("library_unavailable", 404);
    }
}
