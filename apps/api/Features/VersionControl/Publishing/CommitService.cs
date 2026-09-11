using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Projection;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Publishing;

public sealed class CommitService
{
    private readonly NpgsqlDataSource dataSource;
    private readonly TimeProvider clock;
    private readonly Action<string> checkpoint;

    public CommitService(NpgsqlDataSource dataSource) : this(dataSource, TimeProvider.System, _ => { }) { }
    public CommitService(NpgsqlDataSource dataSource, TimeProvider clock, Action<string> checkpoint)
    {
        this.dataSource = dataSource;
        this.clock = clock;
        this.checkpoint = checkpoint;
    }

    public async Task<VersionControlResult> PublishAsync(string owner, string library, string client, CommitRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken, MergeEvidence? mergeEvidence = null)
    {
        if (!(await new IntegrityService(dataSource, clock).VerifyAsync(owner, library, cancellationToken)).Healthy) throw new VersionControlProblem("graph_corrupt");
        var timer = Stopwatch.StartNew();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return await PublishAttemptAsync(owner, library, client, request, idempotencyKey, operationId, cancellationToken, mergeEvidence);
                }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure) { }
            }
            throw new VersionControlProblem("version_control_retry", 503);
        }
        catch (VersionControlProblem error)
        {
            if (error.Code == "stale_head") VersionControlTelemetry.StaleHeads.Add(1);
            throw;
        }
        finally { VersionControlTelemetry.CommandLatency.Record(timer.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "commit")); }
    }

    private async Task<VersionControlResult> PublishAttemptAsync(string owner, string library, string client, CommitRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken, MergeEvidence? mergeEvidence)
    {
        var branch = RefNames.Normalize(request.Branch);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request, mergeEvidence }))));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ProjectionWriter.LockAsync(connection, owner, library, cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\n" + branch);
        var replay = await ReadReplayAsync(connection, owner, library, idempotencyKey, fingerprint, operationId, cancellationToken);
        if (replay is not null) return replay;
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_ref_quarantine WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3", cancellationToken, owner, library, branch) is not null) throw new VersionControlProblem("graph_corrupt");

        var refState = await ReadRefAsync(connection, owner, library, branch, cancellationToken);
        if (refState is null)
        {
            if (branch != "main" || request.ExpectedHead is not null) throw new VersionControlProblem("ref_not_found", 404);
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,'main','main','branch',NULL,true)", cancellationToken, owner, library);
            refState = (null, 1L);
        }
        if (!string.Equals(refState.Value.Head, request.ExpectedHead, StringComparison.Ordinal))
            throw await StaleAsync(connection, owner, library, client, branch, request, refState.Value.Head, cancellationToken);
        checkpoint("ref-locked");

        var staged = await ReadStagedAsync(connection, owner, library, client, request.StagingSetId, branch, request.ExpectedHead, cancellationToken);
        if (staged.Entries.Any(entry => entry.Change == "conflicted")) throw new VersionControlProblem("unresolved_divergence");
        await VersionControlAdapters.ValidateEntriesAsync(connection, owner, staged.Entries, cancellationToken);
        await ValidateBaseStateAsync(connection, owner, library, refState.Value.Head, staged.Entries, cancellationToken);
        checkpoint("content-validated");
        var parents = new List<string>();
        if (refState.Value.Head is not null) parents.Add(refState.Value.Head);
        if (mergeEvidence is not null && !parents.Contains(mergeEvidence.SourceCommitId, StringComparer.Ordinal)) parents.Add(mergeEvidence.SourceCommitId);
        if (parents.Count > 2) throw new VersionControlProblem("invalid_request", 400);
        foreach (var parent in parents)
        {
            if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND integrity_state='verified'", cancellationToken, parent, owner, library) is null) throw new VersionControlProblem("cross_library_reference", 403);
        }

        var receivedAt = Now;
        var authoredAt = new DateTimeOffset(request.AuthoredAt.Ticks - request.AuthoredAt.Ticks % 10, TimeSpan.Zero);
        var manifest = new VersionControlManifest(library, parents, owner, authoredAt, receivedAt, request.Message, staged.Entries);
        var bytes = VersionControlManifestCodec.Write(manifest);
        var commitId = VersionControlManifestCodec.Digest(bytes);
        await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)", cancellationToken, commitId, owner, library, owner, authoredAt, receivedAt, request.Message, manifest.ContractVersion, manifest.PolicyVersion, bytes);
        for (var ordinal = 0; ordinal < parents.Count; ordinal++) await ExecuteAsync(connection, "INSERT INTO vc_commit_parents(commit_id,ordinal,parent_id) VALUES($1,$2,$3)", cancellationToken, commitId, ordinal, parents[ordinal]);
        for (var ordinal = 0; ordinal < staged.Entries.Length; ordinal++)
        {
            var entry = staged.Entries[ordinal];
            await ExecuteAsync(connection, "INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,base_content_sha256,content_sha256,display_path,lifecycle_state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)", cancellationToken, commitId, ordinal, entry.AssetId, entry.EditId, entry.Change, entry.BaseContentSha256, entry.ContentSha256, entry.Path, entry.Lifecycle);
        }
        checkpoint("commit-inserted");

        var advanced = await ExecuteAsync(connection, "UPDATE vc_refs SET head_commit_id=$4,version=version+1 WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND head_commit_id IS NOT DISTINCT FROM $5 AND version=$6", cancellationToken, owner, library, branch, commitId, request.ExpectedHead, refState.Value.Version);
        if (advanced != 1) throw new VersionControlProblem("stale_head");
        await ProjectionWriter.ApplyAsync(connection, owner, library, branch, commitId, staged.Entries, receivedAt, cancellationToken);
        checkpoint("ref-advanced");
        await ExecuteAsync(connection, "INSERT INTO vc_reflog(id,owner_id,library_id,normalized_name,old_head,new_head,actor_id,operation,reason,occurred_at) VALUES($1,$2,$3,$4,$5,$6,$7,'commit',$8,$9)", cancellationToken, Guid.NewGuid(), owner, library, branch, request.ExpectedHead, commitId, owner, request.Message, receivedAt);
        await VersionControlAdapters.PublishHoldsAsync(connection, commitId, staged.Entries.Select(entry => entry.AssetId), null, cancellationToken);
        if (mergeEvidence is not null)
        {
            await ExecuteAsync(connection, "INSERT INTO vc_divergence(id,owner_id,library_id,normalized_ref,base_commit_id,ours_commit_id,theirs_commit_id,resolved_commit_id,state) VALUES($1,$2,$3,$4,$5,$6,$7,$8,'resolved')", cancellationToken, Guid.NewGuid(), owner, library, branch, mergeEvidence.BaseCommitId, mergeEvidence.TargetCommitId, mergeEvidence.SourceCommitId, commitId);
        }
        checkpoint("holds-published");
        await ExecuteAsync(connection, "DELETE FROM vc_staged_entries WHERE staging_set_id=$2; DELETE FROM vc_working_changes WHERE id=ANY($1); UPDATE vc_staging_sets SET status='published',version=version+1 WHERE id=$2", cancellationToken, staged.WorkingIds, request.StagingSetId);
        var result = new VersionControlResult(operationId, commitId, branch, refState.Value.Version + 1);
        await ExecuteAsync(connection, "INSERT INTO vc_idempotency(owner_id,library_id,idempotency_key,fingerprint,commit_id,result) VALUES($1,$2,$3,$4,$5,$6::jsonb)", cancellationToken, owner, library, idempotencyKey, fingerprint, commitId, JsonSerializer.Serialize(result));
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.commit", request.StagingSetId, "accepted", cancellationToken);
        checkpoint("audit-recorded");
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.Commits.Add(1);
        return result;
    }

    private DateTimeOffset Now
    {
        get
        {
            var value = clock.GetUtcNow();
            return new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
        }
    }

    private static async Task<VersionControlResult?> ReadReplayAsync(NpgsqlConnection connection, string owner, string library, string key, string fingerprint, string operationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT fingerprint,result::text FROM vc_idempotency WHERE owner_id=$1 AND library_id=$2 AND idempotency_key=$3", owner, library, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.GetString(0).Trim() != fingerprint) throw new VersionControlProblem("idempotency_key_reused");
        var stored = JsonSerializer.Deserialize<VersionControlResult>(reader.GetString(1))!;
        return stored with { OperationId = operationId };
    }

    private static async Task<(string? Head, long Version)?> ReadRefAsync(NpgsqlConnection connection, string owner, string library, string branch, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT head_commit_id,version FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch' FOR UPDATE", owner, library, branch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.IsDBNull(0) ? null : reader.GetString(0).Trim(), reader.GetInt64(1));
    }

    private static async Task<(VersionControlEntry[] Entries, Guid[] WorkingIds)> ReadStagedAsync(NpgsqlConnection connection, string owner, string library, string client, Guid setId, string branch, string? expectedHead, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, """
            SELECT w.id,w.asset_id,w.edit_id,w.change_kind,w.base_content_sha256,w.current_content_sha256,w.display_path,w.lifecycle_state
            FROM vc_staging_sets s JOIN vc_staged_entries se ON se.staging_set_id=s.id JOIN vc_working_changes w ON w.id=se.working_change_id AND w.version=se.working_version
            WHERE s.id=$1 AND s.owner_id=$2 AND s.library_id=$3 AND s.client_id=$4 AND s.branch_name=$5 AND s.expected_head IS NOT DISTINCT FROM $6 AND s.status='open'
            ORDER BY se.ordinal FOR UPDATE OF s,w
            """, setId, owner, library, client, branch, expectedHead);
        var entries = new List<VersionControlEntry>();
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
                entries.Add(new(reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4).Trim(), reader.IsDBNull(5) ? null : reader.GetString(5).Trim(), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        if (entries.Count == 0) throw new VersionControlProblem("staging_conflict");
        var expectedCount = Convert.ToInt32(await ScalarAsync(connection, "SELECT count(*) FROM vc_staged_entries e JOIN vc_staging_sets s ON s.id=e.staging_set_id WHERE s.id=$1 AND s.owner_id=$2 AND s.library_id=$3 AND s.client_id=$4 AND s.branch_name=$5 AND s.status='open'", cancellationToken, setId, owner, library, client, branch), System.Globalization.CultureInfo.InvariantCulture);
        if (entries.Count != expectedCount) throw new VersionControlProblem("staging_conflict");
        return (entries.ToArray(), ids.ToArray());
    }

    private static async Task ValidateBaseStateAsync(NpgsqlConnection connection, string owner, string library, string? head, IReadOnlyList<VersionControlEntry> entries, CancellationToken cancellationToken)
    {
        var state = head is null ? new Dictionary<string, ChangeView>(StringComparer.Ordinal) : GraphQueries.State(await QueryService.LoadAsync(connection, owner, library, cancellationToken), head);
        foreach (var entry in entries)
        {
            state.TryGetValue($"{entry.AssetId:D}/{entry.EditId?.ToString("D") ?? "source"}", out var current);
            if (entry.Change == "added")
            {
                if (current is not null || entry.BaseContentSha256 is not null) throw new VersionControlProblem("stale_working_base");
            }
            else if (current is null || !string.Equals(current.ContentSha256, entry.BaseContentSha256, StringComparison.Ordinal)) throw new VersionControlProblem("stale_working_base");
        }
    }

    private static async Task<VersionControlProblem> StaleAsync(NpgsqlConnection connection, string owner, string library, string client, string branch, CommitRequest request, string? currentHead, CancellationToken cancellationToken)
    {
        var staged = await ReadStagedAsync(connection, owner, library, client, request.StagingSetId, branch, request.ExpectedHead, cancellationToken);
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        var current = currentHead is null ? new Dictionary<string, ChangeView>(StringComparer.Ordinal) : GraphQueries.State(graph, currentHead);
        var changes = staged.Entries.Select(entry =>
        {
            current.TryGetValue($"{entry.AssetId:D}/{entry.EditId?.ToString("D") ?? "source"}", out var ours);
            return new
            {
                entry.AssetId,
                entry.EditId,
                BaseContentSha256 = entry.BaseContentSha256,
                OursContentSha256 = ours?.ContentSha256,
                TheirsContentSha256 = entry.ContentSha256,
            };
        }).ToArray();
        return new("stale_head", extensions: new Dictionary<string, object?>
        {
            ["baseCommitId"] = request.ExpectedHead,
            ["oursCommitId"] = currentHead,
            ["theirs"] = changes,
        });
    }
}
