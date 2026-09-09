using Npgsql;
using Platform.Contracts;

namespace Platform.Api.Features.Ingest;

public static class IngestEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/import-batches", CreateAsync);
        app.MapGet("/v1/import-batches/{id:guid}", StatusAsync);
    }

    private static async Task<IResult> CreateAsync(ImportBatchRequest request, HttpContext context, NpgsqlDataSource? dataSource, IAuditWriter? audit, CancellationToken cancellationToken)
    {
        if (!OwnerAuthorization.TryGetOwner(context.User, out var ownerId)) return RequestContracts.Problem(context, 401, "unauthenticated", "Authentication required");
        if (dataSource is null || request.Items.Count is 0 || request.Items.Count > 1000) return RequestContracts.Problem(context, 400, "invalid_batch", "Batch must contain 1 to 1000 items");
        if (request.Items.Any(item => !new Platform.Storage.ContentAddress(item.Sha256, item.Length).IsValid)) return RequestContracts.Problem(context, 400, "checksum_mismatch", "Item checksum is invalid");
        var batchId = Guid.NewGuid(); var idempotencyKey = context.Request.Headers[RequestContracts.IdempotencyHeader].FirstOrDefault() ?? throw new InvalidOperationException("idempotency_key_required");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in request.Items)
        {
            var sessionId = Guid.NewGuid();
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO import_batches (id, owner_id, idempotency_key, atomic, state) VALUES ($1,$2,$3,$4,'created') ON CONFLICT (owner_id,idempotency_key) DO NOTHING; INSERT INTO upload_sessions (id,batch_id,owner_id,target_key,expected_sha256,expected_length,expires_at) VALUES ($5,$1,$2,$6,$7,$8,now()+interval '1 hour'); INSERT INTO import_batch_items (id,batch_id,upload_session_id,state) VALUES ($9,$1,$5,'uploading');";
            command.Parameters.AddWithValue(batchId); command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(idempotencyKey); command.Parameters.AddWithValue(request.Atomic); command.Parameters.AddWithValue(sessionId); command.Parameters.AddWithValue($"staging/{sessionId:N}"); command.Parameters.AddWithValue(item.Sha256); command.Parameters.AddWithValue(item.Length); command.Parameters.AddWithValue(Guid.NewGuid());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (audit is not null) await audit.WriteAsync(AuditEvent.Create(ownerId, context.Items["platform.operation_id"]?.ToString() ?? "op_unavailable", "import.batch_created", "accepted"), cancellationToken);
        return Results.Created($"/v1/import-batches/{batchId}", new { id = batchId, state = "created" });
    }

    private static async Task<IResult> StatusAsync(Guid id, HttpContext context, NpgsqlDataSource? dataSource, CancellationToken cancellationToken)
    {
        if (!OwnerAuthorization.TryGetOwner(context.User, out var ownerId)) return RequestContracts.Problem(context, 401, "unauthenticated", "Authentication required");
        if (dataSource is null) return RequestContracts.Problem(context, 503, "ingest_unavailable", "Ingest is not configured");
        await using var command = dataSource.CreateCommand("SELECT state FROM import_batches WHERE id=$1 AND owner_id=$2"); command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(ownerId);
        var state = await command.ExecuteScalarAsync(cancellationToken) as string; return state is null ? Results.NotFound() : Results.Ok(new { id, state });
    }
}

public sealed record ImportBatchRequest(bool Atomic, IReadOnlyList<ImportBatchItem> Items);
public sealed record ImportBatchItem(string Sha256, long Length);
