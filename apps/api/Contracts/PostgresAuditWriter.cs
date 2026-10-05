using Npgsql;
using Platform.Contracts;
using Platform.Storage;

namespace Platform.Api.Contracts;

public sealed class PostgresAuditWriter(NpgsqlDataSource dataSource, IConfiguration configuration) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("INSERT INTO audit_events (id, owner_id, operation_id, event_type, outcome, reason_code) VALUES ($1, $2, $3, $4, $5, $6)", connection, transaction);
        command.Parameters.AddWithValue(auditEvent.Id);
        command.Parameters.AddWithValue(auditEvent.OwnerId);
        command.Parameters.AddWithValue(auditEvent.OperationId);
        command.Parameters.AddWithValue(auditEvent.EventType);
        command.Parameters.AddWithValue(auditEvent.Outcome);
        command.Parameters.AddWithValue((object?)auditEvent.ReasonCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await IngestDb.RecordRecoveryChangeAsync(connection, transaction, auditEvent.OwnerId,
            configuration["Recovery:LibraryId"] ?? configuration["Gallery:LibraryId"] ?? "main",
            auditEvent.OperationId, auditEvent.EventType, "audit-event", auditEvent.Id.ToString("D"),
            [], DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
