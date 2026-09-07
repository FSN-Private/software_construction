using Npgsql;

namespace Platform.Storage;

public sealed class TransferGrantIssuer(NpgsqlDataSource dataSource)
{
    public async Task<TransferGrant> IssueAsync(Guid sessionId, string ownerId, long lifecycleVersion, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT expected_sha256, expected_length, expires_at FROM upload_sessions WHERE id = $1 AND owner_id = $2 AND lifecycle_version = $3 AND state = 'uploading' AND expires_at > $4");
        command.Parameters.AddWithValue(sessionId); command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(lifecycleVersion); command.Parameters.AddWithValue(nowUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("upload_session_not_authorized");
        var expires = reader.GetDateTime(2);
        return new TransferGrant(sessionId, ownerId, new ContentAddress(reader.GetString(0).Trim(), reader.GetInt64(1)), expires < nowUtc.AddMinutes(15) ? expires : nowUtc.AddMinutes(15));
    }
}
