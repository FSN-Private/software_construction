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
        var cancelled = action == "cancel" ? await CancelAsync(connection, cancellationToken) : 0;
        await transaction.CommitAsync(cancellationToken);
        return new(paused, cancelled, 0);
    }

    private static async Task<int> CancelAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var cancelled = new List<(Guid Attempt, Guid Request, string Owner, string Library)>();
        await using (var command = Command(connection, "UPDATE render_attempts a SET state='cancelled',completed_at=now() FROM render_requests r WHERE r.id=a.request_id AND a.state='pending' RETURNING a.id,a.request_id,r.owner_id,r.library_id"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) cancelled.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        foreach (var item in cancelled)
        {
            await ExecuteAsync(connection, "UPDATE render_requests SET state='cancelled',updated_at=now() WHERE id=$1 AND state='pending'", cancellationToken, item.Request);
            await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type) VALUES($1,$2,$3,$4,$5,'cancelled')", cancellationToken, Guid.NewGuid(), item.Owner, item.Library, item.Request, item.Attempt);
        }
        return cancelled.Count;
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
