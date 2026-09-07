using Npgsql;

namespace Platform.Api.Contracts;

public sealed class IdempotencyStore(NpgsqlDataSource dataSource)
{
    public async Task<IdempotencyResult?> GetAsync(string ownerId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT fingerprint, reservation_id, status_code, response_body, response_content_type, reissue_response FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("idempotency_key_reused");
        return reader.IsDBNull(2) ? new(reader.GetGuid(1), null, null, null, false) : new(reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
    }

    public async Task<Guid?> ReserveAsync(string ownerId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var reservationId = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("INSERT INTO idempotency_results (owner_id, idempotency_key, fingerprint, reservation_id) VALUES ($1, $2, $3, $4) ON CONFLICT (owner_id, idempotency_key) DO UPDATE SET fingerprint = EXCLUDED.fingerprint, reservation_id = EXCLUDED.reservation_id, created_at = now() WHERE idempotency_results.status_code IS NULL AND idempotency_results.created_at < now() - interval '15 minutes' RETURNING reservation_id");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(fingerprint); command.Parameters.AddWithValue(reservationId);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    public async Task ReleaseAsync(string ownerId, string key, Guid reservationId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2 AND reservation_id = $3 AND status_code IS NULL");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(reservationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteAsync(string ownerId, string key, Guid reservationId, int statusCode, string response, string contentType, bool reissue, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE idempotency_results SET status_code = $4, response_body = $5, response_content_type = $6, reissue_response = $7, completed_at = now() WHERE owner_id = $1 AND idempotency_key = $2 AND reservation_id = $3");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(reservationId); command.Parameters.AddWithValue(statusCode); command.Parameters.AddWithValue(response); command.Parameters.AddWithValue(contentType); command.Parameters.AddWithValue(reissue);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ResetAsync(string ownerId, string key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2 AND reissue_response AND status_code IS NOT NULL RETURNING 1");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}

public sealed record IdempotencyResult(Guid ReservationId, int? StatusCode, string? Response, string? ContentType, bool Reissue);
