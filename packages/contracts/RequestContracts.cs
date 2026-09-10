using Microsoft.AspNetCore.Http;

namespace Platform.Contracts;

public static class RequestContracts
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string LifecycleVersionHeader = "If-Match";

    public static bool HasIdempotencyKey(HttpContext context) =>
        context.Request.Headers[IdempotencyHeader].FirstOrDefault() is { } key && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{16,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool TryGetLifecycleVersion(HttpContext context, out long version) =>
        long.TryParse(context.Request.Headers[LifecycleVersionHeader].FirstOrDefault()?.Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version) && version > 0;

    public static IResult Problem(HttpContext context, int status, string code, string title) => Results.Problem(
        type: $"https://example.invalid/problems/{code}", title: title, statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["operationId"] = context.Items["platform.operation_id"] });
}
