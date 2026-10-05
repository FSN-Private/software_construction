using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Platform.Contracts;

namespace Platform.Api.Features.Recovery;

public static class RecoveryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/recovery/health", async (HttpContext context, IConfiguration configuration,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!RecoveryHealthService.IsEnabled(configuration))
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            var service = services.GetService<RecoveryHealthService>();
            var ownerResolver = services.GetService<RecoveryOwnerResolver>();
            if (service is null || ownerResolver is null) return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            try
            {
                var owner = await ownerResolver.ResolveAsync(context.User, cancellationToken);
                if (owner is null)
                    return Problem(context, context.User.Identity?.IsAuthenticated is true
                        ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                        context.User.Identity?.IsAuthenticated is true ? "recovery_identity_unavailable" : "unauthenticated");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                return Results.Ok(await service.GetAsync(owner, timeout.Token));
            }
            catch (RecoveryUnavailableException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
        }).RequireAuthorization();
    }

    private static IResult Problem(HttpContext context, int status, string code) => Results.Problem(
        type: $"https://example.invalid/problems/{code}",
        title: "Recovery status is unavailable",
        statusCode: status,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["operationId"] = context.Items[Platform.Api.Telemetry.PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable"
        });
}
