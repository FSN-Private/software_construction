using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Platform.Api.Telemetry;

public static class PlatformTelemetry
{
    public const string OperationIdHeader = "X-Operation-Id";
    public static readonly ActivitySource ActivitySource = new("Platform.Api", "1.0.0");
    public static readonly Meter Meter = new("Platform.Api", "1.0.0");
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("platform.api.requests");
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("platform.api.request.duration", unit: "ms");
}
