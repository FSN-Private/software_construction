namespace Platform.Api.Telemetry;

public static class PlatformTelemetryExtensions
{
    public static IApplicationBuilder UsePlatformTelemetry(this IApplicationBuilder app) =>
        app.UseMiddleware<PlatformTelemetryMiddleware>();
}
