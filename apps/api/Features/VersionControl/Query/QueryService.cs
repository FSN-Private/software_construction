using Npgsql;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Query;

public sealed class QueryService(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<CommitView>> ProjectAsync(string owner, string library, string refName, Guid? asset, bool showHidden, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var head = (await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", cancellationToken, owner, library, RefNames.Normalize(refName)) as string)?.Trim() ?? throw new VersionControlProblem("ref_not_found", 404);
        var graph = await LoadAsync(connection, owner, library, cancellationToken);
        return GraphQueries.Project(graph, head, asset, showHidden, offset, limit).Select(ToView).ToArray();
    }

    public async Task<CompareView> CompareAsync(string owner, string library, string ours, string theirs, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var graph = await LoadAsync(connection, owner, library, cancellationToken);
        return GraphQueries.Compare(graph, await ResolveAsync(connection, owner, library, ours, cancellationToken), await ResolveAsync(connection, owner, library, theirs, cancellationToken));
    }

    public async Task<(string Head, Dictionary<string, ChangeView> State)> StateAsync(string owner, string library, string reference, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var head = await ResolveAsync(connection, owner, library, reference, cancellationToken);
        return (head, GraphQueries.State(await LoadAsync(connection, owner, library, cancellationToken), head));
    }

    public static async Task<IReadOnlyDictionary<string, GraphCommit>> LoadAsync(NpgsqlConnection connection, string owner, string library, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT c.id,c.author_id,c.authored_at,c.received_at,c.message,COALESCE(v.hidden,false),
                   COALESCE(array_agg(p.parent_id ORDER BY p.ordinal) FILTER(WHERE p.parent_id IS NOT NULL),ARRAY[]::bpchar[])
            FROM vc_commits c LEFT JOIN vc_commit_parents p ON p.commit_id=c.id LEFT JOIN vc_visibility v ON v.commit_id=c.id AND v.owner_id=c.owner_id AND v.library_id=c.library_id
            WHERE c.owner_id=$1 AND c.library_id=$2 GROUP BY c.id,c.author_id,c.authored_at,c.received_at,c.message,v.hidden
            """, owner, library);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0).Trim();
                result[id] = new(id, reader.GetFieldValue<string[]>(6).Select(value => value.Trim()).ToArray(), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), [], reader.GetBoolean(5));
            }
        }
        await using (var entriesCommand = Command(connection, """
            SELECT e.commit_id,e.asset_id,e.edit_id,e.change_kind,e.base_content_sha256,e.content_sha256,e.display_path,e.lifecycle_state
            FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
            WHERE c.owner_id=$1 AND c.library_id=$2 ORDER BY e.commit_id,e.ordinal
            """, owner, library))
        await using (var reader = await entriesCommand.ExecuteReaderAsync(cancellationToken))
        {
            var entries = new Dictionary<string, List<ChangeView>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0).Trim();
                if (!entries.TryGetValue(id, out var commitEntries)) entries[id] = commitEntries = [];
                commitEntries.Add(new(reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4).Trim(), reader.IsDBNull(5) ? null : reader.GetString(5).Trim(), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            foreach (var (id, commitEntries) in entries) result[id] = result[id] with { Entries = commitEntries };
        }
        return result;
    }

    private static async Task<string> ResolveAsync(NpgsqlConnection connection, string owner, string library, string value, CancellationToken cancellationToken)
    {
        if (value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3", cancellationToken, value, owner, library) is not null) return value;
        }
        var head = await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", cancellationToken, owner, library, RefNames.Normalize(value)) as string;
        return head?.Trim() ?? throw new VersionControlProblem("commit_not_found", 404);
    }

    private static CommitView ToView(GraphCommit commit) => new(commit.Id, commit.Parents, commit.Author, commit.AuthoredAt, commit.ReceivedAt, commit.Message, commit.Entries, commit.Hidden);
}
