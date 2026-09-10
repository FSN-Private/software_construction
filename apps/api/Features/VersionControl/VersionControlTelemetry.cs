using System.Diagnostics.Metrics;

namespace Platform.Api.Features.VersionControl;

public static class VersionControlTelemetry
{
    private static readonly Meter Meter = new("Platform.VersionControl");
    public static readonly Histogram<double> CommandLatency = Meter.CreateHistogram<double>("platform.version_control.command.duration", "ms");
    public static readonly Counter<long> Commits = Meter.CreateCounter<long>("platform.version_control.commits");
    public static readonly Counter<long> RefMovements = Meter.CreateCounter<long>("platform.version_control.ref_movements");
    public static readonly Counter<long> StaleHeads = Meter.CreateCounter<long>("platform.version_control.stale_heads");
    public static readonly Counter<long> IntegrityFailures = Meter.CreateCounter<long>("platform.version_control.integrity_failures");
    public static readonly Counter<long> ProjectionRebuilds = Meter.CreateCounter<long>("platform.version_control.projection_rebuilds");
    public static readonly Counter<long> RecoveryExpirations = Meter.CreateCounter<long>("platform.version_control.recovery_expirations");
}
