using Npgsql;

namespace Platform.Api.Contracts;

public sealed class IdempotencyStore(NpgsqlDataSource dataSource)
{
    public async Task<IdempotencyResult?> GetAsync(string ownerId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT fingerprint, status_code, response_body, reissue_response FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("idempotency_key_reused");
        return reader.IsDBNull(1) ? new(null, null, false) : new(reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3));
    }

    public async Task<bool> ReserveAsync(string ownerId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO idempotency_results (owner_id, idempotency_key, fingerprint) VALUES ($1, $2, $3) ON CONFLICT (owner_id, idempotency_key) DO UPDATE SET fingerprint = EXCLUDED.fingerprint, created_at = now() WHERE idempotency_results.status_code IS NULL AND idempotency_results.created_at < now() - interval '15 minutes' RETURNING 1");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(fingerprint);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task ReleaseAsync(string ownerId, string key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2 AND status_code IS NULL");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ReleaseStaleAsync(string ownerId, string key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2 AND status_code IS NULL AND created_at < now() - interval '15 minutes' RETURNING 1");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task CompleteAsync(string ownerId, string key, int statusCode, string response, bool reissue, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE idempotency_results SET status_code = $3, response_body = $4, reissue_response = $5, completed_at = now() WHERE owner_id = $1 AND idempotency_key = $2");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(statusCode); command.Parameters.AddWithValue(response); command.Parameters.AddWithValue(reissue);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ResetAsync(string ownerId, string key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM idempotency_results WHERE owner_id = $1 AND idempotency_key = $2 AND reissue_response AND status_code IS NOT NULL RETURNING 1");
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(key);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}

public sealed record IdempotencyResult(int? StatusCode, string? Response, bool Reissue);
