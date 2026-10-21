using System.Text.Json;
using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Rendering;

// Server-authored receipts use existing operator audit storage. Callers can
// select only an opaque receipt, never an arbitrary digest or approval tuple.
public static class RenderPromotionRollback
{
    private sealed record Snapshot(string Matrix, string Release, string Digest, string Profile, string Evidence,
        DateTime ApprovedAt, string ApprovedBy, string Candidate, string CandidateEvidence);

    public static async Task LockAsync(NpgsqlConnection connection, IConfiguration configuration, CancellationToken ct)
    {
        await ScalarAsync(connection, "SELECT matrix_version FROM rendering_compatibility WHERE matrix_version=$1 FOR UPDATE", ct, configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1");
        await ScalarAsync(connection, "SELECT singleton FROM render_intake_control WHERE singleton FOR UPDATE", ct);
    }

    public static async Task<Guid> CaptureAsync(NpgsqlConnection connection, IConfiguration configuration, string operatorName, CancellationToken ct)
    {
        var matrix = configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1";
        await using var command = Command(connection, "SELECT renderer_release,renderer_artifact_digest,profile,corpus_evidence_id,approved_at,approved_by FROM rendering_compatibility WHERE matrix_version=$1 AND state='approved'", matrix);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new RenderingProblem("render_promotion_rejected", 409);
        var snapshot = new Snapshot(matrix, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4), reader.GetString(5),
            configuration["Rendering:DeployedArtifactDigest"] ?? "", configuration["Rendering:PromotionEvidenceId"] ?? "");
        await reader.DisposeAsync();
        var receipt = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,event_type,reason_code,created_at) VALUES($1,$2,$3,'compatibility_admitted',$4,clock_timestamp())", ct,
            receipt, operatorName, "rollback:" + matrix, JsonSerializer.Serialize(snapshot));
        return receipt;
    }

    public static async Task<RenderOperationsView> RestoreAsync(NpgsqlDataSource dataSource, IConfiguration configuration, Guid receipt, string operatorName, string? previousDigest, CancellationToken ct)
    {
        if (operatorName != configuration["Rendering:OperationsUsername"]) throw new RenderingProblem("render_operation_forbidden", 403);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, configuration, ct);
        var matrix = configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1";
        await using var command = Command(connection, "SELECT id,reason_code,owner_id FROM render_events WHERE library_id=$1 AND event_type='compatibility_admitted' ORDER BY created_at DESC,id DESC LIMIT 1", "rollback:" + matrix);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetGuid(0) != receipt || reader.GetString(2) != operatorName) throw new RenderingProblem("render_rollback_rejected", 409);
        var prior = JsonSerializer.Deserialize<Snapshot>(reader.GetString(1)) ?? throw new RenderingProblem("render_rollback_rejected", 409);
        await reader.DisposeAsync();
        if (prior.Matrix != matrix || prior.Candidate != configuration["Rendering:DeployedArtifactDigest"] || prior.CandidateEvidence != configuration["Rendering:PromotionEvidenceId"])
            throw new RenderingProblem("render_rollback_rejected", 409);
        if (previousDigest is not null && previousDigest != prior.Digest) throw new RenderingProblem("render_rollback_rejected", 409);
        // Exact previous state permits replay/pre-activation recovery; exact
        // activated candidate permits rollback after a lost activation response.
        var restored = await ExecuteAsync(connection, """
            UPDATE rendering_compatibility current SET renderer_release=$2,renderer_artifact_digest=$3,profile=$4,
                corpus_evidence_id=$5,approved_at=$6,approved_by=$7,state='approved'
            WHERE current.matrix_version=$1 AND current.state='approved' AND current.renderer_release=$2 AND current.profile=$4 AND
                EXISTS(SELECT 1 FROM rendering_compatibility_candidates candidate WHERE candidate.matrix_version=$1
                    AND candidate.renderer_artifact_digest=$8 AND candidate.corpus_evidence_id=$9 AND candidate.admitted_by=$10 AND
                    ((current.renderer_artifact_digest=$8 AND current.corpus_evidence_id=$9 AND candidate.state='activated') OR
                     (current.renderer_artifact_digest=$3 AND current.corpus_evidence_id=$5 AND current.approved_at=$6 AND current.approved_by=$7 AND candidate.state IN ('accepted','rejected'))))
            """, ct, matrix, prior.Release, prior.Digest, prior.Profile, prior.Evidence, prior.ApprovedAt, prior.ApprovedBy, prior.Candidate, prior.CandidateEvidence, operatorName);
        if (restored != 1) throw new RenderingProblem("render_rollback_rejected", 409);
        await ExecuteAsync(connection, "UPDATE rendering_compatibility_candidates SET state='rejected' WHERE matrix_version=$1 AND renderer_artifact_digest=$2 AND corpus_evidence_id=$3", ct, matrix, prior.Candidate, prior.CandidateEvidence);
        await ExecuteAsync(connection, "UPDATE render_intake_control SET paused=true,acceptance_owner=NULL,updated_at=now() WHERE singleton", ct);
        await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,event_type) VALUES($1,$2,'*','intake_paused')", ct, Guid.NewGuid(), operatorName);
        await transaction.CommitAsync(ct);
        var pending = Convert.ToInt32(await ScalarAsync(connection, "SELECT count(*) FROM render_attempts WHERE state IN ('pending','running')", ct), System.Globalization.CultureInfo.InvariantCulture);
        return new(true, pending, 0, 0);
    }
}
