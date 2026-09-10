using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.VersionControl.Projection;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Rewrite;

public sealed class RewriteService(NpgsqlDataSource dataSource, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<RewritePreview> PreviewAsync(string owner, string library, string branchValue, string newHead, CancellationToken cancellationToken)
    {
        var branch = RefNames.Normalize(branchValue);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var current = await ReadRefAsync(connection, owner, library, branch, false, cancellationToken) ?? throw new VersionControlProblem("ref_not_found", 404);
        if (current.Protected) throw new VersionControlProblem("protected_ref");
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND integrity_state='verified'", cancellationToken, newHead, owner, library) is null) throw new VersionControlProblem("cross_library_reference", 403);
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        var displaced = current.Head is null ? [] : Reachable(graph, current.Head).Except(Reachable(graph, newHead)).Order(StringComparer.Ordinal).ToArray();
        return new(branch, current.Head, newHead, displaced, Token(owner, library, branch, current.Head, newHead));
    }

    public async Task<VersionControlResult> ApplyAsync(string owner, string library, RewriteRequest request, string operationId, CancellationToken cancellationToken)
    {
        if (!request.Confirm) throw new VersionControlProblem("confirmation_required");
        var branch = RefNames.Normalize(request.Branch);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadRefAsync(connection, owner, library, branch, true, cancellationToken) ?? throw new VersionControlProblem("ref_not_found", 404);
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_ref_quarantine WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3", cancellationToken, owner, library, branch) is not null) throw new VersionControlProblem("graph_corrupt");
        if (current.Protected)
        {
            await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.rewrite.rejected", RefService.StableResource(branch), "denied", cancellationToken, "protected_ref");
            await transaction.CommitAsync(cancellationToken);
            throw new VersionControlProblem("protected_ref");
        }
        if (request.PreviewToken != Token(owner, library, branch, current.Head, request.NewHead)) throw new VersionControlProblem("rewrite_preview_stale");
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND integrity_state='verified'", cancellationToken, request.NewHead, owner, library) is null) throw new VersionControlProblem("cross_library_reference", 403);
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        var retained = HistoryRetention.Reachable(graph, await HistoryRetention.RefHeadsAsync(connection, owner, library, branch, cancellationToken));
        retained.UnionWith(Reachable(graph, request.NewHead));
        retained.UnionWith(await HistoryRetention.HiddenAsync(connection, graph, owner, library, cancellationToken));
        var displaced = current.Head is null ? [] : Reachable(graph, current.Head).Except(retained).ToArray();
        await ExecuteAsync(connection, "UPDATE vc_refs SET head_commit_id=$4,version=version+1 WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", cancellationToken, owner, library, branch, request.NewHead);
        await ProjectionWriter.ReplaceAsync(connection, owner, library, branch, request.NewHead, graph, Now, cancellationToken);
        var reflogId = Guid.NewGuid();
        var expiry = Now.AddDays(90);
        await ExecuteAsync(connection, "INSERT INTO vc_reflog(id,owner_id,library_id,normalized_name,old_head,new_head,actor_id,operation,reason,occurred_at,recovery_expires_at) VALUES($1,$2,$3,$4,$5,$6,$2,'rewrite','confirmed rewrite',$7,$8)", cancellationToken, reflogId, owner, library, branch, current.Head, request.NewHead, Now, expiry);
        if (current.Head is not null)
        {
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,$3,$3,'recovery',$4,true)", cancellationToken, owner, library, $"recovery/{reflogId:N}", current.Head);
            await HistoryRetention.UpdateExpiryAsync(connection, displaced, expiry, cancellationToken);
        }
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.rewrite", RefService.StableResource(branch), "accepted", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.RefMovements.Add(1, new KeyValuePair<string, object?>("operation", "rewrite"));
        return new(operationId, request.NewHead, branch, current.Version + 1);
    }

    public async Task<RefView> RecoverAsync(string owner, string library, Guid reflogId, string branchName, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var oldHead = (await ScalarAsync(connection, "SELECT old_head FROM vc_reflog WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND recovery_expires_at >= $4", cancellationToken, reflogId, owner, library, Now) as string)?.Trim() ?? throw new VersionControlProblem("recovery_expired", 404);
        return await new RefService(dataSource, clock).CreateAsync(owner, library, "branch", new(branchName, oldHead), operationId, cancellationToken);
    }

    public async Task<int> ExpireAsync(string owner, string library, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var expired = new List<(Guid Id, string? OldHead)>();
        await using (var command = Command(connection, "SELECT r.id,r.old_head FROM vc_reflog r LEFT JOIN vc_recovery_expirations x ON x.reflog_id=r.id WHERE r.owner_id=$1 AND r.library_id=$2 AND r.recovery_expires_at < $3 AND x.reflog_id IS NULL FOR UPDATE OF r", owner, library, Now))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) expired.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1).Trim()));
        foreach (var item in expired)
        {
            await ExecuteAsync(connection, "DELETE FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='recovery'", cancellationToken, owner, library, $"recovery/{item.Id:N}");
            if (item.OldHead is not null && await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND head_commit_id=$3 LIMIT 1", cancellationToken, owner, library, item.OldHead) is null && await ScalarAsync(connection, "SELECT 1 FROM vc_visibility WHERE owner_id=$1 AND library_id=$2 AND commit_id=$3 AND hidden LIMIT 1", cancellationToken, owner, library, item.OldHead) is null)
                await ExecuteAsync(connection, "UPDATE lifecycle_holds SET expires_at=$2 WHERE source_reference=$1", cancellationToken, $"vc:{item.OldHead}", Now);
            await ExecuteAsync(connection, "INSERT INTO vc_recovery_expirations(reflog_id,expired_at) VALUES($1,$2)", cancellationToken, item.Id, Now);
        }
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.RecoveryExpirations.Add(expired.Count);
        return expired.Count;
    }

    private static async Task<(string? Head, bool Protected, long Version)?> ReadRefAsync(NpgsqlConnection connection, string owner, string library, string branch, bool update, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, $"SELECT head_commit_id,protected,version FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'{(update ? " FOR UPDATE" : string.Empty)}", owner, library, branch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.IsDBNull(0) ? null : reader.GetString(0).Trim(), reader.GetBoolean(1), reader.GetInt64(2));
    }

    public static HashSet<string> Reachable(IReadOnlyDictionary<string, GraphCommit> graph, string head)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(head);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!result.Add(id)) continue;
            if (!graph.TryGetValue(id, out var commit)) throw new VersionControlProblem("graph_corrupt");
            foreach (var parent in commit.Parents) pending.Push(parent);
        }
        return result;
    }

    private static string Token(string owner, string library, string branch, string? oldHead, string newHead) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{library}\n{branch}\n{oldHead}\n{newHead}")));
}
