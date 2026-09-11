using Npgsql;
using System.Data;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Query;

public sealed class QueryService(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<CommitView>> ProjectAsync(string owner, string library, string refName, Guid? asset, bool showHidden, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var head = await ResolveAsync(connection, owner, library, refName, cancellationToken);
        var graph = await LoadAsync(connection, owner, library, cancellationToken);
        return GraphQueries.Project(graph, head, asset, showHidden, offset, limit).Select(ToView).ToArray();
    }

    public async Task<CompareView> CompareAsync(string owner, string library, string ours, string theirs, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var graph = await LoadAsync(connection, owner, library, cancellationToken);
        return GraphQueries.Compare(graph, await ResolveAsync(connection, owner, library, ours, cancellationToken), await ResolveAsync(connection, owner, library, theirs, cancellationToken));
    }

    public async Task<(string Head, Dictionary<string, ChangeView> State)> StateAsync(string owner, string library, string reference, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var head = await ResolveAsync(connection, owner, library, reference, cancellationToken);
        return (head, GraphQueries.State(await LoadAsync(connection, owner, library, cancellationToken), head));
    }

    public async Task<CommitView> GetAsync(string owner, string library, string commitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var graph = await LoadAsync(connection, owner, library, cancellationToken);
        return graph.TryGetValue(commitId, out var commit) ? ToView(commit) : throw new VersionControlProblem("commit_not_found", 404);
    }

    public static async Task<IReadOnlyDictionary<string, GraphCommit>> LoadAsync(NpgsqlConnection connection, string owner, string library, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        await using var command = Command(connection, """
            SELECT c.id,c.manifest_bytes,COALESCE(v.hidden,false)
            FROM vc_commits c LEFT JOIN vc_visibility v ON v.commit_id=c.id AND v.owner_id=c.owner_id AND v.library_id=c.library_id
            WHERE c.owner_id=$1 AND c.library_id=$2
            """, owner, library);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0).Trim();
                try
                {
                    var manifest = VersionControlManifestCodec.Read((byte[])reader[1]);
                    if (VersionControlManifestCodec.Digest((byte[])reader[1]) != id || !VersionControlManifestCodec.Write(manifest).AsSpan().SequenceEqual((byte[])reader[1]) || manifest.LibraryId != library) throw new VersionControlProblem("graph_corrupt");
                    result[id] = new(id, manifest.Parents, manifest.AuthorId, manifest.AuthoredAt, manifest.ReceivedAt, manifest.Message, manifest.Entries.Select(entry => new ChangeView(entry.AssetId, entry.EditId, entry.Change, entry.BaseContentSha256, entry.ContentSha256, entry.Path, entry.Lifecycle)).ToArray(), reader.GetBoolean(2));
                }
                catch (VersionControlProblem) { throw; }
                catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException) { throw new VersionControlProblem("graph_corrupt"); }
            }
        }
        return result;
    }

    internal static async Task<string> ResolveAsync(NpgsqlConnection connection, string owner, string library, string value, CancellationToken cancellationToken)
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
