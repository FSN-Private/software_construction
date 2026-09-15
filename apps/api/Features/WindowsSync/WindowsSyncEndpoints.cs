using Platform.Api.Features.Ingest;
using Platform.Api.Features.VersionControl;
using Platform.Contracts;
using Platform.Contracts.WindowsSync;

namespace Platform.Api.Features.WindowsSync;

public static class WindowsSyncEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/windows-sync").RequireAuthorization().AddEndpointFilter<VersionControlGuard>();
        group.MapPost("/devices", async (string library, WindowsSyncDeviceRequest request, HttpContext context, WindowsSyncService service, CancellationToken cancellationToken) => Results.Ok(await service.RegisterAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), request.Branch, VersionControlEndpoints.OperationId(context), cancellationToken)));
        group.MapDelete("/devices/current", async (string library, HttpContext context, WindowsSyncService service, CancellationToken cancellationToken) => Results.Ok(await service.RevokeAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), VersionControlEndpoints.OperationId(context), cancellationToken)));
        group.MapGet("/namespace", async (string library, string branch, string? cursor, int limit, HttpContext context, WindowsSyncService service, CancellationToken cancellationToken) => Results.Ok(await service.NamespaceAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), branch, cursor, limit, cancellationToken)));
        group.MapGet("/content/{contentId:guid}", async (string library, Guid contentId, HttpContext context, WindowsSyncService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ContentAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), contentId, VersionControlEndpoints.OperationId(context), cancellationToken);
            context.Response.Headers["X-Content-SHA256"] = result.Address.Sha256;
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(result.Bytes, "application/octet-stream", enableRangeProcessing: true);
        });
        group.MapPut("/working/{asset:guid}/{edit:guid?}", async (string library, Guid asset, Guid? edit, string branch, string path, string? baseSha256, HttpContext context, WindowsSyncService service, CancellationToken cancellationToken) =>
        {
            var digest = context.Request.Headers["X-Content-SHA256"].ToString();
            return Results.Ok(await service.UploadAsync(VersionControlEndpoints.Owner(context), library, VersionControlEndpoints.Client(context), branch, asset, edit, path, baseSha256, digest, context.Request, VersionControlEndpoints.OperationId(context), cancellationToken));
        });
    }
}
