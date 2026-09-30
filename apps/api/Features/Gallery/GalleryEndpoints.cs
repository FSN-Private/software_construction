using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.Gallery.Metadata;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Platform.Contracts;
using Platform.Storage;

namespace Platform.Api.Features.Gallery;

public static class GalleryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/gallery").RequireAuthorization().AddEndpointFilter<GalleryGuard>();
        group.MapGet("", async (string library, HttpContext context, [FromServices] GalleryBrowseService browse, CancellationToken ct) =>
            Results.Ok(await browse.BrowseAsync(Owner(context), library, context.Request.Query, ct)));
        group.MapGet("/filter-values", async (string library, HttpContext context, [FromServices] GalleryBrowseService browse, CancellationToken ct) =>
            Results.Ok(await browse.FilterValuesAsync(Owner(context), library,
                context.Request.Query["branch"].ToString() is { Length: > 0 } branch ? branch : "main",
                context.Request.Query["facet"].ToString(), context.Request.Query["q"].ToString(), ct)));
        group.MapGet("/{assetId:guid}", async (string library, Guid assetId, HttpContext context, [FromServices] GalleryDetailService details, CancellationToken ct) =>
            Results.Ok(await details.GetAsync(Owner(context), library, context.Request.Query["branch"].ToString() is { Length: > 0 } branch ? branch : "main", assetId, ct)));
        group.MapGet("/{assetId:guid}/location", async (string library, Guid assetId, HttpContext context, [FromServices] GalleryPreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.RevealAsync(Owner(context), library, context.Request.Query["branch"].ToString() is { Length: > 0 } branch ? branch : "main", assetId,
                context.Items["platform.operation_id"]?.ToString() ?? "op_unavailable", ct)));
        group.MapPut("/preferences", async (string library, GalleryPreferenceRequest request, HttpContext context, [FromServices] GalleryPreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.SetAsync(Owner(context), library, request, ct, context.Request.Headers["Idempotency-Key"].ToString())));
        group.MapGet("/preferences", async (string library, HttpContext context, [FromServices] GalleryPreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.GetAsync(Owner(context), library, ct)));
        group.MapPatch("/{assetId:guid}/metadata", async (string library, Guid assetId, GalleryEditRequest request, HttpContext context, [FromServices] GalleryEditService edits, CancellationToken ct) =>
            Results.Ok(await edits.PatchAsync(Owner(context), library, assetId, request,
                context.Items["platform.operation_id"]?.ToString() ?? "op_unavailable", ct, context.Request.Headers["Idempotency-Key"].ToString())));
    }

    public static string Owner(HttpContext context) => OwnerAuthorization.TryGetOwner(context.User, out var owner)
        ? owner : throw new GalleryProblem("unauthenticated", 401);
}

public sealed class GalleryGuard(ILogger<GalleryGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var timer = Stopwatch.StartNew();
        context.Response.Headers.CacheControl = "private, no-store";
        try { return await next(invocation); }
        catch (Exception error) when (error is GalleryProblem or Platform.Contracts.VersionControl.VersionControlProblem)
        {
            var code = error is GalleryProblem gallery ? gallery.Code : ((Platform.Contracts.VersionControl.VersionControlProblem)error).Code;
            var status = error is GalleryProblem problem ? problem.Status : ((Platform.Contracts.VersionControl.VersionControlProblem)error).Status;
            GalleryTelemetry.Errors.Add(1, new KeyValuePair<string, object?>("code", code));
            if (code == "stale_working_state") GalleryTelemetry.EditConflicts.Add(1);
            if (OwnerAuthorization.TryGetOwner(context.User, out var owner) && context.RequestServices.GetService<IAuditWriter>() is { } audit)
                await audit.WriteAsync(AuditEvent.Create(owner, context.Items["platform.operation_id"]?.ToString() ?? "op_unavailable",
                    context.Request.Method == "PATCH" ? "gallery.metadata_edit" : "gallery.access_failed", "denied", code), context.RequestAborted);
            else logger.LogWarning("Gallery access denied {ReasonCode}", code);
            return RequestContracts.Problem(context, status, code, "Gallery request unavailable");
        }
        finally
        {
            GalleryTelemetry.RequestLatency.Record(timer.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("method", context.Request.Method));
        }
    }
}
