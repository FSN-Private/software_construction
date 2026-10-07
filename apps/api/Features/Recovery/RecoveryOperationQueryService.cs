using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Recovery;

namespace Platform.Api.Features.Recovery;

public sealed record RecoveryPointView(Guid RecoveryPointId, DateTimeOffset SnapshotCutoffUtc,
    DateTimeOffset VerifiedAtUtc, long ChangeSequence);
public sealed record RecoveryPointPage(IReadOnlyList<RecoveryPointView> Items, Guid? NextAfterPointId);
public sealed record RecoverySessionView(Guid SessionId, Guid RecoveryPointId, string State,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? CompletedAtUtc,
    string? ValidationDigest);

/// <summary>Bounded private status queries. Listing an attested point or session never
/// grants restore, recovered-content or promotion authority.</summary>
public sealed class RecoveryOperationQueryService(NpgsqlDataSource source, IConfiguration configuration,
    TimeProvider? clock = null)
{
    private string Library()
    {
        if (!RecoveryHealthService.IsEnabled(configuration)) throw new RecoveryUnavailableException();
        return RecoveryHealthService.ConfiguredLibrary(configuration)!;
    }

    public async Task<RecoveryPointPage> ListPointsAsync(string owner, Guid? afterPointId, CancellationToken ct)
    {
        var library = Library();
        if (!RecoveryHealthService.TryGetTrust(configuration, out var trust)) throw new RecoveryUnavailableException();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT p.point_id,p.captured_at_utc,a.worker_id,a.signing_key_id,a.signing_public_key_sha256,
              a.payload_sha256,a.canonical_payload,a.signature
            FROM recovery_points p LEFT JOIN recovery_point_attestations a ON a.point_id=p.point_id
            WHERE p.owner_id=$1 AND p.library_id=$2 AND p.state='verified' AND
              ($3::uuid IS NULL OR (p.captured_at_utc,p.point_id)<
                (SELECT q.captured_at_utc,q.point_id FROM recovery_points q
                 WHERE q.point_id=$3 AND q.owner_id=$1 AND q.library_id=$2))
            ORDER BY p.captured_at_utc DESC,p.point_id DESC LIMIT 101
            """, connection);
        query.Parameters.AddWithValue(owner);
        query.Parameters.AddWithValue(library);
        query.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,
            Value=afterPointId is { } id ? id : DBNull.Value });
        await using var reader = await query.ExecuteReaderAsync(ct);
        var items = new List<RecoveryPointView>();
        Guid? next = null;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var scanned = 0;
        while (await reader.ReadAsync(ct))
        {
            if (++scanned > 100) break;
            next = reader.GetGuid(0);
            if (reader.IsDBNull(2)) continue;
            try
            {
                var catalog = RecoveryCatalogCodec.Verify(new(1,"recovery_point",reader.GetString(2),reader.GetString(3),
                    reader.GetString(4).Trim(),reader.GetString(5).Trim(),Convert.ToBase64String((byte[])reader[6]),
                    Convert.ToBase64String((byte[])reader[7])),trust).Payload;
                if (catalog.RecoveryPointId != next || catalog.OwnerId != owner || catalog.LibraryId != library ||
                    catalog.CapturedAtUtc != reader.GetFieldValue<DateTimeOffset>(1) ||
                    catalog.VerifiedAtUtc > now || catalog.SnapshotCutoffUtc > now) continue;
                items.Add(new(catalog.RecoveryPointId,catalog.SnapshotCutoffUtc,catalog.VerifiedAtUtc,catalog.ChangeSequence));
            }
            catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException) { }
        }
        return new(items,scanned>100 ? next : null);
    }

    public async Task<RecoverySessionView?> GetSessionAsync(string owner, Guid sessionId, CancellationToken ct)
    {
        var library = Library();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("""
            SELECT session_id,recovery_point_id,state,created_at_utc,expires_at_utc,completed_at_utc,validation_digest
            FROM recovery_sessions WHERE session_id=$1 AND owner_id=$2 AND library_id=$3
            """, connection);
        query.Parameters.AddWithValue(sessionId);
        query.Parameters.AddWithValue(owner);
        query.Parameters.AddWithValue(library);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var state = reader.GetString(2);
        var expiry = reader.GetFieldValue<DateTimeOffset>(4);
        if (expiry <= (clock ?? TimeProvider.System).GetUtcNow() && state is not ("promoted" or "aborted" or "failed"))
            state = "expired";
        return new(reader.GetGuid(0),reader.GetGuid(1),state,reader.GetFieldValue<DateTimeOffset>(3),expiry,
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),reader.IsDBNull(6) ? null : reader.GetString(6).Trim());
    }
}
