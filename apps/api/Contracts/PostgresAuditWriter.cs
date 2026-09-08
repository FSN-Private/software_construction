using Npgsql;
using Platform.Contracts;

namespace Platform.Api.Contracts;

public sealed class PostgresAuditWriter(NpgsqlDataSource dataSource) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO audit_events (id, owner_id, operation_id, event_type, outcome, reason_code) VALUES ($1, $2, $3, $4, $5, $6)");
        command.Parameters.AddWithValue(auditEvent.Id);
        command.Parameters.AddWithValue(auditEvent.OwnerId);
        command.Parameters.AddWithValue(auditEvent.OperationId);
        command.Parameters.AddWithValue(auditEvent.EventType);
        command.Parameters.AddWithValue(auditEvent.Outcome);
        command.Parameters.AddWithValue((object?)auditEvent.ReasonCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
