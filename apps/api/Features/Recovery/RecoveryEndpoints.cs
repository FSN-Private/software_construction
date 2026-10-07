using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Platform.Contracts;
using Platform.Api.Features.VersionControl;

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

        app.MapPost("/v1/recovery/purge/proposals", async (HttpContext context,
            RecoveryPurgeProposalRequest? request, IConfiguration configuration, IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!TryGetOwner(context, out var owner))
                return Problem(context, context.User.Identity?.IsAuthenticated is true
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                    context.User.Identity?.IsAuthenticated is true ? "recovery_identity_unavailable" : "unauthenticated");
            if (!HasRecentAuthentication(context.User, DateTimeOffset.UtcNow))
                return Problem(context, StatusCodes.Status403Forbidden, "recent_authentication_required");
            if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly"))
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            var service = services.GetService<RecoveryPurgeService>();
            if (service is null) return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            try
            {
                var result = await service.ProposeAsync(owner, request,
                    context.Items[Platform.Api.Telemetry.PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable",
                    cancellationToken);
                return Results.Created($"/v1/recovery/purge/proposals/{result.Proposal.OperationId:D}", result);
            }
            catch (RecoveryPurgeException error)
            {
                return Problem(context, error.StatusCode, error.Code);
            }
            catch (RecoveryUnavailableException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
        }).RequireAuthorization();

        app.MapGet("/v1/recovery/purge/proposals/{operationId:guid}", async (HttpContext context, Guid operationId,
            IConfiguration configuration, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!TryGetOwner(context, out var owner))
                return Problem(context, context.User.Identity?.IsAuthenticated is true
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                    context.User.Identity?.IsAuthenticated is true ? "recovery_identity_unavailable" : "unauthenticated");
            if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly"))
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            var service = services.GetService<RecoveryPurgeService>();
            if (service is null) return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            try
            {
                var result = await service.GetProposalAsync(owner, operationId, cancellationToken);
                return result is null ? Problem(context, StatusCodes.Status404NotFound, "purge_proposal_not_found") : Results.Ok(result);
            }
            catch (RecoveryUnavailableException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
        }).RequireAuthorization();

        app.MapPost("/v1/recovery/purge/proposals/{operationId:guid}/freeze", async (HttpContext context,
            Guid operationId, IConfiguration configuration, IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!TryGetOwner(context, out var owner))
                return Problem(context, context.User.Identity?.IsAuthenticated is true
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                    context.User.Identity?.IsAuthenticated is true ? "recovery_identity_unavailable" : "unauthenticated");
            var now = DateTimeOffset.UtcNow;
            if (!TryGetRecentAuthentication(context.User, now, out var authenticatedAt))
                return Problem(context, StatusCodes.Status403Forbidden, "recent_authentication_required");
            if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly"))
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            var service = services.GetService<RecoveryPurgeService>();
            if (service is null) return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            try
            {
                var result = await service.FreezeAsync(owner, operationId, authenticatedAt,
                    context.Items[Platform.Api.Telemetry.PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable",
                    cancellationToken);
                return Results.Ok(result);
            }
            catch (RecoveryPurgeException error)
            {
                return Problem(context, error.StatusCode, error.Code);
            }
            catch (RecoveryUnavailableException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException)
            {
                return Problem(context, StatusCodes.Status503ServiceUnavailable, "recovery_unavailable");
            }
        }).RequireAuthorization();
    }

    private static bool TryGetOwner(HttpContext context, out string owner) =>
        OwnerAuthorization.TryGetOwner(context.User, out owner);

    private static bool HasRecentAuthentication(System.Security.Claims.ClaimsPrincipal principal, DateTimeOffset nowUtc)
    {
        if (!TryGetRecentAuthentication(principal, nowUtc, out _)) return false;
        return true;
    }

    private static bool TryGetRecentAuthentication(System.Security.Claims.ClaimsPrincipal principal,
        DateTimeOffset nowUtc, out DateTimeOffset authenticatedAt)
    {
        authenticatedAt = default;
        var value = principal.FindFirst("auth_time")?.Value;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        try { authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        var age = nowUtc - authenticatedAt;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5);
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
