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

public sealed class OperationService(NpgsqlDataSource dataSource, QueryService queries, CommitService commits)
{
    public async Task<VersionControlResult> CheckoutAsync(string owner, string library, string client, CheckoutRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        if (request.DirtyAction is not ("block" or "preserve" or "discard")) throw new VersionControlProblem("invalid_request", 400);
        var branch = RefNames.Normalize(request.Branch);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await WorkingService.LockClientAsync(connection, owner, library, client, cancellationToken);
        var fingerprint = VersionControlIdempotency.Fingerprint(new { operation = "checkout", request });
        var replay = await VersionControlIdempotency.ReadAsync<VersionControlResult>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay with { OperationId = operationId };
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch) is null) throw new VersionControlProblem("ref_not_found", 404);
        var storedVersion = await ScalarAsync(connection, "SELECT version FROM vc_checkouts WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 FOR UPDATE", cancellationToken, owner, library, client);
        var currentVersion = storedVersion is long value ? value : 0;
        if (request.ExpectedVersion != currentVersion) throw new VersionControlProblem("checkout_conflict");
        var dirty = (long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3", cancellationToken, owner, library, client) ?? 0L);
        Guid? stashId = null;
        if (dirty > 0)
        {
            if (request.DirtyAction == "block") throw new VersionControlProblem("dirty_checkout");
            if (request.DirtyAction == "preserve")
            {
                var snapshot = await ScalarAsync(connection, """
                    SELECT jsonb_build_object(
                      'working',COALESCE((SELECT jsonb_agg(to_jsonb(w) ORDER BY w.id) FROM vc_working_changes w WHERE owner_id=$1 AND library_id=$2 AND client_id=$3),'[]'::jsonb),
                      'sets',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.id) FROM vc_staging_sets s WHERE s.owner_id=$1 AND s.library_id=$2 AND s.client_id=$3 AND s.status='open'),'[]'::jsonb),
                      'staged',COALESCE((SELECT jsonb_agg(jsonb_build_object('setId',s.id,'ordinal',e.ordinal,'workingId',e.working_change_id,'workingVersion',e.working_version) ORDER BY s.id,e.ordinal) FROM vc_staging_sets s JOIN vc_staged_entries e ON e.staging_set_id=s.id WHERE s.owner_id=$1 AND s.library_id=$2 AND s.client_id=$3 AND s.status='open'),'[]'::jsonb))::text
                    """, cancellationToken, owner, library, client) as string ?? "{}";
                stashId = Guid.NewGuid();
                await ExecuteAsync(connection, "INSERT INTO vc_stashes(id,owner_id,library_id,client_id,source_ref,working_snapshot,created_at) VALUES($1,$2,$3,$4,COALESCE((SELECT normalized_ref FROM vc_checkouts WHERE owner_id=$2 AND library_id=$3 AND client_id=$4),'unassigned'),$5::jsonb,$6)", cancellationToken, stashId, owner, library, client, snapshot, DateTimeOffset.UtcNow);
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
        var result = new VersionControlResult(operationId, RefName: branch, Version: currentVersion + 1, StashId: stashId);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<StashView>> ListStashesAsync(string owner, string library, string client, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, "SELECT id,source_ref,working_snapshot::text,created_at FROM vc_stashes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 ORDER BY created_at,id", owner, library, client);
        var result = new List<StashView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0), reader.GetString(1), JsonDocument.Parse(reader.GetString(2)).RootElement.Clone(), reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    public async Task<VersionControlResult> RestoreStashAsync(string owner, string library, string client, Guid stashId, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await WorkingService.LockClientAsync(connection, owner, library, client, cancellationToken);
        var fingerprint = VersionControlIdempotency.Fingerprint(new { operation = "stash.restore", stashId });
        var replay = await VersionControlIdempotency.ReadAsync<VersionControlResult>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay with { OperationId = operationId };
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 LIMIT 1", cancellationToken, owner, library, client) is not null) throw new VersionControlProblem("working_state_conflict");
        var snapshot = await ScalarAsync(connection, "SELECT working_snapshot::text FROM vc_stashes WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND client_id=$4 FOR UPDATE", cancellationToken, stashId, owner, library, client) as string ?? throw new VersionControlProblem("stash_not_found", 404);
        await ExecuteAsync(connection, """
            INSERT INTO vc_working_changes(id,owner_id,library_id,client_id,branch_name,asset_id,edit_id,base_commit_id,base_content_sha256,current_content_sha256,change_kind,display_path,lifecycle_state,version,created_at,updated_at)
            SELECT id,owner_id,library_id,client_id,branch_name,asset_id,edit_id,base_commit_id,base_content_sha256,current_content_sha256,change_kind,display_path,lifecycle_state,version,created_at,updated_at
            FROM jsonb_to_recordset(($1::jsonb)->'working') AS w(id uuid,owner_id text,library_id text,client_id text,branch_name text,asset_id uuid,edit_id uuid,base_commit_id char(64),base_content_sha256 char(64),current_content_sha256 char(64),change_kind text,display_path text,lifecycle_state text,version bigint,created_at timestamptz,updated_at timestamptz);
            UPDATE vc_staging_sets s SET status='open',version=j.version FROM jsonb_to_recordset(($1::jsonb)->'sets') AS j(id uuid,version bigint) WHERE s.id=j.id AND s.owner_id=$2 AND s.library_id=$3 AND s.client_id=$4 AND s.status='abandoned';
            INSERT INTO vc_staged_entries(staging_set_id,ordinal,working_change_id,working_version)
            SELECT "setId",ordinal,"workingId","workingVersion" FROM jsonb_to_recordset(($1::jsonb)->'staged') AS e("setId" uuid,ordinal integer,"workingId" uuid,"workingVersion" bigint);
            DELETE FROM vc_stashes WHERE id=$5;
            """, cancellationToken, snapshot, owner, library, client, stashId);
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.stash.restored", stashId, "accepted", cancellationToken);
        var result = new VersionControlResult(operationId, StashId: stashId);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<VersionControlResult> RestoreAsync(string owner, string library, string client, RestoreRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        if (request.Mode is not ("restore" or "revert")) throw new VersionControlProblem("invalid_request", 400);
        await using (var replayConnection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var replay = await VersionControlIdempotency.ReadAsync<VersionControlResult>(replayConnection, owner, library, idempotencyKey, CommitService.Fingerprint(request, null), cancellationToken);
            if (replay is not null) return replay with { OperationId = operationId };
        }
        var current = await queries.StateAsync(owner, library, request.Branch, cancellationToken);
        if (!string.Equals(current.Head, request.ExpectedHead, StringComparison.Ordinal)) throw new VersionControlProblem("stale_head");
        var target = await queries.StateAsync(owner, library, request.TargetCommitId, cancellationToken);
        IEnumerable<string> keys = current.State.Keys.Union(target.State.Keys);
        if (request.Mode == "revert")
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
            var commit = graph[target.Head];
            var parentState = commit.Parents.Count == 0 ? new Dictionary<string, ChangeView>() : GraphQueries.State(graph, commit.Parents[0]);
            foreach (var key in commit.Entries.Select(GraphQueries.Key))
            {
                current.State.TryGetValue(key, out var currentEntry);
                target.State.TryGetValue(key, out var targetEntry);
                if (!Same(currentEntry, targetEntry)) throw new VersionControlProblem("unresolved_divergence");
            }
            target = (target.Head, parentState);
            keys = commit.Entries.Select(GraphQueries.Key);
        }
        var entries = new List<VersionControlEntry>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            current.State.TryGetValue(key, out var before);
            target.State.TryGetValue(key, out var after);
            if (before?.ContentSha256 == after?.ContentSha256 && before?.Path == after?.Path && before?.Lifecycle == after?.Lifecycle) continue;
            var identity = after ?? before!;
            var change = after is null ? "deleted" : before is null ? "added" : "modified";
            entries.Add(new(identity.AssetId, identity.EditId, change, before?.ContentSha256, after?.ContentSha256, after?.Path, after?.Lifecycle));
        }
        if (entries.Count == 0) throw new VersionControlProblem("no_changes", 400);
        return await commits.PublishAsync(owner, library, client, new(Guid.Empty, request.Branch, request.ExpectedHead, request.Message, DateTimeOffset.UtcNow), idempotencyKey, operationId, cancellationToken, preparedEntries: entries, fingerprintPayload: request);
    }

    private static bool Same(ChangeView? left, ChangeView? right) => left?.ContentSha256 == right?.ContentSha256 && left?.Path == right?.Path && left?.Lifecycle == right?.Lifecycle;
}
