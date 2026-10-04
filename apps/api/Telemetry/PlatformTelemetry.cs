using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Platform.Api.Telemetry;

public static class PlatformTelemetry
{
    public const string OperationIdHeader = "X-Operation-Id";
    public const string OperationIdItemKey = "platform.operation_id";
    public static readonly ActivitySource ActivitySource = new("Platform.Api", "1.0.0");
    public static readonly Meter Meter = new("Platform.Api", "1.0.0");
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("platform.api.requests");
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("platform.api.request.duration", unit: "ms");
    public static readonly Counter<long> ExportPrepared = Meter.CreateCounter<long>("platform.exports.prepared");
    public static readonly Counter<long> ExportFailed = Meter.CreateCounter<long>("platform.exports.failed");
    public static readonly Counter<long> ExportRenderFailures = Meter.CreateCounter<long>("platform.exports.render.failures");
    public static readonly Histogram<double> ExportPreparationDuration = Meter.CreateHistogram<double>("platform.exports.preparation.duration", unit: "ms");
    public static readonly Histogram<long> ExportBasketSize = Meter.CreateHistogram<long>("platform.exports.basket.items");
    public static readonly Histogram<double> ExportQueueAge = Meter.CreateHistogram<double>("platform.exports.queue.age", unit: "s");
    public static readonly Counter<long> ExportItemOutcomes = Meter.CreateCounter<long>("platform.exports.item.outcomes");
    public static readonly Histogram<long> ExportPackageSize = Meter.CreateHistogram<long>("platform.exports.package.bytes", unit: "By");
    public static readonly Counter<long> ExportDownloadAttempts = Meter.CreateCounter<long>("platform.exports.download.attempts");
    public static readonly Counter<long> ExportPolicyDenials = Meter.CreateCounter<long>("platform.exports.policy.denials");
}
