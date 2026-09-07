using Microsoft.Extensions.Hosting;

namespace Platform.Ingest.Worker;

public sealed class PublicationRepairWorker(PublicationOutbox outbox, PublicationPromoter promoter, ILogger<PublicationRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var repaired = await outbox.ReconcileAsync(stoppingToken);
            if (repaired > 0) logger.LogWarning("Requeued {RepairedIntentCount} stale publication intents", repaired);
            while (await outbox.ClaimAsync(stoppingToken) is { } intent)
            {
                try
                {
                    await promoter.PromoteAsync(intent, stoppingToken);
                    await outbox.MarkPromotedAsync(intent.Id, stoppingToken);
                    if (!await outbox.MarkPublishedAsync(intent.Id, stoppingToken)) throw new InvalidOperationException("publication_metadata_not_ready");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    await outbox.MarkFailedAsync(intent.Id, exception is InvalidDataException ? "checksum_mismatch" : "publication_failed", stoppingToken);
                    logger.LogError(exception, "Publication intent {PublicationIntentId} failed and was retained for repair", intent.Id);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
