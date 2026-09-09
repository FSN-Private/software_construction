using Platform.Api.Features.Ingest;
using Platform.Contracts;
using Platform.Storage;

namespace Platform.Api.Features.Lifecycle;

public static class AssetEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1").AddEndpointFilter<IngestGuard>();
        group.MapGet("/assets", async (bool? deleted, Guid? after, HttpContext c, IServiceProvider services, CancellationToken ct) => Results.Ok(await Service(services).ListAsync(IngestEndpoints.Owner(c), deleted == true, after, ct)));
        group.MapGet("/assets/{id:guid}/edits", async (Guid id, HttpContext c, IServiceProvider services, CancellationToken ct) => Results.Ok(await Service(services).EditsAsync(IngestEndpoints.Owner(c), id, ct)));
        group.MapGet("/assets/{id:guid}/content", (Guid id, HttpContext c, IServiceProvider s, CancellationToken ct) => Download(id, false, c, s, ct));
        group.MapGet("/edits/{id:guid}/content", (Guid id, HttpContext c, IServiceProvider s, CancellationToken ct) => Download(id, true, c, s, ct));
        group.MapPatch("/assets/{id:guid}/path", (Guid id, PathRequest request, HttpContext c, IServiceProvider s, CancellationToken ct) => Change(id, false, "move", request.Path, c, s, ct));
    }

    private static AssetService Service(IServiceProvider services) => services.GetService<AssetService>() ?? throw new IngestException("ingest_unavailable", 503);
    private static async Task<IResult> Change(Guid id, bool edit, string action, string? path, HttpContext context, IServiceProvider services, CancellationToken ct) =>
        Results.Ok(await Service(services).ChangeAsync(IngestEndpoints.Owner(context), id, edit, action, IngestEndpoints.Version(context), path, context.Request.Headers[RequestContracts.IdempotencyHeader].ToString(), IngestEndpoints.Operation(context), ct));

    private static async Task<IResult> Download(Guid id, bool edit, HttpContext context, IServiceProvider services, CancellationToken ct)
    {
        var result = await Service(services).DownloadAsync(IngestEndpoints.Owner(context), id, edit, IngestEndpoints.Operation(context), ct);
        context.Response.Headers["Content-Digest"] = $"sha-256=:{Convert.ToBase64String(Convert.FromHexString(result.Address.Sha256))}:";
        context.Response.Headers["X-Content-SHA256"] = result.Address.Sha256;
        context.Response.Headers["X-Provenance-Id"] = result.HandoffId.ToString();
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(result.Bytes, "application/octet-stream", id + (edit ? ".xmp" : ".original"));
    }
}

public sealed record PathRequest(string Path);
public sealed record RestoreRequest(string? Path);
