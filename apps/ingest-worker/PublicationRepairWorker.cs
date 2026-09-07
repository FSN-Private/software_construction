using Microsoft.Extensions.Hosting;
using Platform.Ingest.Worker.Observability;

namespace Platform.Ingest.Worker;

public sealed class PublicationRepairWorker(PublicationOutbox outbox, PublicationPromoter promoter, ILogger<PublicationRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await ProcessAvailableAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch
            {
                IngestTelemetry.StorageFailures.Add(1);
                logger.LogError("Publication polling failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessAvailableAsync(CancellationToken cancellationToken)
    {
        var repaired = await outbox.ReconcileAsync(cancellationToken);
        if (repaired > 0) { IngestTelemetry.Repairs.Add(repaired); logger.LogWarning("Requeued {RepairedIntentCount} stale publication intents", repaired); }
        while (await outbox.ClaimAsync(cancellationToken) is { } intent)
        {
            try
            {
                var promoted = await promoter.PromoteAsync(intent, cancellationToken);
                await outbox.MarkPromotedAsync(intent.Id, promoted.StorageVersion, cancellationToken);
                if (!await outbox.MarkPublishedAsync(intent.Id, cancellationToken)) throw new InvalidOperationException("publication_metadata_not_ready");
                IngestTelemetry.VerifiedBytes.Add(promoted.Address.Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                IngestTelemetry.StorageFailures.Add(1);
                try { await outbox.MarkFailedAsync(intent.Id, exception is InvalidDataException ? "checksum_mismatch" : "publication_failed", cancellationToken); }
                catch { logger.LogError("Publication failure could not be persisted; retrying polling"); return; }
                logger.LogWarning("Publication intent {PublicationIntentId} failed and was retained for repair", intent.Id);
            }
        }
    }
}
