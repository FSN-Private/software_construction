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
        var group = app.MapGroup("/v1/libraries/{library}/exports").RequireAuthorization().AddEndpointFilter<ExportGuard>();
        group.MapGet("/presets", () => Results.Ok(ExportPreset.Catalog));
        group.MapGet("/baskets", async (string library, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.ListAsync(Owner(c), library, ct)));
        group.MapPost("/baskets", async (string library, BasketInput input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.CreateAsync(Owner(c), library, input.Name, ct)));
        group.MapGet("/baskets/{id:guid}", async (string library, Guid id, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.GetAsync(Owner(c), library, id, ct)));
        group.MapPatch("/baskets/{id:guid}", async (string library, Guid id, BasketUpdate input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.MutateAsync(Owner(c), library, id, input.Revision, "rename", input.Name, input.IsDefault, null, null, ct)));
        group.MapDelete("/baskets/{id:guid}", async (string library, Guid id, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.MutateAsync(Owner(c), library, id, revision, "delete", null, false, null, null, ct)));
        group.MapPost("/baskets/{id:guid}/items", async (string library, Guid id, ItemInput input, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.MutateAsync(Owner(c), library, id, input.Revision, "add", null, false, input.Selection, null, ct)));
        group.MapDelete("/baskets/{id:guid}/items", async (string library, Guid id, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.MutateAsync(Owner(c), library, id, revision, "clear", null, false, null, null, ct)));
        group.MapDelete("/baskets/{id:guid}/items/{item:guid}", async (string library, Guid id, Guid item, long revision, HttpContext c, [FromServices] ExportBasketService service, CancellationToken ct) => Results.Ok(await service.MutateAsync(Owner(c), library, id, revision, "remove", null, false, null, item, ct)));
    }
    public static string Owner(HttpContext c) => OwnerAuthorization.TryGetOwner(c.User, out var owner) ? owner : throw new ExportProblem("unauthenticated", 401);
}
public sealed class ExportGuard(IConfiguration config, ILogger<ExportGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var c = invocation.HttpContext; c.Response.Headers.CacheControl = "private, no-store";
        try
        {
            _ = ExportEndpoints.Owner(c);
            if (!config.GetValue<bool>("Exports:Enabled")) throw new ExportProblem("exports_disabled", 503);
            return await next(invocation);
        }
        catch (Exception error) when (error is ExportProblem or Platform.Contracts.VersionControl.VersionControlProblem)
        {
            var code = error is ExportProblem export ? export.Code : "selected_version_unavailable";
            var status = error is ExportProblem problem ? problem.Status : 404;
            if (OwnerAuthorization.TryGetOwner(c.User, out var owner) && c.RequestServices.GetService<IAuditWriter>() is { } audit)
                await audit.WriteAsync(AuditEvent.Create(owner, c.Items["platform.operation_id"]?.ToString() ?? "op_export", "exports.access_denied", "denied", code), c.RequestAborted);
            else logger.LogWarning("Export operation denied {ReasonCode}", code);
            return RequestContracts.Problem(c, status, code, "Export request unavailable");
        }
    }
}
