using System.Data;
using Npgsql;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.Rendering;
using Platform.Contracts.Lineage;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Lineage;

public sealed class LineageService(NpgsqlDataSource dataSource, RenderingService rendering, S3ObjectStore storage)
{
    public async Task<LineagePageView> HistoryAsync(string owner, string library, Guid asset, string branchValue,
        bool showHidden, int offset, int limit, CancellationToken cancellationToken)
    {
        if (asset == Guid.Empty || offset < 0 || limit is < 1 or > 100) throw new VersionControlProblem("invalid_page", 400);
        var branch = RefNames.Normalize(branchValue);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (await ScalarAsync(connection, "SELECT 1 FROM assets WHERE id=$1 AND owner_id=$2 AND state IN ('active','restored','recoverable-deleted')", cancellationToken, asset, owner) is null)
            throw new VersionControlProblem("asset_not_found", 404);
        var head = await QueryService.ResolveAsync(connection, owner, library, branch, cancellationToken);
        var commits = new List<CommitView>();
        await using (var command = Command(connection, """
            WITH image AS MATERIALIZED (SELECT DISTINCT commit_id FROM vc_commit_entries WHERE asset_id=$4),
            eligible AS MATERIALIZED (
                SELECT image.commit_id, reachable.received_at FROM image
                JOIN LATERAL (
                    SELECT received_at FROM vc_lineage_reachable lineage
                    WHERE lineage.owner_id=$1 AND lineage.library_id=$2 AND lineage.normalized_ref=$3
                      AND lineage.commit_id=image.commit_id
                    OFFSET 0
                ) reachable ON true
            ), page AS MATERIALIZED (
                SELECT eligible.commit_id,eligible.received_at,COALESCE(visibility.hidden,false) AS hidden
                FROM eligible
                LEFT JOIN vc_visibility visibility ON visibility.commit_id=eligible.commit_id
                    AND visibility.owner_id=$1 AND visibility.library_id=$2
                WHERE $5 OR NOT COALESCE(visibility.hidden,false)
                ORDER BY eligible.received_at DESC,eligible.commit_id
                OFFSET $6 LIMIT $7
            )
            SELECT commit.id,commit.manifest_bytes,page.hidden
            FROM page JOIN vc_commits commit ON commit.id=page.commit_id
                AND commit.owner_id=$1 AND commit.library_id=$2
            ORDER BY page.received_at DESC,page.commit_id
            """, owner, library, branch, asset, showHidden, offset, limit + 1))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) commits.Add(ReadCommit(reader, library, asset));

        var commitIds = commits.Select(commit => commit.CommitId).ToArray();
        var refs = new List<RefView>();
        if (commitIds.Length > 0)
        {
            await using var command = Command(connection, "SELECT normalized_name,kind,head_commit_id,protected,version FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND head_commit_id::text=ANY($3)", owner, library, commitIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) refs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetBoolean(3), reader.GetInt64(4)));
        }
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var parentIds = commits.SelectMany(commit => commit.Parents).Distinct(StringComparer.Ordinal).ToArray();
        if (!showHidden && parentIds.Length > 0)
        {
            await using var command = Command(connection,
                "SELECT commit_id FROM vc_visibility WHERE owner_id=$1 AND library_id=$2 AND hidden=true AND commit_id::text=ANY($3)",
                owner, library, parentIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) hidden.Add(reader.GetString(0).Trim());
        }
        var hasHiddenHistory = !showHidden && await ScalarAsync(connection, """
            WITH image AS MATERIALIZED (SELECT DISTINCT commit_id FROM vc_commit_entries WHERE asset_id=$4)
            SELECT 1 FROM image
            JOIN LATERAL (
                SELECT 1 FROM vc_visibility visibility
                WHERE visibility.owner_id=$1 AND visibility.library_id=$2
                  AND visibility.commit_id=image.commit_id AND visibility.hidden=true
                OFFSET 0
            ) hidden ON true
            JOIN LATERAL (
                SELECT 1 FROM vc_lineage_reachable reachable
                WHERE reachable.owner_id=$1 AND reachable.library_id=$2
                  AND reachable.normalized_ref=$3 AND reachable.commit_id=image.commit_id
                OFFSET 0
            ) branch_member ON true
            LIMIT 1
            """, cancellationToken, owner, library, branch, asset) is not null;
        var page = LineageProjection.Build(asset, branch, head, showHidden, offset, limit, commits, refs, hidden, hasHiddenHistory);
        LineageTelemetry.GraphSize.Record(page.Nodes.Count);
        await transaction.CommitAsync(cancellationToken);
        return page;
    }

    public async Task<LineageComparisonView> CompareAsync(string owner, string library, Guid asset, string targetValue,
        string sourceValue, CancellationToken cancellationToken)
    {
        if (asset == Guid.Empty || !IsCommitId(targetValue) || !IsCommitId(sourceValue))
            throw new VersionControlProblem("invalid_version", 400);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (await ScalarAsync(connection, "SELECT 1 FROM assets WHERE id=$1 AND owner_id=$2 AND state IN ('active','restored','recoverable-deleted')", cancellationToken, asset, owner) is null)
            throw new VersionControlProblem("asset_not_found", 404);
        var target = await QueryService.ResolveAsync(connection, owner, library, targetValue, cancellationToken);
        var source = await QueryService.ResolveAsync(connection, owner, library, sourceValue, cancellationToken);
        var graph = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        await using (var command = Command(connection, """
            WITH RECURSIVE ancestors(id) AS (
                SELECT root.id FROM (VALUES ($3::char(64)),($4::char(64))) AS root(id)
                UNION
                SELECT parent.parent_id FROM ancestors a JOIN vc_commit_parents parent ON parent.commit_id=a.id
            )
            SELECT commit.id,commit.manifest_bytes,COALESCE(visibility.hidden,false)
            FROM ancestors a JOIN vc_commits commit ON commit.id=a.id AND commit.owner_id=$1 AND commit.library_id=$2
            LEFT JOIN vc_visibility visibility ON visibility.commit_id=commit.id AND visibility.owner_id=$1 AND visibility.library_id=$2
            """, owner, library, target, source))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var commit = ReadCommit(reader, library, asset);
                graph.Add(commit.CommitId, new(commit.CommitId, commit.Parents, commit.AuthorId, commit.AuthoredAt,
                    commit.ReceivedAt, commit.Message, commit.Changes, commit.Hidden));
            }
        var result = LineageComparison.Build(graph, asset, target, source);
        var metadata = new List<ComparedXmpView>();
        foreach (var difference in result.Differences)
        {
            if (difference.OursContentSha256 is null || difference.TheirsContentSha256 is null)
            {
                metadata.Add(new(difference.EditId, [], "absent_image_state"));
                continue;
            }
            try
            {
                var targetBytes = await VerifiedXmpReader.ReadAsync(connection, storage, difference.OursContentSha256, cancellationToken);
                var sourceBytes = await VerifiedXmpReader.ReadAsync(connection, storage, difference.TheirsContentSha256, cancellationToken);
                var assessment = difference.BaseContentSha256 is { } baseSha
                    ? XmpMergePolicy.Assess(await VerifiedXmpReader.ReadAsync(connection, storage, baseSha, cancellationToken), targetBytes, sourceBytes)
                    : XmpMergePolicy.Compare(targetBytes, sourceBytes);
                metadata.Add(new(difference.EditId,
                    assessment.Fields.Select(field => new XmpFieldView(field.Field, field.Base, field.Target, field.Source, field.RequiresResolution)).ToArray(),
                    assessment.CanCompose ? difference.BaseContentSha256 is null ? "no_common_ancestor" : "available" : assessment.UnavailableReason ?? "unavailable"));
            }
            catch (VersionControlProblem problem) when (problem.Code == "content_not_authoritative")
            {
                metadata.Add(new(difference.EditId, [], "content_unavailable"));
            }
        }
        LineageTelemetry.Comparisons.Add(1);
        await transaction.CommitAsync(cancellationToken);
        return result with
        {
            MetadataDifferences = metadata,
            TargetStates = await PreviewStatesAsync(owner, library, result.TargetStates, cancellationToken),
            SourceStates = await PreviewStatesAsync(owner, library, result.SourceStates, cancellationToken)
        };
    }

    internal async Task<IReadOnlyList<ImageVersionView>> PreviewStatesAsync(string owner, string library,
        IReadOnlyList<ImageVersionView> states, CancellationToken cancellationToken)
    {
        var result = new List<ImageVersionView>(states.Count);
        foreach (var state in states)
        {
            if (state.EditId is null || state.XmpSha256 is null) { result.Add(state); continue; }
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                var requestId = await ScalarAsync(connection, """
                    SELECT id FROM render_requests
                    WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id=$4 AND selected_version=$5
                      AND xmp_sha256=$6 AND rendition='preview-2048' AND profile='render-v1'
                    ORDER BY updated_at DESC LIMIT 1
                    """, cancellationToken, owner, library, state.AssetId, state.EditId.Value, state.CommitId, state.XmpSha256) as Guid?;
                if (requestId is null) { result.Add(state); continue; }
                var status = await rendering.StatusAsync(owner, library, requestId.Value, cancellationToken);
                result.Add(LineageComparison.WithPreviewStatus(state, status));
            }
            catch (Exception error) when (error is NpgsqlException or RenderingProblem)
            {
                result.Add(state with { PreviewState = "unavailable" });
            }
        }
        return result;
    }

    private static bool IsCommitId(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CommitView ReadCommit(NpgsqlDataReader reader, string library, Guid asset)
    {
        var id = reader.GetString(0).Trim();
        var bytes = (byte[])reader[1];
        try
        {
            var manifest = VersionControlManifestCodec.Read(bytes);
            if (VersionControlManifestCodec.Digest(bytes) != id || !VersionControlManifestCodec.Write(manifest).AsSpan().SequenceEqual(bytes) || manifest.LibraryId != library)
                throw new VersionControlProblem("graph_corrupt");
            return new(id, manifest.Parents, manifest.AuthorId, manifest.AuthoredAt, manifest.ReceivedAt, manifest.Message,
                manifest.Entries.Where(entry => entry.AssetId == asset)
                    .Select(entry => new ChangeView(entry.AssetId, entry.EditId, entry.Change, entry.BaseContentSha256, entry.ContentSha256, entry.Path, entry.Lifecycle)).ToArray(), reader.GetBoolean(2));
        }
        catch (VersionControlProblem) { throw; }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { throw new VersionControlProblem("graph_corrupt"); }
    }
}
