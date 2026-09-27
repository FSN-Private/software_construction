using System.Diagnostics.Metrics;

namespace Platform.Api.Features.Lineage;

public static class LineageTelemetry
{
    private static readonly Meter Meter = new("Platform.Lineage", "1.0.0");
    public static readonly Histogram<int> GraphSize = Meter.CreateHistogram<int>("platform.lineage.graph.size", "commits");
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("platform.lineage.operation.duration", "ms");
    public static readonly Counter<long> Comparisons = Meter.CreateCounter<long>("platform.lineage.comparisons");
    public static readonly Counter<long> MergePrepares = Meter.CreateCounter<long>("platform.lineage.merge_prepares");
    public static readonly Counter<long> MergeConflicts = Meter.CreateCounter<long>("platform.lineage.merge_conflicts");
    public static readonly Counter<long> MergeConfirms = Meter.CreateCounter<long>("platform.lineage.merge_confirms");
    public static readonly Counter<long> StaleRejects = Meter.CreateCounter<long>("platform.lineage.stale_rejects");
    public static readonly Counter<long> RewriteAttempts = Meter.CreateCounter<long>("platform.lineage.rewrite_attempts");
}
