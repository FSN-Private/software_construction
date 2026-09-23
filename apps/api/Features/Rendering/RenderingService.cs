using Npgsql;
using Platform.Contracts.Rendering;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Rendering;

public sealed record RenditionRequestInput(Guid AssetId, Guid EditId, string SelectedVersion, RenditionKind Rendition, string Profile, string LibraryId);
public sealed record RenditionStatusView(Guid RequestId, Guid? AttemptId, RenderState State, bool CacheHit, RenderProvenance? Provenance, string? FailureCode);

public sealed class RenderingProblem(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class RenderingService(NpgsqlDataSource dataSource, ISelectedVersionResolver versions, IConfiguration configuration)
{
    public async Task<RenditionStatusView> RequestAsync(string owner, RenditionRequestInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(input.LibraryId) || input.AssetId == Guid.Empty || input.EditId == Guid.Empty || string.IsNullOrWhiteSpace(input.SelectedVersion) || string.IsNullOrWhiteSpace(input.Profile))
            throw new RenderingProblem("invalid_render_request", 400);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var source = await SourceAsync(connection, owner, input.LibraryId, input.AssetId, input.EditId, cancellationToken) ?? throw new RenderingProblem("source_unavailable", 404);
        var selected = input.SelectedVersion == "current"
            ? SelectedVersionResolution.Current(source.XmpSha256)
            : await versions.ResolveAsync(owner, input.LibraryId, input.AssetId, input.EditId, input.SelectedVersion, cancellationToken);
        if (!selected.IsAvailable) throw new RenderingProblem(selected.FailureCode ?? "selected_version_unavailable", 404);

        var compatibility = await CompatibilityAsync(connection, input.Profile, cancellationToken) ?? throw new RenderingProblem("incompatible_renderer");
        var request = new RenditionRequest(owner, input.LibraryId, input.AssetId, input.EditId, input.SelectedVersion, input.Rendition, input.Profile, compatibility.Release, compatibility.Digest, compatibility.Matrix);
        request.Validate();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var requestId = Guid.NewGuid();
        var created = await ScalarAsync(connection, "INSERT INTO render_requests(id,owner_id,library_id,request_key,asset_id,edit_id,selected_version,rendition,profile,renderer_release,renderer_artifact_digest,matrix_version,state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,'pending') ON CONFLICT(owner_id,library_id,request_key) DO NOTHING RETURNING id", cancellationToken, requestId, owner, input.LibraryId, request.Key, input.AssetId, input.EditId, input.SelectedVersion, Rendition(input.Rendition), input.Profile, compatibility.Release, compatibility.Digest, compatibility.Matrix) as Guid?;
        var id = created ?? (Guid?)await ScalarAsync(connection, "SELECT id FROM render_requests WHERE owner_id=$1 AND library_id=$2 AND request_key=$3", cancellationToken, owner, input.LibraryId, request.Key) ?? throw new RenderingProblem("rendering_unavailable", 503);
        if (created.HasValue)
        {
            var attempt = Guid.NewGuid();
            await ExecuteAsync(connection, "INSERT INTO render_attempts(id,request_id,attempt_number,state,worker_policy_version) VALUES($1,$2,1,'pending','render-policy-v1')", cancellationToken, attempt, id);
            await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type) VALUES($1,$2,$3,$4,$5,'requested'),($6,$2,$3,$4,$5,'queued')", cancellationToken, Guid.NewGuid(), owner, input.LibraryId, id, attempt, Guid.NewGuid());
        }
        var result = await StatusAsync(connection, owner, input.LibraryId, id, created is null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RenditionStatusView> StatusAsync(string owner, string library, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await StatusAsync(connection, owner, library, requestId, false, cancellationToken);
    }

    public async Task<RenditionStatusView> RetryAsync(string owner, string library, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Command(connection, "SELECT state FROM render_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 FOR UPDATE", requestId, owner, library);
        var state = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (state is null) throw new RenderingProblem("rendition_not_found", 404);
        if (state is not ("failed" or "retryable" or "unavailable")) throw new RenderingProblem("rendition_not_retryable");
        var number = Convert.ToInt32(await ScalarAsync(connection, "SELECT COALESCE(MAX(attempt_number),0)+1 FROM render_attempts WHERE request_id=$1", cancellationToken, requestId), System.Globalization.CultureInfo.InvariantCulture);
        var attempt = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO render_attempts(id,request_id,attempt_number,state,worker_policy_version) VALUES($1,$2,$3,'pending','render-policy-v1')", cancellationToken, attempt, requestId, number);
        await ExecuteAsync(connection, "UPDATE render_requests SET state='pending',updated_at=now() WHERE id=$1", cancellationToken, requestId);
        await ExecuteAsync(connection, "INSERT INTO render_events(id,owner_id,library_id,request_id,attempt_id,event_type) VALUES($1,$2,$3,$4,$5,'retried'),($6,$2,$3,$4,$5,'queued')", cancellationToken, Guid.NewGuid(), owner, library, requestId, attempt, Guid.NewGuid());
        var result = await StatusAsync(connection, owner, library, requestId, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<RenditionStatusView> StatusAsync(NpgsqlConnection connection, string owner, string library, Guid requestId, bool existing, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT r.state,a.id,a.failure_code,p.source_sha256,p.xmp_sha256,p.selected_version,p.renderer_release,p.renderer_artifact_digest,p.profile,p.matrix_version,p.completed_at FROM render_requests r LEFT JOIN LATERAL (SELECT id,failure_code FROM render_attempts WHERE request_id=r.id ORDER BY attempt_number DESC LIMIT 1) a ON true LEFT JOIN renditions d ON d.request_id=r.id LEFT JOIN render_provenance p ON p.rendition_id=d.id WHERE r.id=$1 AND r.owner_id=$2 AND r.library_id=$3", requestId, owner, library);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RenderingProblem("rendition_not_found", 404);
        var state = ParseState(reader.GetString(0));
        var attempt = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var failure = reader.IsDBNull(2) ? null : reader.GetString(2);
        var provenance = reader.IsDBNull(3) ? null : new RenderProvenance(reader.GetString(3).Trim(), reader.GetString(4).Trim(), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10));
        return new(requestId, attempt, state, existing && state == RenderState.Completed, provenance, failure);
    }

    private async Task<Compatibility?> CompatibilityAsync(NpgsqlConnection connection, string profile, CancellationToken cancellationToken)
    {
        var matrix = configuration["Rendering:CompatibilityMatrix"] ?? "rendering-v1";
        await using var command = Command(connection, "SELECT matrix_version,renderer_release,renderer_artifact_digest FROM rendering_compatibility WHERE matrix_version=$1 AND profile=$2 AND state='approved'", matrix, profile);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<Source?> SourceAsync(NpgsqlConnection connection, string owner, string library, Guid asset, Guid edit, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT a.original_sha256,e.xmp_sha256 FROM assets a JOIN edit_identities e ON e.asset_id=a.id WHERE a.id=$1 AND e.id=$2 AND a.owner_id=$3 AND a.state IN ('active','restored') AND EXISTS(SELECT 1 FROM vc_refs WHERE owner_id=$3 AND library_id=$4)", asset, edit, owner, library);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0) && !reader.IsDBNull(1) ? new(reader.GetString(0).Trim(), reader.GetString(1).Trim()) : null;
    }

    private static string Rendition(RenditionKind value) => value switch { RenditionKind.Thumbnail512 => "thumbnail-512", RenditionKind.Preview2048 => "preview-2048", _ => throw new RenderingProblem("invalid_render_request", 400) };
    private static RenderState ParseState(string value) => Enum.TryParse<RenderState>(value, true, out var state) ? state : throw new RenderingProblem("rendering_unavailable", 503);
    private sealed record Source(string OriginalSha256, string XmpSha256);
    private sealed record Compatibility(string Matrix, string Release, string Digest);
}
