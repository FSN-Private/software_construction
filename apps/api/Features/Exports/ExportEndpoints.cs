using Microsoft.AspNetCore.Mvc;
using Platform.Contracts;
using Platform.Contracts.Exports;
namespace Platform.Api.Features.Exports;

public static class ExportEndpoints
{
    public sealed record BasketInput(string Name);
    public sealed record BasketUpdate(string Name, long Revision, bool IsDefault);
    public sealed record ItemInput(long Revision, ExportSelection Selection);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/exports").WithMetadata(new ExportProtected()).RequireAuthorization().AddEndpointFilter<ExportGuard>();
        group.MapGet("/presets", () => Results.Ok(ExportPreset.Catalog));
        group.MapGet("/limits", ([FromServices] IConfiguration config) => Results.Ok(ExportConfiguration.Limits(config)));
        group.MapGet("/baskets", async (string library, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok((await service.ListAsync(Owner(c), library, ct)).Select(ExportPublic.Basket)));
        group.MapPost("/baskets", async (string library, BasketInput input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.CreateAsync(Owner(c), library, input.Name, ct))));
        group.MapGet("/baskets/{id:guid}", async (string library, Guid id, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.GetAsync(Owner(c), library, id, ct))));
        group.MapPatch("/baskets/{id:guid}", async (string library, Guid id, BasketUpdate input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.MutateAsync(Owner(c), library, id, input.Revision, "rename", input.Name, input.IsDefault, null, null, ct))));
        group.MapDelete("/baskets/{id:guid}", async (string library, Guid id, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.MutateAsync(Owner(c), library, id, revision, "delete", null, false, null, null, ct))));
        group.MapPost("/baskets/{id:guid}/items", async (string library, Guid id, ItemInput input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.MutateAsync(Owner(c), library, id, input.Revision, "add", null, false, input.Selection, null, ct))));
        group.MapDelete("/baskets/{id:guid}/items", async (string library, Guid id, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.MutateAsync(Owner(c), library, id, revision, "clear", null, false, null, null, ct))));
        group.MapDelete("/baskets/{id:guid}/items/{item:guid}", async (string library, Guid id, Guid item, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(ExportPublic.Basket(await service.MutateAsync(Owner(c), library, id, revision, "remove", null, false, null, item, ct))));
        group.MapPost("/baskets/{id:guid}/requests", async (string library, Guid id, ExportSubmit input, HttpContext c, [FromServices] ExportPreparationService service, CancellationToken ct) => Results.Accepted(value: ExportPublic.Request(await service.SubmitAsync(Owner(c), library, id, input, c.Request.Headers["Idempotency-Key"].ToString(), ct))));
        group.MapGet("/requests", async (string library, Guid? before, HttpContext c, [FromServices] ExportPreparationService service, CancellationToken ct) => Results.Ok((await service.ListAsync(Owner(c), library, ct, before)).Select(ExportPublic.Request)));
        group.MapGet("/requests/{id:guid}", async (string library, Guid id, HttpContext c, [FromServices] ExportPreparationService service, CancellationToken ct) => Results.Ok(ExportPublic.Request(await service.GetAsync(Owner(c), library, id, ct))));
        group.MapPost("/requests/{id:guid}/cancel", async (string library, Guid id, HttpContext c, [FromServices] ExportPreparationService service, CancellationToken ct) => Results.Ok(ExportPublic.Request(await service.CancelAsync(Owner(c), library, id, ct))));
        group.MapPost("/baskets/{id:guid}/items/{item:guid}/preview", async (string library, Guid id, Guid item, HttpContext c, [FromServices] ExportBasketService baskets, [FromServices] Platform.Api.Features.Rendering.RenderingService rendering, [FromServices] Npgsql.NpgsqlDataSource source, CancellationToken ct) =>
        {
            var owner = Owner(c); var basket = await baskets.GetAsync(owner, library, id, ct);
            var pin = basket.Items.SingleOrDefault(i => i.Pin.ItemId == item)?.Pin ?? throw new ExportProblem("export_not_found", 404);
            await using var db = await source.OpenConnectionAsync(ct); var compatibility = await rendering.ExportCompatibilityAsync(db, ct);
            var result = await rendering.RequestPinnedAsync(owner, library, pin, compatibility, ct);
            await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
            var available = await ExportSelectionService.AvailabilityBatchAsync(db, owner, library, [pin], false, ct);
            if (available[pin.ItemId] is { } unavailable) throw new ExportProblem(unavailable);
            c.Items["platform.idempotency.reissue"] = true;
            var locator = result.State == Platform.Contracts.Rendering.RenderState.Completed ? (await rendering.LocateAsync(owner, library, result.RequestId, ct)).Locator : null;
            await tx.CommitAsync(ct);
            return Results.Ok(new { state = result.State.ToString().ToLowerInvariant(), result.RequestId, result.Provenance, locator });
        });
        group.MapPost("/requests/{id:guid}/download-grants", async (string library, Guid id, HttpContext c, [FromServices] ExportDownloadService service, CancellationToken ct) => Results.Ok(await service.MintAsync(c, Owner(c), library, id, ct)));
        group.MapPost("/session/revoke", async (HttpContext c, [FromServices] ExportDownloadService service, CancellationToken ct) => { await service.RevokeAsync(c, Owner(c), ct); return Results.NoContent(); });
        group.MapGet("/requests/{id:guid}/manifest", async (string library, Guid id, HttpContext c, [FromServices] ExportPreparationService service, CancellationToken ct) => { var request = await service.GetAsync(Owner(c), library, id, ct); if (request.Outcomes.Any(o => o.Status == "pending")) throw new ExportProblem("export_not_ready"); return Results.Ok(ExportPublic.Manifest(new(1, request.Snapshot, request.Outcomes))); });
        app.MapPost("/v1/libraries/{library}/exports/requests/{id:guid}/download-session", async (string library, Guid id, HttpContext c, [FromServices] ExportDownloadService service, CancellationToken ct) => await service.ExchangeAsync(c, library, id, ct)).WithMetadata(new ExportNativeExchange()).RequireCors("exports-download").AddEndpointFilter<ExportDownloadGuard>();
        app.MapMethods("/v1/libraries/{library}/exports/requests/{id:guid}/download", ["GET", "HEAD"], async (string library, Guid id, HttpContext c, [FromServices] ExportDownloadService service, CancellationToken ct) => await service.StreamAsync(c, library, id, ct)).RequireCors("exports-download").AddEndpointFilter<ExportDownloadGuard>();
        app.MapGet("/v1/exports/operations", async (HttpContext c, [FromServices] ExportOperationsService service, CancellationToken ct) => Results.Ok(await service.GetAsync(c.User, ct))).WithMetadata(new ExportProtected()).RequireAuthorization().AddEndpointFilter<ExportDownloadGuard>();
        app.MapPost("/v1/exports/operations/{action}", async (string action, HttpContext c, [FromServices] ExportOperationsService service, CancellationToken ct) => Results.Ok(await service.SetAsync(c.User, action, ct))).WithMetadata(new ExportProtected()).RequireAuthorization().AddEndpointFilter<ExportDownloadGuard>();
    }
    public static string Owner(HttpContext c) => OwnerAuthorization.TryGetOwner(c.User, out var owner) ? owner : throw new ExportProblem("unauthenticated", 401);
}
public sealed record ExportNativeExchange;
public sealed class ExportDownloadGuard : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        try { return await next(invocation); }
        catch (ExportProblem error) when (!invocation.HttpContext.Response.HasStarted)
        {
            var c = invocation.HttpContext;
            var owner = c.Items["exports.audit_owner"]?.ToString() ?? (c.User.Identity?.IsAuthenticated == true ? c.User.FindFirst("sub")?.Value ?? c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous" : "anonymous");
            Platform.Api.Telemetry.PlatformTelemetry.ExportPolicyDenials.Add(1);
            if (c.RequestServices.GetService<IAuditWriter>() is { } audit) await audit.WriteAsync(AuditEvent.Create(owner, ExportDatabase.OperationId, "exports.download_denied", "denied", error.Code), c.RequestAborted);
            return RequestContracts.Problem(c, error.Status, error.Code, "Download unavailable");
        }
    }
}
public sealed class ExportGuard(IConfiguration config, ILogger<ExportGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var c = invocation.HttpContext; c.Response.Headers.CacheControl = "private, no-store";
        try
        {
            _ = ExportEndpoints.Owner(c);
            if (!config.GetValue<bool>("Exports:Enabled") && !c.Request.Path.Value!.EndsWith("/exports/session/revoke", StringComparison.Ordinal)) throw new ExportProblem("exports_disabled", 503);
            return await next(invocation);
        }
        catch (Exception error) when (error is ExportProblem or Platform.Contracts.VersionControl.VersionControlProblem or Platform.Api.Features.Rendering.RenderingProblem)
        {
            Platform.Api.Telemetry.PlatformTelemetry.ExportPolicyDenials.Add(1);
            var code = error is ExportProblem export ? export.Code : error is Platform.Api.Features.Rendering.RenderingProblem render ? render.Code : "selected_version_unavailable";
            var status = error is ExportProblem problem ? problem.Status : error is Platform.Api.Features.Rendering.RenderingProblem renderingProblem ? renderingProblem.Status : 404;
            var owner = c.User.Identity?.IsAuthenticated == true ? c.User.FindFirst("sub")?.Value ?? c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous" : "anonymous";
            if (c.RequestServices.GetService<IAuditWriter>() is { } audit)
                await audit.WriteAsync(AuditEvent.Create(owner, c.Items["platform.operation_id"]?.ToString() ?? ExportDatabase.OperationId, "exports.access_denied", "denied", code), c.RequestAborted);
            else logger.LogWarning("Export operation denied {ReasonCode}", code);
            return RequestContracts.Problem(c, status, code, "Export request unavailable");
        }
    }
}
