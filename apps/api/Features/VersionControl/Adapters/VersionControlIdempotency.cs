using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

internal static class VersionControlIdempotency
{
    public static string Fingerprint(object request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    public static async Task<T?> ReadAsync<T>(NpgsqlConnection connection, string owner, string library, string key, string fingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new VersionControlProblem("idempotency_key_required", 400);
        await using var command = Command(connection, "SELECT fingerprint,result::text FROM vc_idempotency WHERE owner_id=$1 AND library_id=$2 AND idempotency_key=$3", owner, library, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return default;
        if (reader.GetString(0).Trim() != fingerprint) throw new VersionControlProblem("idempotency_key_reused");
        return JsonSerializer.Deserialize<T>(reader.GetString(1));
    }

    public static Task SaveAsync<T>(NpgsqlConnection connection, string owner, string library, string key, string fingerprint, string? commitId, T result, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "INSERT INTO vc_idempotency(owner_id,library_id,idempotency_key,fingerprint,commit_id,result) VALUES($1,$2,$3,$4,$5,$6::jsonb)", cancellationToken, owner, library, key, fingerprint, commitId, JsonSerializer.Serialize(result));
}
