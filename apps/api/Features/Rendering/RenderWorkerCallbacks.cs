using Npgsql;
using Platform.Contracts.Rendering;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Rendering;

/// <summary>API implementation of the authenticated worker callback boundary.</summary>
public sealed class RenderWorkerCallbacks(NpgsqlDataSource dataSource) : IRenderWorkerCallback
{
    public async Task<bool> ClaimAsync(Guid requestId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await ExecuteAsync(connection, "UPDATE render_attempts SET state='running',started_at=now() WHERE id=$1 AND request_id=$2 AND state='pending'", cancellationToken, attemptId, requestId) != 1) return false;
        await ExecuteAsync(connection, "UPDATE render_requests SET state='running',updated_at=now() WHERE id=$1 AND state='pending'", cancellationToken, requestId);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CompleteAsync(Guid requestId, Guid attemptId, string privateObjectKey, string privateObjectVersion, string outputSha256, long byteLength, string mediaType, RenderProvenance provenance, CancellationToken cancellationToken = default)
    {
        provenance.Validate();
        new RenderPublication(privateObjectKey, privateObjectVersion, outputSha256, byteLength, mediaType).Validate();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var request = Command(connection, "SELECT owner_id,library_id,selected_version,renderer_release,renderer_artifact_digest,profile,matrix_version FROM render_requests WHERE id=$1 FOR UPDATE", requestId);
        await using var reader = await request.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RenderingProblem("rendition_not_found", 404);
        var owner = reader.GetString(0);
        var library = reader.GetString(1);
        if (provenance.SelectedVersion != reader.GetString(2) || provenance.RendererRelease != reader.GetString(3) || provenance.RendererArtifactDigest != reader.GetString(4) || provenance.Profile != reader.GetString(5) || provenance.CompatibilityMatrix != reader.GetString(6)) throw new RenderingProblem("render_provenance_mismatch");
        await reader.DisposeAsync();
        if (await ExecuteAsync(connection, "UPDATE render_attempts SET state='completed',completed_at=now(),failure_code=NULL WHERE id=$1 AND request_id=$2 AND state IN ('pending','running')", cancellationToken, attemptId, requestId) != 1) throw new RenderingProblem("invalid_render_transition");
        var renditionId = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO renditions(id,request_id,object_key,object_version,output_sha256,byte_length,media_type,completed_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", cancellationToken, renditionId, requestId, privateObjectKey, privateObjectVersion, outputSha256, byteLength, mediaType, provenance.CompletedAtUtc);
        await ExecuteAsync(connection, "INSERT INTO render_provenance(rendition_id,source_sha256,xmp_sha256,selected_version,renderer_release,renderer_artifact_digest,profile,matrix_version,completed_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)", cancellationToken, renditionId, provenance.SourceSha256, provenance.XmpSha256, provenance.SelectedVersion, provenance.RendererRelease, provenance.RendererArtifactDigest, provenance.Profile, provenance.CompatibilityMatrix, provenance.CompletedAtUtc);
        await ExecuteAsync(connection, "UPDATE render_requests SET state='completed',updated_at=now() WHERE id=$1", cancellationToken, requestId);
        await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type) VALUES($1,$2,$3,$4,$5,'completed')", cancellationToken, Guid.NewGuid(), owner, library, requestId, attemptId);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailAsync(Guid requestId, Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default)
    {
        if (!RenderFailureCodes.IsSafe(safeFailureCode)) throw new ArgumentException("invalid_render_failure_code");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var request = Command(connection, "SELECT owner_id,library_id FROM render_requests WHERE id=$1 FOR UPDATE", requestId);
        await using var reader = await request.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RenderingProblem("rendition_not_found", 404);
        var owner = reader.GetString(0);
        var library = reader.GetString(1);
        await reader.DisposeAsync();
        if (await ExecuteAsync(connection, "UPDATE render_attempts SET state='failed',failure_code=$3,completed_at=now() WHERE id=$1 AND request_id=$2 AND state IN ('pending','running')", cancellationToken, attemptId, requestId, safeFailureCode) != 1) throw new RenderingProblem("invalid_render_transition");
        await ExecuteAsync(connection, "UPDATE render_requests SET state='failed',updated_at=now() WHERE id=$1", cancellationToken, requestId);
        await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type,reason_code) VALUES($1,$2,$3,$4,$5,'failed',$6)", cancellationToken, Guid.NewGuid(), owner, library, requestId, attemptId, safeFailureCode);
        await transaction.CommitAsync(cancellationToken);
    }
}
