using Platform.Contracts;
using Platform.Storage;

namespace Platform.Api.Features.Ingest;

public static class IngestEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1").AddEndpointFilter<IngestGuard>();
        group.MapPost("/import-batches", async (ImportRequest request, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            var result = await Service(services).CreateAsync(Owner(context), context.Request.Headers[RequestContracts.IdempotencyHeader].ToString(), Operation(context), request, ct);
            return Results.Created($"/v1/import-batches/{result.Id}", result);
        });
        group.MapGet("/import-batches/{id:guid}", async (Guid id, HttpContext context, IServiceProvider services, CancellationToken ct) => Results.Ok(await Service(services).StatusAsync(Owner(context), id, ct)));
        group.MapPut("/upload-sessions/{id:guid}/parts/{number:int}", async (Guid id, int number, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            var version = Version(context);
            if (context.Request.ContentLength is null or < 1 or > S3ObjectStore.PartSize) throw new IngestException("invalid_part", 400);
            var buffer = new byte[(int)context.Request.ContentLength.Value];
            await context.Request.Body.ReadExactlyAsync(buffer, ct);
            return Results.Ok(await Service(services).PutPartAsync(Owner(context), id, version, number, buffer, context.Request.Headers["X-Content-SHA256"].ToString(), Operation(context), ct));
        });
        group.MapPost("/upload-sessions/{id:guid}/complete", async (Guid id, HttpContext context, IServiceProvider services, CancellationToken ct) => Results.Ok(await Service(services).CompleteAsync(Owner(context), id, Version(context), Operation(context), ct)));
        group.MapDelete("/upload-sessions/{id:guid}", async (Guid id, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            await Service(services).CancelAsync(Owner(context), id, Version(context), Operation(context), ct);
            return Results.Ok(new { id, state = "cancelled" });
        });
        group.MapPost("/upload-sessions/{id:guid}/transfer", async (Guid id, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            var issuer = services.GetService<TransferGrantIssuer>() ?? throw new IngestException("ingest_unavailable", 503);
            var grant = await issuer.IssueAsync(id, Owner(context), Version(context), DateTime.UtcNow, ct);
            await services.GetRequiredService<IAuditWriter>().WriteAsync(AuditEvent.Create(Owner(context), Operation(context), "upload.transfer_authorized", "accepted"), ct);
            context.Items["platform.idempotency.reissue"] = true;
            return Results.Ok(new { sessionId = id, expiresAt = grant.ExpiresAtUtc, partSize = S3ObjectStore.PartSize, partUrl = $"/v1/upload-sessions/{id}/parts/{{number}}" });
        });
    }

    private static ImportService Service(IServiceProvider services) => services.GetService<ImportService>() ?? throw new IngestException("ingest_unavailable", 503);
    public static string Owner(HttpContext context) => OwnerAuthorization.TryGetOwner(context.User, out var owner) ? owner : throw new IngestException("unauthenticated", 401);
    public static string Operation(HttpContext context) => context.Items["platform.operation_id"]?.ToString() ?? "op_unavailable";
    public static long Version(HttpContext context) => RequestContracts.TryGetLifecycleVersion(context, out var version) ? version : throw new IngestException("lifecycle_version_required", 400);
}

public sealed class IngestGuard(ILogger<IngestGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        try
        {
            _ = IngestEndpoints.Owner(context);
            if (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" && !RequestContracts.HasIdempotencyKey(context)) throw new IngestException("idempotency_key_required", 400);
            return await next(invocation);
        }
        catch (IngestException e)
        {
            if (OwnerAuthorization.TryGetOwner(context.User, out var owner) && context.RequestServices.GetService<IAuditWriter>() is { } audit)
                await audit.WriteAsync(AuditEvent.Create(owner, IngestEndpoints.Operation(context), "request.rejected", "denied", e.Code), context.RequestAborted);
            if (e.Code == "integrity_error") logger.LogError("Stored content failed verification {AuditEventType} {OperationId}", "ingest.integrity.drift", IngestEndpoints.Operation(context));
            return RequestContracts.Problem(context, e.Status, e.Code, e.Code.Replace('_', ' '));
        }
        catch (ArgumentException e) { var code = e.Message.StartsWith("invalid_windows_path", StringComparison.Ordinal) ? "invalid_windows_path" : "invalid_request"; return RequestContracts.Problem(context, 400, code, "Invalid request"); }
        catch (InvalidDataException) { return RequestContracts.Problem(context, 422, "checksum_mismatch", "Content verification failed"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation) { return RequestContracts.Problem(context, 409, "path_collision", "Destination is already in use"); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or Amazon.S3.AmazonS3Exception or IOException) { return RequestContracts.Problem(context, 503, "storage_retryable", "Storage unavailable; retry with the same request"); }
        catch (InvalidOperationException e) when (e.Message == "upload_session_not_authorized") { return RequestContracts.Problem(context, 403, e.Message, "Upload session is not available"); }
    }
}
