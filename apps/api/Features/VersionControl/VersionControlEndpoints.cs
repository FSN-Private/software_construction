using System.Security.Claims;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Telemetry;
using Platform.Contracts;

namespace Platform.Api.Features.VersionControl;

public static class VersionControlEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/libraries/{library}/version-control").RequireAuthorization();
        group.MapPost("/integrity/verify", VerifyIntegrityAsync);
    }

    private static async Task<IResult> VerifyIntegrityAsync(HttpContext context, string library, IntegrityService integrity, CancellationToken cancellationToken)
    {
        if (!OwnerAuthorization.TryGetOwner(context.User, out var owner)) return Problem(context, "unauthenticated", 401, "Authentication required");
        var result = await integrity.VerifyAsync(owner, library, cancellationToken);
        return result.Healthy
            ? Results.Ok(new { healthy = true, result.CommitsChecked, operationId = OperationId(context) })
            : Problem(context, "graph_corrupt", 409, "Version history integrity verification failed", new() { ["integrityCodes"] = result.Codes });
    }

    internal static IResult Problem(HttpContext context, string code, int status, string title, Dictionary<string, object?>? extra = null)
    {
        var extensions = extra ?? [];
        extensions["code"] = code;
        extensions["operationId"] = OperationId(context);
        return Results.Problem(type: $"https://example.invalid/problems/{code}", title: title, statusCode: status, extensions: extensions);
    }

    internal static string OperationId(HttpContext context) => context.Items[PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable";
}
