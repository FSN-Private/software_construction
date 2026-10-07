using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Rewrite;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class HistoryRetention
{
    public static Task LockAsync(NpgsqlConnection connection, string owner, string library, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\nretention");

    public static HashSet<string> Reachable(IReadOnlyDictionary<string, GraphCommit> graph, IEnumerable<string> heads)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var head in heads) result.UnionWith(RewriteService.Reachable(graph, head));
        return result;
    }

    public static async Task UpdateExpiryAsync(NpgsqlConnection connection, IEnumerable<string> commitIds, DateTimeOffset? expiry, CancellationToken cancellationToken)
    {
        var ids = commitIds.Distinct(StringComparer.Ordinal).ToArray();
        var references = ids.Select(id => "vc:" + id).ToArray();
        if (references.Length == 0) return;
        await EnsureNoFrozenAssetHoldsAsync(connection, ids, cancellationToken);
        await ExecuteAsync(connection, "UPDATE lifecycle_holds SET expires_at=$2 WHERE source_reference=ANY($1)", cancellationToken, (object)references, expiry);
    }

    public static async Task<HashSet<string>> HiddenAsync(NpgsqlConnection connection, IReadOnlyDictionary<string, GraphCommit> graph, string owner, string library, CancellationToken cancellationToken)
    {
        var heads = new List<string>();
        await using var command = Command(connection, "SELECT commit_id FROM vc_visibility WHERE owner_id=$1 AND library_id=$2 AND hidden", owner, library);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) heads.Add(reader.GetString(0).Trim());
        return Reachable(graph, heads);
    }

    public static async Task ReconcileAsync(NpgsqlConnection connection, IReadOnlyDictionary<string, GraphCommit> graph, string owner, string library, IEnumerable<string> affected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var affectedCommits = affected.Distinct(StringComparer.Ordinal).ToArray();
        await EnsureNoFrozenAssetHoldsAsync(connection, affectedCommits, cancellationToken);
        var indefinite = Reachable(graph, await RefHeadsByKindAsync(connection, owner, library, cancellationToken));
        indefinite.UnionWith(await HiddenAsync(connection, graph, owner, library, cancellationToken));
        var recoverableUntil = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using (var command = Command(connection, """
            SELECT r.old_head,r.recovery_expires_at FROM vc_reflog r
            LEFT JOIN vc_recovery_expirations x ON x.reflog_id=r.id
            WHERE r.owner_id=$1 AND r.library_id=$2 AND r.old_head IS NOT NULL
              AND r.recovery_expires_at >= $3 AND x.reflog_id IS NULL
            """, owner, library, now))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var expiry = reader.GetFieldValue<DateTimeOffset>(1);
                foreach (var id in RewriteService.Reachable(graph, reader.GetString(0).Trim()))
                    if (!recoverableUntil.TryGetValue(id, out var existing) || expiry > existing) recoverableUntil[id] = expiry;
            }
        }
        foreach (var id in affectedCommits)
        {
            DateTimeOffset? expiry = indefinite.Contains(id) ? null : recoverableUntil.GetValueOrDefault(id, now);
            await ExecuteAsync(connection, "UPDATE lifecycle_holds SET expires_at=$2 WHERE source_reference=$1", cancellationToken, "vc:" + id, expiry);
        }
    }

    private static async Task<string[]> RefHeadsByKindAsync(NpgsqlConnection connection, string owner, string library, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = Command(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND kind<>'recovery' AND head_commit_id IS NOT NULL", owner, library);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0).Trim());
        return result.ToArray();
    }

    private static async Task EnsureNoFrozenAssetHoldsAsync(NpgsqlConnection connection,
        IReadOnlyList<string> commitIds, CancellationToken cancellationToken)
    {
        if (commitIds.Count == 0) return;
        var digests = new List<string>();
        await using (var command = Command(connection, """
            SELECT DISTINCT digest FROM (
                SELECT base_content_sha256 AS digest FROM vc_commit_entries WHERE commit_id=ANY($1)
                UNION ALL
                SELECT content_sha256 AS digest FROM vc_commit_entries WHERE commit_id=ANY($1)
            ) affected WHERE digest IS NOT NULL ORDER BY digest
            """, (object)commitIds.ToArray()))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) digests.Add(reader.GetString(0).Trim());
        if (await Platform.Storage.IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
                connection, digests, cancellationToken))
            throw new VersionControlProblem("recovery_purge_frozen", 409);

        var assetIds = new List<Guid>();
        await using (var command = Command(connection,
            "SELECT DISTINCT asset_id FROM vc_commit_entries WHERE commit_id=ANY($1) ORDER BY asset_id", (object)commitIds.ToArray()))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) assetIds.Add(reader.GetGuid(0));
        if (assetIds.Count == 0) return;
        await using (var lockAssets = Command(connection,
            "SELECT id FROM assets WHERE id=ANY($1) ORDER BY id FOR UPDATE", assetIds.ToArray()))
        await using (var reader = await lockAssets.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) { }
        if (await ScalarAsync(connection, """
            SELECT 1 FROM recovery_purge_proposals
             WHERE phase IN ('executing','primary_cleanup')
               AND (asset_id=ANY($1) OR content_sha256=ANY($2::char(64)[])) LIMIT 1
            """, cancellationToken, (object)assetIds.ToArray(), (object)digests.ToArray()) is not null)
            throw new VersionControlProblem("recovery_purge_frozen", 409);
    }

    public static async Task<string[]> RefHeadsAsync(NpgsqlConnection connection, string owner, string library, string? exceptName, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = Command(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND head_commit_id IS NOT NULL AND ($3::text IS NULL OR normalized_name<>$3)", owner, library, exceptName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0).Trim());
        return result.ToArray();
    }
}
