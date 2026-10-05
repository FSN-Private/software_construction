using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;
using Platform.Api.Features.VersionControl.Query;

namespace Platform.Api.Features.VersionControl.Visibility;

public sealed class VisibilityService(NpgsqlDataSource dataSource, TimeProvider? clock = null)
{
    public async Task<VersionControlResult> SetAsync(string owner, string library, VisibilityRequest request, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await HistoryRetention.LockAsync(connection, owner, library, cancellationToken);
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3", cancellationToken, request.CommitId, owner, library) is null) throw new VersionControlProblem("commit_not_found", 404);
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        var hiddenHistory = HistoryRetention.Reachable(graph, [request.CommitId]);
        await ExecuteAsync(connection, "INSERT INTO vc_visibility(owner_id,library_id,commit_id,hidden,changed_at,changed_by) VALUES($1,$2,$3,$4,$5,$2) ON CONFLICT(owner_id,library_id,commit_id) DO UPDATE SET hidden=EXCLUDED.hidden,changed_at=EXCLUDED.changed_at,changed_by=EXCLUDED.changed_by", cancellationToken, owner, library, request.CommitId, request.Hidden, (clock ?? TimeProvider.System).GetUtcNow());
        if (request.Hidden) await HistoryRetention.UpdateExpiryAsync(connection, hiddenHistory, null, cancellationToken);
        else
        {
            await HistoryRetention.ReconcileAsync(connection, graph, owner, library, hiddenHistory, (clock ?? TimeProvider.System).GetUtcNow(), cancellationToken);
        }
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, request.Hidden ? "version-control.commit.hidden" : "version-control.commit.unhidden", RefService.StableResource(request.CommitId), "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            request.Hidden ? "version-control.commit.hidden" : "version-control.commit.unhidden", "commit-visibility",
            request.CommitId, [request.CommitId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(operationId, request.CommitId);
    }
}
