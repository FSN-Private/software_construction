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
                await promoter.PromoteAsync(intent, stoppingToken);
                await outbox.MarkPromotedAsync(intent.Id, stoppingToken);
                if (!await outbox.MarkPublishedAsync(intent.Id, stoppingToken)) throw new InvalidOperationException("publication_metadata_not_ready");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
