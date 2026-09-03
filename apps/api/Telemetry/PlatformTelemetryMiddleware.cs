using System.Diagnostics;

namespace Platform.Api.Telemetry;

public sealed class PlatformTelemetryMiddleware(
    RequestDelegate next,
    ILogger<PlatformTelemetryMiddleware> logger,
    OperationIdFactory operationIds)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var operationId = operationIds.Resolve(context.Request.Headers[PlatformTelemetry.OperationIdHeader].FirstOrDefault());
        context.Response.Headers[PlatformTelemetry.OperationIdHeader] = operationId;

        using var activity = PlatformTelemetry.ActivitySource.StartActivity("http.request", ActivityKind.Server);
        activity?.SetTag("platform.operation_id", operationId);
        activity?.SetTag("http.request.method", context.Request.Method);
        activity?.SetTag("url.path", context.Request.Path.Value);

        var started = Stopwatch.GetTimestamp();
        using (logger.BeginScope(new Dictionary<string, object?> { ["operationId"] = operationId }))
        {
            await next(context);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PlatformTelemetry.Requests.Add(1, new KeyValuePair<string, object?>("status", context.Response.StatusCode));
            PlatformTelemetry.RequestDuration.Record(duration, new KeyValuePair<string, object?>("status", context.Response.StatusCode));
            activity?.SetTag("http.response.status_code", context.Response.StatusCode);
            logger.LogInformation("Request completed with status {StatusCode} in {DurationMs} ms", context.Response.StatusCode, duration);
        }
    }
}
