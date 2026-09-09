using Npgsql;

namespace Platform.Storage;

public static class IngestDb
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object?[] values)
    {
        // Named parameters let Npgsql safely split our fixed SQL batches into statements.
        var command = new NpgsqlCommand(System.Text.RegularExpressions.Regex.Replace(sql, @"\$(\d+)", "@p$1"), connection);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("p" + (i + 1), values[i] ?? DBNull.Value);
        return command;
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteScalarAsync(ct);
    }

    // ponytail: serialize a single owner's lifecycle decisions; partition by asset if measured throughput needs it.
    public static Task<int> LockOwnerAsync(NpgsqlConnection connection, string owner, CancellationToken ct) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1, 5))", ct, owner);

    public static Task<int> AuditAsync(NpgsqlConnection connection, string owner, string operation, string type, Guid resource, string outcome, CancellationToken ct, string? code = null) =>
        ExecuteAsync(connection, "INSERT INTO audit_events(id,owner_id,operation_id,event_type,resource_id,outcome,reason_code) VALUES($1,$2,$3,$4,$5,$6,$7)", ct, Guid.NewGuid(), owner, operation, type, resource, outcome, code);
}

public sealed class IngestException(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
