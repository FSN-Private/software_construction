namespace Platform.Contracts;

/// <summary>Payload-safe record of a security or lifecycle decision.</summary>
public sealed record AuditEvent(
    Guid Id,
    string OwnerId,
    string OperationId,
    string EventType,
    string Outcome,
    string? ReasonCode = null)
{
    public static AuditEvent Create(string ownerId, string operationId, string eventType, string outcome, string? reasonCode = null) =>
        new(Guid.NewGuid(), ownerId, operationId, eventType, outcome, reasonCode);
}

public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
