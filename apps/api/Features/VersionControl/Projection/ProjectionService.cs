using Npgsql;
using System.Data;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Features.VersionControl.Query;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.VersionControl.Refs;
using System.Text.Json;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Projection;

public sealed record VersionControlInventory(long UniqueXmpBytes, long CommitRows, long EntryRows, long ProjectionRows, long AuditRows, long RecoveryRows);

public sealed class ProjectionService
{
    private readonly NpgsqlDataSource dataSource;
    private readonly IntegrityService integrity;
    private readonly TimeProvider clock;
    private readonly Func<string, CancellationToken, Task> checkpoint;

    public ProjectionService(NpgsqlDataSource dataSource, IntegrityService integrity) : this(dataSource, integrity, TimeProvider.System, (_, _) => Task.CompletedTask) { }
    public ProjectionService(NpgsqlDataSource dataSource, IntegrityService integrity, TimeProvider clock, Func<string, CancellationToken, Task> checkpoint)
    {
        this.dataSource = dataSource;
        this.integrity = integrity;
        this.clock = clock;
        this.checkpoint = checkpoint;
    }

    public async Task<int> RebuildAsync(string owner, string library, CancellationToken cancellationToken)
    {
        var verified = await integrity.VerifyAsync(owner, library, cancellationToken);
        if (!verified.Healthy) throw new VersionControlProblem("graph_corrupt");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ProjectionWriter.LockAsync(connection, owner, library, cancellationToken);
        var refs = new List<(string Name, string Head)>();
        await using (var command = Command(connection, "SELECT normalized_name,head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND head_commit_id IS NOT NULL FOR UPDATE", owner, library))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) refs.Add((reader.GetString(0), reader.GetString(1).Trim()));
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        await checkpoint("snapshot-captured", cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2", cancellationToken, owner, library);
        var rows = 0;
        foreach (var reference in refs)
            rows += await ProjectionWriter.ReplaceAsync(connection, owner, library, reference.Name, reference.Head, graph, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.ProjectionRebuilds.Add(1);
        return rows;
    }

    public async Task<VersionControlInventory> InventoryAsync(string owner, string library, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        async Task<long> Count(string sql) => Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken, owner, library), System.Globalization.CultureInfo.InvariantCulture);
        return new(
            await Count("SELECT COALESCE(sum(byte_length),0) FROM content_objects WHERE sha256 IN (SELECT content_sha256 FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id WHERE c.owner_id=$1 AND c.library_id=$2)"),
            await Count("SELECT count(*) FROM vc_commits WHERE owner_id=$1 AND library_id=$2"),
            await Count("SELECT count(*) FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id WHERE c.owner_id=$1 AND c.library_id=$2"),
            await Count("SELECT count(*) FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2"),
            await Count("SELECT count(*) FROM audit_events WHERE owner_id=$1 AND event_type LIKE 'version-control.%'"),
            await Count("SELECT count(*) FROM vc_reflog WHERE owner_id=$1 AND library_id=$2 AND recovery_expires_at IS NOT NULL"));
    }

    public async Task<IReadOnlyList<ChangeView>> AssetAsync(string owner, string library, string reference, Guid asset, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, "SELECT asset_id,edit_id,content_sha256,display_path,lifecycle_state FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3 AND asset_id=$4 ORDER BY edit_id NULLS FIRST", owner, library, RefNames.Normalize(reference), asset);
        var result = new List<ChangeView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), "clean", null, reader.IsDBNull(2) ? null : reader.GetString(2).Trim(), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }
}

internal static class ProjectionWriter
{
    public static Task LockAsync(NpgsqlConnection connection, string owner, string library, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\nprojection");

    public static async Task ApplyAsync(NpgsqlConnection connection, string owner, string library, string reference, string head, IReadOnlyList<VersionControlEntry> entries, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;
        var rows = JsonSerializer.Serialize(entries.Select(entry => new
        {
            asset_id = entry.AssetId,
            edit_id = entry.EditId,
            change_kind = entry.Change,
            content_sha256 = entry.ContentSha256,
            display_path = entry.Path,
            lifecycle_state = entry.Lifecycle,
        }));
        await ExecuteAsync(connection, """
            DELETE FROM vc_materialized_state s USING jsonb_to_recordset($6::jsonb) AS e(asset_id uuid,edit_id uuid,change_kind text,content_sha256 char(64),display_path text,lifecycle_state text)
            WHERE e.change_kind='deleted' AND s.owner_id=$1 AND s.library_id=$2 AND s.normalized_ref=$3 AND s.asset_id=e.asset_id AND s.edit_id IS NOT DISTINCT FROM e.edit_id;
            INSERT INTO vc_materialized_state(owner_id,library_id,normalized_ref,source_commit_id,asset_id,edit_id,content_sha256,display_path,lifecycle_state,rebuilt_at)
            SELECT $1,$2,$3,$4,e.asset_id,e.edit_id,e.content_sha256,e.display_path,e.lifecycle_state,$5
            FROM jsonb_to_recordset($6::jsonb) AS e(asset_id uuid,edit_id uuid,change_kind text,content_sha256 char(64),display_path text,lifecycle_state text)
            WHERE e.change_kind<>'deleted'
            ON CONFLICT(owner_id,library_id,normalized_ref,asset_id,edit_id)
            DO UPDATE SET source_commit_id=EXCLUDED.source_commit_id,content_sha256=EXCLUDED.content_sha256,display_path=EXCLUDED.display_path,lifecycle_state=EXCLUDED.lifecycle_state,rebuilt_at=EXCLUDED.rebuilt_at
            """, cancellationToken, owner, library, reference, head, now, rows);
    }

    public static async Task<int> ReplaceAsync(NpgsqlConnection connection, string owner, string library, string reference, string head, IReadOnlyDictionary<string, GraphCommit> graph, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "DELETE FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3", cancellationToken, owner, library, reference);
        var state = GraphQueries.State(graph, head);
        await ApplyAsync(connection, owner, library, reference, head, state.Values.Select(entry => new VersionControlEntry(entry.AssetId, entry.EditId, entry.Change, entry.BaseContentSha256, entry.ContentSha256, entry.Path, entry.Lifecycle)).ToArray(), now, cancellationToken);
        return state.Count;
    }
}
