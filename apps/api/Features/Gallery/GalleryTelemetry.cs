using System.Diagnostics.Metrics;

namespace Platform.Api.Features.Gallery;

public static class GalleryTelemetry
{
    private static readonly Meter Meter = new("Platform.Gallery");
    public static readonly Histogram<double> RequestLatency = Meter.CreateHistogram<double>("platform.gallery.request.duration", "ms");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("platform.gallery.errors");
    public static readonly Counter<long> PreviewStates = Meter.CreateCounter<long>("platform.gallery.preview_states");
    public static readonly Counter<long> FacetUses = Meter.CreateCounter<long>("platform.gallery.facet_uses");
    public static readonly Counter<long> EditConflicts = Meter.CreateCounter<long>("platform.gallery.edit_conflicts");
    public static readonly Counter<long> RefreshCompleted = Meter.CreateCounter<long>("platform.gallery.refresh_completed");
    public static readonly Counter<long> RefreshFailed = Meter.CreateCounter<long>("platform.gallery.refresh_failed");
}
