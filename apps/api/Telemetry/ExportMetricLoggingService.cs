using System.Diagnostics.Metrics;

namespace Platform.Api.Telemetry;

// The task's existing JSON log pipeline is the collector; never forward metric tags.
public sealed class ExportMetricLoggingService(ILogger<ExportMetricLoggingService> logger) : IHostedService, IDisposable
{
    private readonly MeterListener listener = new();
    private static readonly string[] InstrumentNames =
    [
        "platform.exports.prepared", "platform.exports.failed", "platform.exports.render.failures", "platform.exports.preparation.duration",
        "platform.exports.basket.items", "platform.exports.queue.age", "platform.exports.item.outcomes",
        "platform.exports.package.bytes", "platform.exports.download.attempts", "platform.exports.policy.denials"
    ];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        listener.InstrumentPublished = (instrument, collector) =>
        {
            if (instrument.Meter == PlatformTelemetry.Meter && InstrumentNames.Contains(instrument.Name))
                collector.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();
        return Task.CompletedTask;
    }

    private void Record(Instrument instrument, double value)
    {
        if (double.IsFinite(value) && value >= 0)
            logger.LogInformation("{EventType} {MetricName} {MetricValue}", "exports.metric", instrument.Name, value);
    }

    public Task StopAsync(CancellationToken cancellationToken) { listener.Dispose(); return Task.CompletedTask; }
    public void Dispose() => listener.Dispose();
}
