using Npgsql;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Rewrite;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class HistoryRetention
{
    public static HashSet<string> Reachable(IReadOnlyDictionary<string, GraphCommit> graph, IEnumerable<string> heads)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var head in heads) result.UnionWith(RewriteService.Reachable(graph, head));
        return result;
    }

    public static async Task UpdateExpiryAsync(NpgsqlConnection connection, IEnumerable<string> commitIds, DateTimeOffset? expiry, CancellationToken cancellationToken)
    {
        var references = commitIds.Distinct(StringComparer.Ordinal).Select(id => "vc:" + id).ToArray();
        if (references.Length == 0) return;
        await ExecuteAsync(connection, "UPDATE lifecycle_holds SET expires_at=$2 WHERE source_reference=ANY($1)", cancellationToken, references, expiry);
    }

    public static async Task<HashSet<string>> HiddenAsync(NpgsqlConnection connection, IReadOnlyDictionary<string, GraphCommit> graph, string owner, string library, CancellationToken cancellationToken)
    {
        var heads = new List<string>();
        await using var command = Command(connection, "SELECT commit_id FROM vc_visibility WHERE owner_id=$1 AND library_id=$2 AND hidden", owner, library);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) heads.Add(reader.GetString(0).Trim());
        return Reachable(graph, heads);
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
