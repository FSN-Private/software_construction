using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

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

    public static async Task<Guid> AuditAndRecordRecoveryChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string owner,
        string library,
        string operation,
        string eventType,
        Guid resource,
        string outcome,
        string entityType,
        string entityId,
        IReadOnlyCollection<string> contentDigests,
        CancellationToken ct,
        string? code = null)
    {
        var eventId = Guid.NewGuid();
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(id,owner_id,operation_id,event_type,resource_id,outcome,reason_code)
            VALUES($1,$2,$3,$4,$5,$6,$7)
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue(eventId);
            audit.Parameters.AddWithValue(owner);
            audit.Parameters.AddWithValue(operation);
            audit.Parameters.AddWithValue(eventType);
            audit.Parameters.AddWithValue(resource);
            audit.Parameters.AddWithValue(outcome);
            audit.Parameters.AddWithValue((object?)code ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await RecordRecoveryChangeAsync(connection, transaction, owner, library, operation,
            eventType, entityType, entityId, contentDigests, DateTimeOffset.UtcNow, ct);
        return eventId;
    }

    public static async Task<long> RecordRecoveryChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string owner,
        string library,
        string operation,
        string eventType,
        string entityType,
        string entityId,
        IReadOnlyCollection<string> contentDigests,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLedgerText(owner, nameof(owner), 200);
        ValidateLedgerText(library, nameof(library), 200);
        ValidateLedgerText(operation, nameof(operation), 200);
        ValidateLedgerText(eventType, nameof(eventType), 120);
        ValidateLedgerText(entityType, nameof(entityType), 120);
        ValidateLedgerText(entityId, nameof(entityId), 200);
        if (changedAtUtc == default || changedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("recovery_change_timestamp_must_be_utc");
        if (contentDigests is null || contentDigests.Any(digest => digest.Length != 64 || digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new InvalidDataException("recovery_change_digest_invalid");
        var references = contentDigests.Order(StringComparer.Ordinal).ToArray();
        if (references.Distinct(StringComparer.Ordinal).Count() != references.Length)
            throw new InvalidDataException("recovery_change_digest_duplicate");
        var serializedReferences = JsonSerializer.Serialize(references);

        await using (var create = new NpgsqlCommand("INSERT INTO recovery_change_watermarks(owner_id,library_id) VALUES($1,$2) ON CONFLICT DO NOTHING", connection, transaction))
        {
            create.Parameters.AddWithValue(owner);
            create.Parameters.AddWithValue(library);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        long previous;
        await using (var lockRow = new NpgsqlCommand("SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id=$2 FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue(owner);
            lockRow.Parameters.AddWithValue(library);
            previous = (long)(await lockRow.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("recovery_change_watermark_missing"));
        }

        var sequence = checked(previous + 1);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_change_ledger(owner_id,library_id,sequence,operation_id,event_type,entity_type,entity_id,object_references,changed_at_utc)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(owner);
            insert.Parameters.AddWithValue(library);
            insert.Parameters.AddWithValue(sequence);
            insert.Parameters.AddWithValue(operation);
            insert.Parameters.AddWithValue(eventType);
            insert.Parameters.AddWithValue(entityType);
            insert.Parameters.AddWithValue(entityId);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = serializedReferences });
            insert.Parameters.AddWithValue(changedAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE recovery_change_watermarks
            SET last_sequence=$3, latest_changed_at_utc=CASE
                WHEN latest_changed_at_utc IS NULL OR latest_changed_at_utc<$4 THEN $4 ELSE latest_changed_at_utc END
            WHERE owner_id=$1 AND library_id=$2 AND last_sequence=$5
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(owner);
            update.Parameters.AddWithValue(library);
            update.Parameters.AddWithValue(sequence);
            update.Parameters.AddWithValue(changedAtUtc);
            update.Parameters.AddWithValue(previous);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("recovery_change_watermark_conflict");
        }
        return sequence;
    }

    private static void ValidateLedgerText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new InvalidDataException($"recovery_change_{name}_invalid");
    }
}

public sealed class IngestException(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
