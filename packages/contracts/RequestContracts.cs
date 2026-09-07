using Microsoft.AspNetCore.Http;

namespace Platform.Contracts;

public static class RequestContracts
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string LifecycleVersionHeader = "If-Match";

    public static bool HasIdempotencyKey(HttpContext context) =>
        !string.IsNullOrWhiteSpace(context.Request.Headers[IdempotencyHeader].FirstOrDefault());

    public static IResult Problem(HttpContext context, int status, string code, string title) => Results.Problem(
        type: $"https://example.invalid/problems/{code}", title: title, statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["operationId"] = context.Items["platform.operation_id"] });
}
