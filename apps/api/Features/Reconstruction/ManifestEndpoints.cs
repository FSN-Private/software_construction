using Platform.Api.Features.Ingest;
using Platform.Storage;
using Platform.Storage.Reconstruction;

namespace Platform.Api.Features.Reconstruction;

public static class ManifestEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/reconstruction-manifests").AddEndpointFilter<IngestGuard>();
        group.MapPost("", async (HttpContext c, IServiceProvider s, CancellationToken ct) =>
        {
            var manifest = await Service(s).ExportAsync(IngestEndpoints.Owner(c), IngestEndpoints.Operation(c), ct, c.Request.Headers[Platform.Contracts.RequestContracts.IdempotencyHeader].ToString());
            return Results.Created($"/v1/reconstruction-manifests/{manifest.Id}", new { manifest.Id, manifest.Version, manifest.Sha256 });
        });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext c, IServiceProvider s, CancellationToken ct) =>
        {
            c.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(await Service(s).GetAsync(IngestEndpoints.Owner(c), id, ct));
        });
    }
    private static ManifestService Service(IServiceProvider s) => s.GetService<ManifestService>() ?? throw new IngestException("ingest_unavailable", 503);
}
