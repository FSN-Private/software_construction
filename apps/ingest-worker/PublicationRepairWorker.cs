using Platform.Ingest.Worker.Observability;

namespace Platform.Ingest.Worker;

public sealed class PublicationRepairWorker(BatchProcessor batches, ILogger<PublicationRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                var expired = await batches.ExpireAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Expired {ExpiredSessions} unfinished upload sessions", expired);
                while (await batches.ProcessNextAsync(stoppingToken))
                {
                    IngestTelemetry.Batches.Add(1);
                    IngestTelemetry.Repairs.Add(1);
                    logger.LogInformation("Ingest publication pass completed {AuditEventType}", "ingest.batch.processed");
                    logger.LogInformation("Ingest publication state reconciled {AuditEventType}", "ingest.repaired");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch
            {
                IngestTelemetry.StorageFailures.Add(1);
                logger.LogError("Publication polling failed {AuditEventType}", "ingest.storage.failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
