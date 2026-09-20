using Platform.Api.Features.Ingest;
using Platform.Api.Features.VersionControl;
using Platform.Contracts;
using Platform.Contracts.VersionControl;
using Platform.Contracts.WindowsSync;

namespace Platform.Api.Features.WindowsSync;

public static class WindowsSyncEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/windows-sync").RequireAuthorization().AddEndpointFilter<VersionControlGuard>();
        group.MapPost("/devices", async (string library, WindowsSyncDeviceRequest request, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) => Results.Ok(await Service(services).RegisterAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), request.Branch, VersionControlEndpoints.OperationId(context), cancellationToken)));
        group.MapDelete("/devices/current", async (string library, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) => Results.Ok(await Service(services).RevokeAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), VersionControlEndpoints.OperationId(context), cancellationToken)));
        group.MapGet("/namespace", async (string library, string branch, string? cursor, int limit, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) => Results.Ok(await Service(services).NamespaceAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), branch, cursor, limit, cancellationToken)));
        group.MapGet("/content/{contentId:guid}", async (string library, Guid contentId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var result = await Service(services).ContentAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), contentId, VersionControlEndpoints.OperationId(context), cancellationToken);
            context.Response.Headers["X-Content-SHA256"] = result.Address.Sha256;
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(result.Bytes, "application/octet-stream", enableRangeProcessing: true);
        });
        group.MapPut("/working/{asset:guid}/{edit:guid?}", async (string library, Guid asset, Guid? edit, string branch, string path, string? baseSha256, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var digest = context.Request.Headers["X-Content-SHA256"].ToString();
            return Results.Ok(await Service(services).UploadAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), branch, asset, edit, path, baseSha256, digest, context.Request, VersionControlEndpoints.OperationId(context), cancellationToken));
        });
    }

    private static WindowsSyncService Service(IServiceProvider services) => services.GetService<WindowsSyncService>() ?? throw new VersionControlProblem("windows_sync_unavailable", 503);
}
