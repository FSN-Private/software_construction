using Platform.Api.Features.VersionControl;
using Platform.Contracts;

namespace Platform.Api.Features.Rendering;

public static class RenderingEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/renditions").RequireAuthorization().AddEndpointFilter<RenderingGuard>();
        group.MapPost("", async (RenditionRequestInput request, HttpContext context, CancellationToken cancellationToken) =>
        {
            var result = await Service(context).RequestAsync(VersionControlEndpoints.Owner(context), request, cancellationToken);
            return result.CacheHit ? Results.Ok(result) : Results.Accepted($"/v1/renditions/{result.RequestId:D}", result);
        });
        group.MapGet("/{requestId:guid}", async (Guid requestId, string library, HttpContext context, CancellationToken cancellationToken) => Results.Ok(await Service(context).StatusAsync(VersionControlEndpoints.Owner(context), library, requestId, cancellationToken)));
    }

    private static RenderingService Service(HttpContext context) => context.RequestServices.GetService<RenderingService>() ?? throw new RenderingProblem("rendering_unavailable", 503);
}

public sealed class RenderingGuard(ILogger<RenderingGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        try { return await next(invocation); }
        catch (RenderingProblem error)
        {
            logger.LogWarning("Rendering request rejected {ReasonCode}", error.Code);
            return Results.Problem(type: $"https://example.invalid/problems/{error.Code}", title: error.Code.Replace('_', ' '), statusCode: error.Status, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["operationId"] = VersionControlEndpoints.OperationId(invocation.HttpContext) });
        }
    }
}
