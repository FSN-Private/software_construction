using Microsoft.Extensions.Hosting;

namespace Platform.Ingest.Worker;

public sealed class PublicationRepairWorker(PublicationOutbox outbox, ILogger<PublicationRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var repaired = await outbox.ReconcileAsync(stoppingToken);
            if (repaired > 0) logger.LogWarning("Requeued {RepairedIntentCount} stale publication intents", repaired);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
