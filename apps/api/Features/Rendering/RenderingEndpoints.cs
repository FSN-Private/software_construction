using Platform.Api.Features.VersionControl;
using Platform.Contracts;
using Platform.Contracts.Rendering;

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
        group.MapPost("/{requestId:guid}/retry", async (Guid requestId, string library, HttpContext context, CancellationToken cancellationToken) => Results.Accepted($"/v1/renditions/{requestId:D}", await Service(context).RetryAsync(VersionControlEndpoints.Owner(context), library, requestId, cancellationToken)));

        var callbacks = app.MapGroup("/internal/render-callbacks");
        callbacks.MapPost("/claim", async (RenderClaimCallback callback, HttpContext context, CancellationToken cancellationToken) =>
            await Callback(context).ClaimAsync(callback.RequestId, callback.AttemptId, cancellationToken) ? Results.Ok() : Results.Conflict());
        callbacks.MapPost("/complete", async (RenderCompleteCallback callback, HttpContext context, CancellationToken cancellationToken) =>
        {
            await Callback(context).CompleteAsync(callback.RequestId, callback.AttemptId, callback.Publication.ObjectKey, callback.Publication.ObjectVersion, callback.Publication.OutputSha256, callback.Publication.ByteLength, callback.Publication.MediaType, callback.Provenance, cancellationToken);
            return Results.Ok();
        });
        callbacks.MapPost("/fail", async (RenderFailCallback callback, HttpContext context, CancellationToken cancellationToken) =>
        {
            await Callback(context).FailAsync(callback.RequestId, callback.AttemptId, callback.SafeFailureCode, cancellationToken);
            return Results.Ok();
        });
    }

    private static RenderingService Service(HttpContext context) => context.RequestServices.GetService<RenderingService>() ?? throw new RenderingProblem("rendering_unavailable", 503);
    private static IRenderWorkerCallback Callback(HttpContext context) => context.RequestServices.GetRequiredService<IRenderWorkerCallback>();
}

public sealed record RenderClaimCallback(Guid RequestId, Guid AttemptId);
public sealed record RenderCompleteCallback(Guid RequestId, Guid AttemptId, RenderPublication Publication, RenderProvenance Provenance);
public sealed record RenderFailCallback(Guid RequestId, Guid AttemptId, string SafeFailureCode);

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
