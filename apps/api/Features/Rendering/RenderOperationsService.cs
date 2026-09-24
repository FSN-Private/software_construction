using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Rendering;

public sealed record RenderOperationsView(bool Paused, int Cancelled, int Recovered);

public sealed class RenderOperationsService(NpgsqlDataSource dataSource)
{
    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ScalarAsync(connection, "SELECT paused FROM render_intake_control WHERE singleton", cancellationToken) as bool? ?? false;
    }

    public async Task<RenderOperationsView> SetAsync(string action, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var paused = action is "pause" or "drain" or "cancel";
        if (action is not ("pause" or "resume" or "drain" or "cancel")) throw new RenderingProblem("invalid_render_operation", 400);
        await ExecuteAsync(connection, "UPDATE render_intake_control SET paused=$1,updated_at=now() WHERE singleton", cancellationToken, paused);
        var cancelled = action == "cancel" ? await ExecuteAsync(connection, "WITH pending AS (SELECT a.id,a.request_id,r.owner_id,r.library_id FROM render_attempts a JOIN render_requests r ON r.id=a.request_id WHERE a.state='pending') UPDATE render_attempts a SET state='cancelled',completed_at=now() FROM pending p WHERE a.id=p.id; UPDATE render_requests SET state='cancelled',updated_at=now() WHERE state='pending'", cancellationToken) : 0;
        await transaction.CommitAsync(cancellationToken);
        return new(paused, cancelled, 0);
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var recovered = await ExecuteAsync(connection, "UPDATE render_attempts SET state='retryable',failure_code='renderer_timeout',completed_at=now() WHERE state='running' AND started_at < now()-interval '10 minutes'", cancellationToken);
        if (recovered > 0) await ExecuteAsync(connection, "UPDATE render_requests SET state='retryable',updated_at=now() WHERE state='running' AND NOT EXISTS(SELECT 1 FROM render_attempts WHERE request_id=render_requests.id AND state='running')", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }
}

public sealed class RenderRecoveryWorker(RenderOperationsService operations, ILogger<RenderRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try { if (await operations.RecoverAsync(stoppingToken) > 0) logger.LogWarning("Recovered stale renderer attempts"); }
            catch (Exception error) when (error is NpgsqlException or OperationCanceledException) { logger.LogWarning("Renderer recovery unavailable"); }
    }
}
