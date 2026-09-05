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
            Exception? exception = null;
            try
            {
                await next(context);
            }
            catch (Exception caught)
            {
                exception = caught;
                activity?.SetStatus(ActivityStatusCode.Error, caught.GetType().Name);
                throw;
            }
            finally
            {
                var statusCode = exception is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
                var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                PlatformTelemetry.Requests.Add(1, new KeyValuePair<string, object?>("status", statusCode));
                PlatformTelemetry.RequestDuration.Record(duration, new KeyValuePair<string, object?>("status", statusCode));
                activity?.SetTag("http.response.status_code", statusCode);
                if (exception is null)
                {
                    logger.LogInformation("Request completed with status {StatusCode} in {DurationMs} ms", statusCode, duration);
                }
                else
                {
                    logger.LogError(exception, "Request failed with status {StatusCode} in {DurationMs} ms", statusCode, duration);
                }
            }
        }
    }
}
