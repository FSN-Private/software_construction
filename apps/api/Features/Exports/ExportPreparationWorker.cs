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
        var candidates = new List<(Guid Id, string Owner, string Library)>();
        await using (var cmd = Command(db, "SELECT id,owner_id,library_id FROM export_requests WHERE state='preparing' AND lease_until<now() OR state IN ('completed','partial','failed') AND expires_at<=now() OR state IN ('cancelling','cancelled','unavailable') AND (EXISTS(SELECT 1 FROM export_results o WHERE o.request_id=export_requests.id AND o.outcome->>'status'='pending') OR EXISTS(SELECT 1 FROM export_download_grants g WHERE g.request_id=export_requests.id AND g.revoked_at IS NULL)) ORDER BY owner_id,library_id,id"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
        foreach (var candidate in candidates) await MaintainRequestAsync(db, candidate.Id, candidate.Owner, candidate.Library, ct);
        await cleanup.TickAsync(ct);
        if (await ScalarAsync(db, "SELECT 1 FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.state='unknown' OR i.state='admitted' AND (r.lease_until IS NULL OR r.lease_until<=now() OR r.generation<>i.generation OR r.state<>'preparing') LIMIT 1", ct) is not null) logger.LogWarning("Export cleanup requires settlement {EventType}", "exports.cleanup_pending");
    }
    public async Task TickAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Exports:Enabled")) return;
        await using var db = await source.OpenConnectionAsync(ct);
        var candidates = new List<(Guid Id, string Owner, string Library)>();
        await using (var cmd = Command(db, "SELECT id,owner_id,library_id FROM export_requests WHERE state IN ('pending','retryable') ORDER BY created_at,id LIMIT 16"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
        string? owner = null; string? library = null; Guid id = default; long generation = 0; var lease = Guid.NewGuid();
        foreach (var candidate in candidates)
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await ExportDatabase.LockAsync(db, candidate.Owner, candidate.Library, ct);
            await using var cmd = Command(db, "SELECT generation FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND state IN ('pending','retryable') AND NOT EXISTS(SELECT 1 FROM export_operations WHERE singleton AND paused) FOR UPDATE SKIP LOCKED", candidate.Id, candidate.Owner, candidate.Library);
            var current = await cmd.ExecuteScalarAsync(ct);
            if (current is null) { await tx.CommitAsync(ct); continue; }
            id = candidate.Id; owner = candidate.Owner; library = candidate.Library; generation = (long)current;
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='preparing',lease_id=$2,lease_until=now()+interval '2 minutes' WHERE id=$1 AND state IN ('pending','retryable')", ct, id, lease) != 1) { await tx.CommitAsync(ct); owner = library = null; continue; }
            await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.preparation_started", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, code: lease.ToString("D"));
            await tx.CommitAsync(ct); break;
        }
        if (owner is null || library is null || id == Guid.Empty) return;
        var created = (DateTime)(await ScalarAsync(db, "SELECT created_at FROM export_requests WHERE id=$1", ct, id))!;
        Platform.Api.Telemetry.PlatformTelemetry.ExportQueueAge.Record(Math.Max(0, (DateTime.UtcNow - created).TotalSeconds));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (created.AddSeconds(ExportConfiguration.Limits(configuration).PreparationSeconds) - DateTime.UtcNow).TotalMilliseconds)));
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var heartbeat = HeartbeatAsync(owner, library, id, lease, generation, timeout, heartbeatStop.Token);
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
            if (!done) await CompleteAttemptAsync(owner, library, id, lease, generation, ct);
        }
        catch (Exception error) when (error is ExportProblem or Amazon.S3.AmazonS3Exception or IOException or OperationCanceledException)
        {
            var reason = error is ExportProblem problem && ExportStates.Reasons.Contains(problem.Code) ? problem.Code : error is OperationCanceledException ? "resource_limit" : "storage_unavailable";
            await using var tx = await db.BeginTransactionAsync(CancellationToken.None);
            await ExportDatabase.LockAsync(db, owner, library, CancellationToken.None);
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='failed',reason=$4,lease_id=NULL,lease_until=NULL,completed_at=now(),expires_at=now()+interval '24 hours' WHERE id=$1 AND lease_id=$2 AND generation=$3 AND state='preparing'", CancellationToken.None, id, lease, generation, reason) != 0)
            {
                Platform.Api.Telemetry.PlatformTelemetry.ExportPreparationDuration.Record(Math.Max(0, (DateTime.UtcNow - created).TotalMilliseconds));
                Platform.Api.Telemetry.PlatformTelemetry.ExportFailed.Add(1, new KeyValuePair<string, object?>("reason", reason)); logger.LogWarning("Export preparation failed {EventType} {ReasonCode}", "exports.failed", reason);
                await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_results SET outcome=jsonb_build_object('itemId',item_id,'status','failed','reason',$2::text,'filename',NULL,'byteLength',NULL,'sha256',NULL) WHERE request_id=$1 AND outcome->>'status'='pending'", CancellationToken.None, id, reason);
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.preparation_failed", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, CancellationToken.None), CancellationToken.None, outcome: "failed", code: reason);
            }
            await tx.CommitAsync(CancellationToken.None);
        }
        finally { await heartbeatStop.CancelAsync(); await heartbeat; }
    }
    private async Task HeartbeatAsync(string owner, string library, Guid id, Guid lease, long generation, CancellationTokenSource preparation, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
                if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET lease_until=now()+interval '2 minutes' WHERE id=$1 AND lease_id=$2 AND generation=$3 AND state='preparing' AND lease_until>now()", ct, id, lease, generation) == 0) { await tx.RollbackAsync(ct); await preparation.CancelAsync(); return; }
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.preparation_heartbeat", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, code: lease.ToString("D"));
                await tx.CommitAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { await preparation.CancelAsync(); }
    }

    private static async Task MaintainRequestAsync(NpgsqlConnection db, Guid id, string owner, string library, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        string state;
        await using (var cmd = Command(db, "SELECT state FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 FOR UPDATE", id, owner, library))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) { await tx.CommitAsync(ct); return; } state = r.GetString(0); }
        if (state == "preparing" && await ScalarAsync(db, "SELECT 1 FROM export_requests WHERE id=$1 AND lease_until<now()", ct, id) is not null)
        {
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='retryable',reason='retryable',generation=generation+1,lease_id=NULL,lease_until=NULL WHERE id=$1 AND state='preparing' AND lease_until<now()", ct, id) != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.lease_recovered", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, outcome: "retryable", code: "retryable");
            state = "retryable";
        }
        else if (state is "completed" or "partial" or "failed" && await ScalarAsync(db, "SELECT 1 FROM export_requests WHERE id=$1 AND expires_at<=now()", ct, id) is not null)
        {
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='expired',reason='expired',generation=generation+1 WHERE id=$1 AND state IN ('completed','partial','failed') AND expires_at<=now()", ct, id) != 0)
            {
                state = "expired";
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.expired", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, outcome: "expired", code: "expired");
            }
        }
        if (state is "expired" or "cancelled" or "cancelling" or "unavailable")
        {
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_download_grants SET revoked_at=now() WHERE request_id=$1 AND revoked_at IS NULL", ct, id) != 0)
            {
                await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_download_sessions SET revoked_at=now() WHERE id IN (SELECT session_id FROM export_download_grants WHERE request_id=$1) AND revoked_at IS NULL", ct, id);
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.download_grants_revoked", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, outcome: state);
            }
            var result = state == "unavailable" ? "unavailable" : "cancelled";
            var reason = state == "unavailable" ? "permanently_unavailable" : "cancelled";
            if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_results SET outcome=jsonb_build_object('itemId',item_id,'status',$2::text,'reason',$3::text,'filename',NULL,'byteLength',NULL,'sha256',NULL) WHERE request_id=$1 AND outcome->>'status'='pending'", ct, id, result, reason) != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.pending_items_settled", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, outcome: result, code: reason);
        }
        await tx.CommitAsync(ct);
    }

    private async Task CompleteAttemptAsync(string owner, string library, Guid id, Guid lease, long generation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        if (await Platform.Storage.IngestDb.ExecuteAsync(db, "UPDATE export_requests SET state='pending',lease_id=NULL,lease_until=NULL WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND lease_id=$4 AND generation=$5 AND state='preparing'", ct, id, owner, library, lease, generation) != 0)
            await ExportDatabase.RecordChangeAsync(db, tx, owner, library, "exports.preparation_requeued", "export_request", id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, id, ct), ct, outcome: "pending");
        await tx.CommitAsync(ct);
    }
}
