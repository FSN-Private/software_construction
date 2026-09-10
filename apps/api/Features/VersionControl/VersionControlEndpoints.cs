using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Working;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Operations;
using Platform.Api.Features.VersionControl.Rewrite;
using Platform.Api.Features.VersionControl.Visibility;
using Platform.Api.Features.VersionControl.MergePublishing;
using Platform.Api.Features.VersionControl.Projection;
using Platform.Api.Telemetry;
using Platform.Contracts;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl;

public static class VersionControlEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/version-control").RequireAuthorization().AddEndpointFilter<VersionControlGuard>();
        group.MapGet("/working/{branch}", async (HttpContext context, string library, string branch, CancellationToken cancellationToken) =>
            Results.Ok(await Service<WorkingService>(context).ListAsync(Owner(context), library, Client(context), branch, cancellationToken)));
        group.MapPut("/working", async (HttpContext context, string library, WorkingChangeRequest request, CancellationToken cancellationToken) =>
            Results.Ok(await Service<WorkingService>(context).PutAsync(Owner(context), library, Client(context), request, OperationId(context), cancellationToken)));
        group.MapPost("/staging/{branch}", async (HttpContext context, string library, string branch, StageRequest request, CancellationToken cancellationToken) =>
            Results.Created($"/v1/libraries/{library}/version-control/staging", new { id = await Service<WorkingService>(context).StageAsync(Owner(context), library, Client(context), branch, request, OperationId(context), cancellationToken), version = 1, operationId = OperationId(context) }));
        group.MapDelete("/staging/{setId:guid}/entries/{workingId:guid}", async (HttpContext context, string library, Guid setId, Guid workingId, CancellationToken cancellationToken) =>
        {
            await Service<WorkingService>(context).UnstageAsync(Owner(context), library, setId, workingId, Version(context), OperationId(context), cancellationToken);
            return Results.Ok(new { id = setId, version = Version(context) + 1, operationId = OperationId(context) });
        });
        group.MapPost("/commits", async (HttpContext context, string library, CommitRequest request, CancellationToken cancellationToken) =>
            Results.Created($"/v1/libraries/{library}/version-control/commits", await Service<CommitService>(context).PublishAsync(Owner(context), library, Client(context), request, IdempotencyKey(context), OperationId(context), cancellationToken)));
        group.MapGet("/refs", async (HttpContext context, string library, CancellationToken cancellationToken) => Results.Ok(await Service<RefService>(context).ListAsync(Owner(context), library, cancellationToken)));
        group.MapPost("/refs/{kind}", async (HttpContext context, string library, string kind, RefRequest request, CancellationToken cancellationToken) => Results.Created($"/v1/libraries/{library}/version-control/refs/{request.Name}", await Service<RefService>(context).CreateAsync(Owner(context), library, kind, request, OperationId(context), cancellationToken)));
        group.MapPatch("/refs/{name}", async (HttpContext context, string library, string name, RefRequest request, CancellationToken cancellationToken) => Results.Ok(await Service<RefService>(context).RenameAsync(Owner(context), library, name, request with { ExpectedVersion = Version(context) }, OperationId(context), cancellationToken)));
        group.MapDelete("/refs/{name}", async (HttpContext context, string library, string name, [FromBody] RefRequest request, CancellationToken cancellationToken) => { await Service<RefService>(context).DeleteAsync(Owner(context), library, name, request with { ExpectedVersion = Version(context) }, OperationId(context), cancellationToken); return Results.Ok(new { name, operationId = OperationId(context) }); });
        group.MapGet("/graph/{reference}", async (HttpContext context, string library, string reference, Guid? asset, bool showHidden, int offset, int limit, CancellationToken cancellationToken) => Results.Ok(await Service<QueryService>(context).ProjectAsync(Owner(context), library, reference, asset, showHidden, offset, limit == 0 ? 100 : limit, cancellationToken)));
        group.MapGet("/compare", async (HttpContext context, string library, string ours, string theirs, CancellationToken cancellationToken) => Results.Ok(await Service<QueryService>(context).CompareAsync(Owner(context), library, ours, theirs, cancellationToken)));
        group.MapPost("/checkout", async (HttpContext context, string library, CheckoutRequest request, CancellationToken cancellationToken) => Results.Ok(await Service<OperationService>(context).CheckoutAsync(Owner(context), library, Client(context), request with { ExpectedVersion = Version(context) }, OperationId(context), cancellationToken)));
        group.MapPost("/restore", async (HttpContext context, string library, RestoreRequest request, CancellationToken cancellationToken) => Results.Created($"/v1/libraries/{library}/version-control/commits", await Service<OperationService>(context).RestoreAsync(Owner(context), library, Client(context), request, IdempotencyKey(context), OperationId(context), cancellationToken)));
        group.MapGet("/rewrite/preview", async (HttpContext context, string library, string branch, string newHead, CancellationToken cancellationToken) => Results.Ok(await Service<RewriteService>(context).PreviewAsync(Owner(context), library, branch, newHead, cancellationToken)));
        group.MapPost("/rewrite", async (HttpContext context, string library, RewriteRequest request, CancellationToken cancellationToken) => Results.Ok(await Service<RewriteService>(context).ApplyAsync(Owner(context), library, request, OperationId(context), cancellationToken)));
        group.MapPost("/recovery/{reflogId:guid}", async (HttpContext context, string library, Guid reflogId, RefRequest request, CancellationToken cancellationToken) => Results.Created($"/v1/libraries/{library}/version-control/refs/{request.Name}", await Service<RewriteService>(context).RecoverAsync(Owner(context), library, reflogId, request.Name, OperationId(context), cancellationToken)));
        group.MapPost("/recovery/expire", async (HttpContext context, string library, CancellationToken cancellationToken) => Results.Ok(new { expired = await Service<RewriteService>(context).ExpireAsync(Owner(context), library, cancellationToken), operationId = OperationId(context) }));
        group.MapPut("/visibility", async (HttpContext context, string library, VisibilityRequest request, CancellationToken cancellationToken) => Results.Ok(await Service<VisibilityService>(context).SetAsync(Owner(context), library, request, OperationId(context), cancellationToken)));
        group.MapPost("/merges", async (HttpContext context, string library, MergeRequest request, CancellationToken cancellationToken) => Results.Created($"/v1/libraries/{library}/version-control/commits", await Service<MergeService>(context).PublishAsync(Owner(context), library, Client(context), request, IdempotencyKey(context), OperationId(context), cancellationToken)));
        group.MapPost("/projections/rebuild", async (HttpContext context, string library, CancellationToken cancellationToken) => Results.Ok(new { rows = await Service<ProjectionService>(context).RebuildAsync(Owner(context), library, cancellationToken), operationId = OperationId(context) }));
        group.MapGet("/inventory", async (HttpContext context, string library, CancellationToken cancellationToken) => Results.Ok(await Service<ProjectionService>(context).InventoryAsync(Owner(context), library, cancellationToken)));
        group.MapGet("/state/{reference}/{asset:guid}", async (HttpContext context, string library, string reference, Guid asset, CancellationToken cancellationToken) => Results.Ok(await Service<ProjectionService>(context).AssetAsync(Owner(context), library, reference, asset, cancellationToken)));
        group.MapPost("/integrity/verify", VerifyIntegrityAsync);
    }

    private static async Task<IResult> VerifyIntegrityAsync(HttpContext context, string library, CancellationToken cancellationToken)
    {
        if (!OwnerAuthorization.TryGetOwner(context.User, out var owner)) return Problem(context, "unauthenticated", 401, "Authentication required");
        var result = await Service<IntegrityService>(context).VerifyAsync(owner, library, cancellationToken);
        return result.Healthy
            ? Results.Ok(new { healthy = true, result.CommitsChecked, operationId = OperationId(context) })
            : Problem(context, "graph_corrupt", 409, "Version history integrity verification failed", new() { ["integrityCodes"] = result.Codes });
    }

    internal static IResult Problem(HttpContext context, string code, int status, string title, Dictionary<string, object?>? extra = null)
    {
        var extensions = extra ?? [];
        extensions["code"] = code;
        extensions["operationId"] = OperationId(context);
        return Results.Problem(type: $"https://example.invalid/problems/{code}", title: title, statusCode: status, extensions: extensions);
    }

    internal static string OperationId(HttpContext context) => context.Items[PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable";
    private static T Service<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>() ?? throw new VersionControlProblem("version_control_unavailable", 503);
    internal static string Owner(HttpContext context) => OwnerAuthorization.TryGetOwner(context.User, out var owner) ? owner : throw new VersionControlProblem("unauthenticated", 401);
    internal static string Client(HttpContext context) => !string.IsNullOrWhiteSpace(context.Request.Headers["X-Client-Id"]) ? context.Request.Headers["X-Client-Id"].ToString() : throw new VersionControlProblem("client_id_required", 400);
    internal static string IdempotencyKey(HttpContext context) => context.Request.Headers[RequestContracts.IdempotencyHeader].ToString();
    internal static long Version(HttpContext context) => RequestContracts.TryGetLifecycleVersion(context, out var version) ? version : throw new VersionControlProblem("lifecycle_version_required", 400);
}

public sealed class VersionControlGuard(ILogger<VersionControlGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        try { return await next(invocation); }
        catch (VersionControlProblem error)
        {
            logger.LogWarning("Version-control request rejected {AuditEventType} {OperationId} {ReasonCode}", "version-control.rejected", VersionControlEndpoints.OperationId(context), error.Code);
            return VersionControlEndpoints.Problem(context, error.Code, error.Status, error.Code.Replace('_', ' '), new(error.Extensions));
        }
        catch (Npgsql.NpgsqlException)
        {
            return VersionControlEndpoints.Problem(context, "version_control_unavailable", 503, "Version control is unavailable; retry with the same request");
        }
    }
}
