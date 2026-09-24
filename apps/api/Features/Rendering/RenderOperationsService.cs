using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Rendering;

public sealed record RenderOperationsView(bool Paused, int Cancelled, int Recovered);

public sealed class RenderOperationsService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ScalarAsync(connection, "SELECT paused FROM render_intake_control WHERE singleton", cancellationToken) as bool? ?? false;
    }

    public async Task<bool> IsAcceptanceOwnerAsync(string owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ScalarAsync(connection, "SELECT paused AND acceptance_owner=$1 FROM render_intake_control WHERE singleton", cancellationToken, owner) as bool? ?? false;
    }

    public async Task<RenderOperationsView> SetAsync(string action, string owner, string operatorName, CancellationToken cancellationToken)
    {
        if (action is not ("pause" or "resume" or "drain" or "cancel" or "admit" or "activate" or "abort")) throw new RenderingProblem("invalid_render_operation", 400);
        if (!string.Equals(operatorName, configuration["Rendering:OperationsUsername"], StringComparison.Ordinal)) throw new RenderingProblem("render_operation_forbidden", 403);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var paused = action is "pause" or "drain" or "cancel";
        var acceptanceOwner = (string?)null;
        if (action == "admit")
        {
            var digest = configuration["Rendering:DeployedArtifactDigest"];
            var corpusEvidenceId = configuration["Rendering:PromotionEvidenceId"];
            if (digest is null || corpusEvidenceId is null || !corpusEvidenceId.StartsWith("trusted-linux-", StringComparison.Ordinal)) throw new RenderingProblem("invalid_render_promotion", 400);
            var admitted = await ExecuteAsync(connection, "INSERT INTO rendering_compatibility_candidates(matrix_version,renderer_artifact_digest,renderer_release,profile,corpus_evidence_id,admitted_by,state) SELECT $1,$2,c.renderer_release,c.profile,$3,$4,'accepted' FROM rendering_compatibility c WHERE c.matrix_version=$1 AND c.profile=$5 AND c.state='approved' ON CONFLICT(matrix_version,renderer_artifact_digest) DO UPDATE SET corpus_evidence_id=EXCLUDED.corpus_evidence_id,admitted_at=now(),admitted_by=EXCLUDED.admitted_by,state='accepted'", cancellationToken, configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1", digest, corpusEvidenceId, operatorName, configuration["Rendering:RenderProfile"] ?? "render-v1");
            if (admitted != 1) throw new RenderingProblem("render_promotion_rejected", 409);
            paused = true;
            acceptanceOwner = owner;
            await EventAsync(connection, operatorName, "compatibility_admitted", cancellationToken);
        }
        if (action == "activate")
        {
            var digest = configuration["Rendering:DeployedArtifactDigest"];
            var evidence = configuration["Rendering:PromotionEvidenceId"];
            if (digest is null || evidence is null) throw new RenderingProblem("invalid_render_promotion", 400);
            var activated = await ExecuteAsync(connection, "UPDATE rendering_compatibility c SET renderer_artifact_digest=$1,state='approved',approved_at=now(),approved_by=$2,corpus_evidence_id=$3 FROM rendering_compatibility_candidates candidate, render_intake_control control WHERE c.matrix_version=$4 AND candidate.matrix_version=c.matrix_version AND candidate.renderer_artifact_digest=$1 AND candidate.corpus_evidence_id=$3 AND candidate.state='accepted' AND control.singleton AND control.paused AND control.acceptance_owner=$5", cancellationToken, digest, operatorName, evidence, configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1", owner);
            if (activated != 1) throw new RenderingProblem("render_promotion_rejected", 409);
            await ExecuteAsync(connection, "UPDATE rendering_compatibility_candidates SET state='activated' WHERE matrix_version=$1 AND renderer_artifact_digest=$2", cancellationToken, configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1", digest);
            paused = false;
            await EventAsync(connection, operatorName, "compatibility_activated", cancellationToken);
        }
        if (action == "abort")
        {
            var digest = configuration["Rendering:DeployedArtifactDigest"];
            if (digest is null) throw new RenderingProblem("invalid_render_promotion", 400);
            await ExecuteAsync(connection, "UPDATE rendering_compatibility_candidates SET state='rejected' WHERE matrix_version=$1 AND renderer_artifact_digest=$2 AND state='accepted'", cancellationToken, configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1", digest);
            paused = false;
        }
        await ExecuteAsync(connection, "UPDATE render_intake_control SET paused=$1,acceptance_owner=$2,updated_at=now() WHERE singleton", cancellationToken, paused, acceptanceOwner);
        if (action is "pause" or "drain" or "cancel") await EventAsync(connection, operatorName, "intake_paused", cancellationToken);
        if (action is "resume" or "abort") await EventAsync(connection, operatorName, "intake_resumed", cancellationToken);
        var cancelled = action == "cancel" ? await CancelAsync(connection, cancellationToken) : 0;
        await transaction.CommitAsync(cancellationToken);
        return new(paused, cancelled, 0);
    }

    private static Task EventAsync(NpgsqlConnection connection, string operatorName, string eventType, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type) VALUES($1,$2,'*',NULL,NULL,$3)", cancellationToken, Guid.NewGuid(), operatorName, eventType);

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

public sealed class RenderRecoveryWorker(RenderOperationsService operations, RenderingService rendering, ILogger<RenderRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try
            {
                var recovered = await operations.RecoverAsync(stoppingToken);
                var redispatched = await rendering.RedispatchPendingAsync(stoppingToken);
                if (recovered > 0 || redispatched > 0) logger.LogWarning("Recovered stale renderer attempts or redispatched pending work");
            }
            catch (Exception error) when (error is NpgsqlException or OperationCanceledException) { logger.LogWarning("Renderer recovery unavailable"); }
    }
}
