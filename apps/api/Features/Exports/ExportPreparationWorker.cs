using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed class ExportPreparationWorker(NpgsqlDataSource source, ExportPackageService packages, ExportCleanupService cleanup, IConfiguration configuration, ILogger<ExportPreparationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(PreparationLoopAsync(stoppingToken), MaintenanceLoopAsync(stoppingToken));
    private async Task PreparationLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Export worker operation unavailable {EventType} {ReasonCode}", "exports.worker_unavailable", "retryable"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await MaintainAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Export maintenance unavailable {EventType}", "exports.worker_unavailable"); }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
    public async Task MaintainAsync(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await Platform.Storage.IngestDb.ExecuteAsync(db, "WITH changed AS (UPDATE export_requests SET state='retryable',reason='retryable',generation=generation+1,lease_id=NULL,lease_until=NULL WHERE state='preparing' AND lease_until<now() RETURNING id,owner_id) INSERT INTO audit_events(id,owner_id,operation_id,event_type,resource_id,outcome,reason_code) SELECT gen_random_uuid(),owner_id,$1,'exports.lease_recovered',id,'retryable','retryable' FROM changed; WITH changed AS (UPDATE export_requests SET state='expired',reason='expired',generation=generation+1 WHERE state IN ('completed','partial','failed') AND expires_at<=now() RETURNING id,owner_id) INSERT INTO audit_events(id,owner_id,operation_id,event_type,resource_id,outcome,reason_code) SELECT gen_random_uuid(),owner_id,$1,'exports.expired',id,'expired','expired' FROM changed; UPDATE export_download_grants g SET revoked_at=now() FROM export_requests r WHERE g.request_id=r.id AND g.revoked_at IS NULL AND r.state IN ('expired','cancelled','cancelling','unavailable')", ct, ExportDatabase.OperationId);
        await cleanup.TickAsync(ct);
        await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_results o SET outcome=jsonb_build_object('itemId',o.item_id,'status',CASE WHEN r.state='unavailable' THEN 'unavailable' ELSE 'cancelled' END,'reason',CASE WHEN r.state='unavailable' THEN 'permanently_unavailable' ELSE 'cancelled' END,'filename',NULL,'byteLength',NULL,'sha256',NULL) FROM export_requests r WHERE r.id=o.request_id AND r.state IN ('cancelling','cancelled','unavailable') AND o.outcome->>'status'='pending'", ct);
        if (await ScalarAsync(db, "SELECT 1 FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.state='unknown' OR i.state='admitted' AND (r.lease_until IS NULL OR r.lease_until<=now() OR r.generation<>i.generation OR r.state<>'preparing') LIMIT 1", ct) is not null) logger.LogWarning("Export cleanup requires settlement {EventType}", "exports.cleanup_pending");
    }
    public async Task TickAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Exports:Enabled")) return;
        await using var db = await source.OpenConnectionAsync(ct);
        string owner; string library; Guid id; long generation; var lease = Guid.NewGuid();
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            await using var cmd = Command(db, "SELECT id,owner_id,library_id,generation FROM export_requests WHERE state IN ('pending','retryable') AND NOT EXISTS(SELECT 1 FROM export_operations WHERE singleton AND paused) ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED");
            await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return; id = r.GetGuid(0); owner = r.GetString(1); library = r.GetString(2); generation = r.GetInt64(3); }
            await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1", ct, id, lease); await tx.CommitAsync(ct);
        }
        var created = (DateTime)(await ScalarAsync(db, "SELECT created_at FROM export_requests WHERE id=$1", ct, id))!;
        Platform.Api.Telemetry.PlatformTelemetry.ExportQueueAge.Record(Math.Max(0, (DateTime.UtcNow - created).TotalSeconds));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (created.AddSeconds(ExportConfiguration.Limits(configuration).PreparationSeconds) - DateTime.UtcNow).TotalMilliseconds)));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var heartbeat = HeartbeatAsync(id, lease, generation, timeout, heartbeatStop.Token);
        try
        {
            var done = await packages.PrepareAsync(owner, library, id, lease, generation, timeout.Token);
            if (done)
            {
                var state = await ScalarAsync(db, "SELECT state FROM export_requests WHERE id=$1 AND generation=$2", ct, id, generation) as string;
                if (state is "completed" or "partial" or "failed") Platform.Api.Telemetry.PlatformTelemetry.ExportPreparationDuration.Record(Math.Max(0, (DateTime.UtcNow - created).TotalMilliseconds));
                if (state is "completed" or "partial") { Platform.Api.Telemetry.PlatformTelemetry.ExportPrepared.Add(1); logger.LogInformation("Export preparation settled {EventType}", "exports.prepared"); }
                if (state is "partial" or "failed") { Platform.Api.Telemetry.PlatformTelemetry.ExportFailed.Add(1); logger.LogWarning("Export preparation has failed items {EventType}", "exports.failed"); }
            }
            if (!done) await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='pending',lease_id=NULL,lease_until=NULL WHERE id=$1 AND lease_id=$2 AND generation=$3 AND state='preparing'", ct, id, lease, generation);
        }
        catch (Exception error) when (error is ExportProblem or Amazon.S3.AmazonS3Exception or IOException or OperationCanceledException)
        {
            var reason = error is ExportProblem problem && ExportStates.Reasons.Contains(problem.Code) ? problem.Code : error is OperationCanceledException ? "resource_limit" : "storage_unavailable";
            await using var tx = await db.BeginTransactionAsync(CancellationToken.None);
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='failed',reason=$4,lease_id=NULL,lease_until=NULL,completed_at=now(),expires_at=now()+interval '24 hours' WHERE id=$1 AND lease_id=$2 AND generation=$3 AND state='preparing'", CancellationToken.None, id, lease, generation, reason) != 0)
            {
                Platform.Api.Telemetry.PlatformTelemetry.ExportPreparationDuration.Record(Math.Max(0, (DateTime.UtcNow - created).TotalMilliseconds));
                Platform.Api.Telemetry.PlatformTelemetry.ExportFailed.Add(1, new KeyValuePair<string, object?>("reason", reason)); logger.LogWarning("Export preparation failed {EventType} {ReasonCode}", "exports.failed", reason);
                await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_results SET outcome=jsonb_build_object('itemId',item_id,'status','failed','reason',$2::text,'filename',NULL,'byteLength',NULL,'sha256',NULL) WHERE request_id=$1 AND outcome->>'status'='pending'", CancellationToken.None, id, reason);
                await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.preparation_failed", id, "failed", CancellationToken.None, reason);
            }
            await tx.CommitAsync(CancellationToken.None);
        }
        finally { await heartbeatStop.CancelAsync(); await heartbeat; }
    }
    private async Task HeartbeatAsync(Guid id, Guid lease, long generation, CancellationTokenSource preparation, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await using var db = await source.OpenConnectionAsync(ct);
                if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET lease_until=now()+interval '2 minutes' WHERE id=$1 AND lease_id=$2 AND generation=$3 AND state='preparing' AND lease_until>now()", ct, id, lease, generation) == 0) { await preparation.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { await preparation.CancelAsync(); }
    }
}
