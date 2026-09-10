using System.Text.Json;
using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Working;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Operations;

public sealed class OperationService(NpgsqlDataSource dataSource, QueryService queries, WorkingService working, CommitService commits)
{
    public async Task<VersionControlResult> CheckoutAsync(string owner, string library, string client, CheckoutRequest request, string operationId, CancellationToken cancellationToken)
    {
        if (request.DirtyAction is not ("block" or "preserve" or "discard")) throw new VersionControlProblem("invalid_request", 400);
        var branch = RefNames.Normalize(request.Branch);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\n" + client);
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch) is null) throw new VersionControlProblem("ref_not_found", 404);
        var storedVersion = await ScalarAsync(connection, "SELECT version FROM vc_checkouts WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 FOR UPDATE", cancellationToken, owner, library, client);
        var currentVersion = storedVersion is long value ? value : 0;
        if (request.ExpectedVersion != currentVersion) throw new VersionControlProblem("checkout_conflict");
        var dirty = (long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3", cancellationToken, owner, library, client) ?? 0L);
        if (dirty > 0)
        {
            if (request.DirtyAction == "block") throw new VersionControlProblem("dirty_checkout");
            if (request.DirtyAction == "preserve")
            {
                var snapshot = await ScalarAsync(connection, """
                    SELECT jsonb_build_object(
                      'working',COALESCE((SELECT jsonb_agg(to_jsonb(w) ORDER BY w.id) FROM vc_working_changes w WHERE owner_id=$1 AND library_id=$2 AND client_id=$3),'[]'::jsonb),
                      'staged',COALESCE((SELECT jsonb_agg(jsonb_build_object('setId',s.id,'ordinal',e.ordinal,'workingId',e.working_change_id,'workingVersion',e.working_version) ORDER BY s.id,e.ordinal) FROM vc_staging_sets s JOIN vc_staged_entries e ON e.staging_set_id=s.id WHERE s.owner_id=$1 AND s.library_id=$2 AND s.client_id=$3 AND s.status='open'),'[]'::jsonb))::text
                    """, cancellationToken, owner, library, client) as string ?? "{}";
                await ExecuteAsync(connection, "INSERT INTO vc_stashes(id,owner_id,library_id,client_id,source_ref,working_snapshot,created_at) VALUES($1,$2,$3,$4,COALESCE((SELECT normalized_ref FROM vc_checkouts WHERE owner_id=$2 AND library_id=$3 AND client_id=$4),'unassigned'),$5::jsonb,$6)", cancellationToken, Guid.NewGuid(), owner, library, client, snapshot, DateTimeOffset.UtcNow);
            }
            else if (request.DirtyAction != "discard" || !request.ConfirmDiscard) throw new VersionControlProblem("confirmation_required");
            await ExecuteAsync(connection, "DELETE FROM vc_staged_entries e USING vc_staging_sets s WHERE e.staging_set_id=s.id AND s.owner_id=$1 AND s.library_id=$2 AND s.client_id=$3 AND s.status='open'; UPDATE vc_staging_sets SET status='abandoned',version=version+1 WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND status='open'", cancellationToken, owner, library, client);
            await ExecuteAsync(connection, "DELETE FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3", cancellationToken, owner, library, client);
        }
        if (currentVersion == 0)
            await ExecuteAsync(connection, "INSERT INTO vc_checkouts(owner_id,library_id,client_id,normalized_ref) VALUES($1,$2,$3,$4)", cancellationToken, owner, library, client, branch);
        else
            await ExecuteAsync(connection, "UPDATE vc_checkouts SET normalized_ref=$4,version=version+1 WHERE owner_id=$1 AND library_id=$2 AND client_id=$3", cancellationToken, owner, library, client, branch);
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.checkout", RefService.StableResource(client), "accepted", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(operationId, RefName: branch, Version: currentVersion + 1);
    }

    public async Task<VersionControlResult> RestoreAsync(string owner, string library, string client, RestoreRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        if (request.Mode is not ("restore" or "revert")) throw new VersionControlProblem("invalid_request", 400);
        var current = await queries.StateAsync(owner, library, request.Branch, cancellationToken);
        var target = await queries.StateAsync(owner, library, request.TargetCommitId, cancellationToken);
        if (request.Mode == "revert")
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
            var commit = graph[target.Head];
            target = commit.Parents.Count == 0 ? (target.Head, new()) : (commit.Parents[0], GraphQueries.State(graph, commit.Parents[0]));
        }
        var workingIds = new List<Guid>();
        foreach (var key in current.State.Keys.Union(target.State.Keys).Order(StringComparer.Ordinal))
        {
            current.State.TryGetValue(key, out var before);
            target.State.TryGetValue(key, out var after);
            if (before?.ContentSha256 == after?.ContentSha256 && before?.Path == after?.Path && before?.Lifecycle == after?.Lifecycle) continue;
            var identity = after ?? before!;
            var change = after is null ? "deleted" : before is null ? "added" : "modified";
            var result = await working.PutAsync(owner, library, client, new(identity.AssetId, identity.EditId, request.Branch, change, before?.ContentSha256, after?.ContentSha256, after?.Path, after?.Lifecycle, 0), operationId, cancellationToken);
            workingIds.Add(result.Id);
        }
        if (workingIds.Count == 0) throw new VersionControlProblem("no_changes", 400);
        var set = await working.StageAsync(owner, library, client, request.Branch, new(workingIds, current.Head, 0), operationId, cancellationToken);
        return await commits.PublishAsync(owner, library, client, new(set, request.Branch, current.Head, request.Message, DateTimeOffset.UtcNow), idempotencyKey, operationId, cancellationToken);
    }
}
