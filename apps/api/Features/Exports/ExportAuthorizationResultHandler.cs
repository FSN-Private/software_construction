using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Platform.Contracts;
using System.Security.Claims;
namespace Platform.Api.Features.Exports;

public sealed record ExportProtected;
public sealed class ExportAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Succeeded && context.GetEndpoint()?.Metadata.GetMetadata<ExportProtected>() is not null)
        {
            var actor = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous" : "anonymous";
            if (context.RequestServices.GetService<IAuditWriter>() is { } audit)
                await audit.WriteAsync(AuditEvent.Create(actor, ExportDatabase.OperationId, "exports.access_denied", "denied", result.Challenged ? "unauthenticated" : "forbidden"), context.RequestAborted);
            context.Response.Headers.CacheControl = "private, no-store";
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
