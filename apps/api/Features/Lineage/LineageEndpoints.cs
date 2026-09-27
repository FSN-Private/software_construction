using Platform.Api.Features.VersionControl;
using Platform.Contracts.Lineage;

namespace Platform.Api.Features.Lineage;

public static class LineageEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/lineage").RequireAuthorization().AddEndpointFilter<VersionControlGuard>();
        group.MapGet("/capabilities", (HttpContext context, IConfiguration configuration) =>
        {
            _ = VersionControlEndpoints.Owner(context);
            return Results.Ok(new { mutationsEnabled = configuration.GetValue<bool?>("Lineage:MutationsEnabled") != false });
        });
        group.MapGet("/images/{asset:guid}/history", async (HttpContext context, string library, Guid asset, string? branch,
            bool showHidden, int offset, int limit, CancellationToken cancellationToken) =>
        {
            var result = await context.RequestServices.GetRequiredService<LineageService>().HistoryAsync(
                VersionControlEndpoints.Owner(context), library, asset, branch ?? "main", showHidden, offset, limit == 0 ? 100 : limit, cancellationToken);
            Log(context, "lineage.history.loaded", result.Nodes.Count, 0);
            return Results.Ok(result);
        });
        group.MapGet("/images/{asset:guid}/compare", async (HttpContext context, string library, Guid asset, string target,
            string source, CancellationToken cancellationToken) =>
        {
            var result = await context.RequestServices.GetRequiredService<LineageService>().CompareAsync(
                VersionControlEndpoints.Owner(context), library, asset, target, source, cancellationToken);
            Log(context, "lineage.comparison.completed", result.Differences.Count, 0);
            return Results.Ok(result);
        });
        group.MapPost("/merges/prepare", async (HttpContext context, string library, MergePrepareRequest request,
            CancellationToken cancellationToken) =>
        {
            var result = await context.RequestServices.GetRequiredService<LineageMergeService>().PrepareAsync(
                VersionControlEndpoints.Owner(context), library, request, cancellationToken);
            Log(context, "lineage.merge.prepared", result.Affected.Count, result.Affected.Sum(item => item.Conflicts.Count));
            return Results.Ok(result);
        });
        group.MapPost("/merges/review", async (HttpContext context, string library, MergeReviewRequest request,
            CancellationToken cancellationToken) =>
            Results.Ok(await context.RequestServices.GetRequiredService<LineageMergeService>().ReviewAsync(
                VersionControlEndpoints.Owner(context), library, request, cancellationToken)));
        group.MapPost("/merges/confirm", async (HttpContext context, string library, MergeConfirmRequest request,
            CancellationToken cancellationToken) =>
        {
            var result = await context.RequestServices.GetRequiredService<LineageMergeService>().ConfirmAsync(
                    VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), request,
                    VersionControlEndpoints.IdempotencyKey(context), VersionControlEndpoints.OperationId(context), cancellationToken);
            Log(context, "lineage.merge.confirmed", 1, 0);
            return Results.Created($"/v1/libraries/{library}/version-control/commits", result);
        });
    }

    private static void Log(HttpContext context, string eventType, int count, int conflicts) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Lineage")
            .LogInformation("Lineage event {AuditEventType} {OperationId} {Count} {ConflictCount}",
                eventType, VersionControlEndpoints.OperationId(context), count, conflicts);
}
