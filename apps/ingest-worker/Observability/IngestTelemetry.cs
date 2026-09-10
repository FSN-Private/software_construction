using System.Diagnostics.Metrics;
namespace Platform.Ingest.Worker.Observability;
public static class IngestTelemetry
{
    public static readonly Meter Meter = new("Platform.Ingest", "1.0.0");
    public static readonly Counter<long> Batches = Meter.CreateCounter<long>("ingest.batches");
    public static readonly Counter<long> Quarantined = Meter.CreateCounter<long>("ingest.quarantined");
    public static readonly Counter<long> Repairs = Meter.CreateCounter<long>("ingest.repaired");
    public static readonly Counter<long> StorageFailures = Meter.CreateCounter<long>("ingest.storage.failures");
    public static readonly Counter<long> VerifiedBytes = Meter.CreateCounter<long>("ingest.verified.bytes", unit: "By");
    public static readonly Counter<long> IntegrityDrift = Meter.CreateCounter<long>("ingest.integrity.drift");
    public static readonly Histogram<double> BatchAge = Meter.CreateHistogram<double>("ingest.batch.age", unit: "s");
    public static readonly Histogram<double> VerificationDuration = Meter.CreateHistogram<double>("ingest.verification.duration", unit: "s");
}
