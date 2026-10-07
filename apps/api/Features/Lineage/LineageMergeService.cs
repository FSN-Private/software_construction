using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Npgsql;
using Platform.Api.Features.Gallery.Metadata;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.Lineage;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Lineage;

public sealed class LineageMergeService(NpgsqlDataSource dataSource, S3ObjectStore storage, CommitService commits, LineageService lineage)
{
    private sealed record PendingXmp(Guid AssetId, Guid EditId, string Sha256, byte[] Bytes,
        string? BaseSha256, string TargetSha256, string SourceSha256);
    private sealed record Evaluated(MergeProposalView Proposal, MergeReviewView Review,
        IReadOnlyList<VersionControlEntry> Entries, IReadOnlyList<PendingXmp> Generated);

    public async Task<MergeReviewView> ReviewAsync(string owner, string library, MergeReviewRequest request, CancellationToken ct) =>
        (await EvaluateAsync(owner, library, request, ct)).Review;

    public async Task<VersionControlResult> ConfirmAsync(string owner, string library, string client, MergeConfirmRequest request,
        string idempotencyKey, string operationId, CancellationToken ct)
    {
        if (!request.Confirm || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4096 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200 || request.Reviewed is null)
            throw new VersionControlProblem("invalid_request", 400);
        var replay = await commits.TryReplayAsync(owner, library, idempotencyKey, request, operationId, ct);
        if (replay is not null) return replay;
        var evaluated = await EvaluateAsync(owner, library, request.Reviewed, ct);
        if (evaluated.Review.ReviewDigest != request.ReviewDigest) throw new VersionControlProblem("stale_review");
        if (evaluated.Entries.Count == 0) throw new VersionControlProblem("no_changes", 400);
        var generated = new List<GeneratedXmpContent>();
        foreach (var item in evaluated.Generated)
        {
            string version;
            try
            {
                await using var stream = new MemoryStream(item.Bytes, writable: false);
                version = await storage.PublishVerifiedAsync(new(item.Sha256, item.Bytes.Length), stream, ct);
            }
            catch (Exception error) when (error is AmazonS3Exception or InvalidDataException or ArgumentException)
            { throw new VersionControlProblem("content_not_authoritative"); }
            generated.Add(new(item.AssetId, item.EditId, item.Sha256, item.Bytes.Length, version,
                item.BaseSha256, item.TargetSha256, item.SourceSha256, XmpMergePolicy.Version));
        }
        var proposal = evaluated.Proposal;
        var evidence = new MergeEvidence(proposal.BaseCommitId, proposal.SourceHead, proposal.TargetHead, [], proposal.SourceBranch);
        var commit = new CommitRequest(Guid.Empty, proposal.TargetBranch, proposal.TargetHead, request.Message, request.AuthoredAt);
        var result = await commits.PublishAsync(owner, library, client, commit, idempotencyKey, operationId, ct,
            evidence, evaluated.Entries, request, generated);
        LineageTelemetry.MergeConfirms.Add(1);
        return result;
    }

    private async Task<Evaluated> EvaluateAsync(string owner, string library, MergeReviewRequest request, CancellationToken ct)
    {
        if (request.Resolutions is null) throw new VersionControlProblem("invalid_request", 400);
        var proposal = await PrepareAsync(owner, library, request.Prepared, ct);
        if (proposal.ProposalDigest != request.ProposalDigest) throw new VersionControlProblem("stale_head");
        if (proposal.PolicyVersion != XmpMergePolicy.Version) throw new VersionControlProblem("unsupported_merge_policy");
        var used = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<VersionControlEntry>();
        var generated = new List<PendingXmp>();
        var resolved = new List<MergeResolvedView>();
        foreach (var item in proposal.Affected)
        {
            var prefix = $"{item.AssetId:D}/{item.EditId?.ToString("D") ?? "source"}:";
            foreach (var conflict in item.Conflicts) _ = Choice(prefix + conflict);
        }
        if (used.Count != request.Resolutions.Count) throw new VersionControlProblem("invalid_resolution", 400);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        foreach (var item in proposal.Affected)
        {
            var prefix = $"{item.AssetId:D}/{item.EditId?.ToString("D") ?? "source"}:";
            string? proposedSha = item.ProposedXmpSha256;
            byte[]? bytes = null;
            if (item.Conflicts.Contains("whole_xmp", StringComparer.Ordinal))
                proposedSha = Choice(prefix + "whole_xmp") == "source" ? item.SourceXmpSha256 : item.TargetXmpSha256;
            else if (item.TargetXmpSha256 != item.SourceXmpSha256 &&
                     item.TargetXmpSha256 != item.BaseXmpSha256 && item.SourceXmpSha256 != item.BaseXmpSha256)
            {
                if (item.EditId is null || item.BaseXmpSha256 is null || item.TargetXmpSha256 is null || item.SourceXmpSha256 is null)
                    throw new VersionControlProblem("unresolved_divergence");
                var choices = item.Conflicts.Where(conflict => conflict is not ("path" or "lifecycle"))
                    .ToDictionary(conflict => conflict, conflict => Choice(prefix + conflict), StringComparer.Ordinal);
                try
                {
                    bytes = XmpMergePolicy.Compose(await ReadXmpAsync(connection, item.BaseXmpSha256, ct),
                        await ReadXmpAsync(connection, item.TargetXmpSha256, ct),
                        await ReadXmpAsync(connection, item.SourceXmpSha256, ct), choices);
                }
                catch (InvalidDataException) { throw new VersionControlProblem("unresolved_divergence"); }
                proposedSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                generated.Add(new(item.AssetId, item.EditId.Value, proposedSha, bytes,
                    item.BaseXmpSha256, item.TargetXmpSha256, item.SourceXmpSha256));
            }
            var path = item.Conflicts.Contains("path", StringComparer.Ordinal)
                ? Choice(prefix + "path") == "source" ? item.SourcePath : item.TargetPath : item.ProposedPath;
            var lifecycle = item.Conflicts.Contains("lifecycle", StringComparer.Ordinal)
                ? Choice(prefix + "lifecycle") == "source" ? item.SourceLifecycle : item.TargetLifecycle : item.ProposedLifecycle;
            var changed = proposedSha != item.TargetXmpSha256 || path != item.TargetPath || lifecycle != item.TargetLifecycle;
            if (!changed) generated.RemoveAll(value => value.AssetId == item.AssetId && value.EditId == item.EditId);
            if (proposedSha is not null && bytes is null) bytes = await ReadXmpAsync(connection, proposedSha, ct);
            resolved.Add(new(item.AssetId, item.EditId, proposedSha,
                bytes is null ? null : Convert.ToBase64String(bytes), path, lifecycle, changed));
            if (changed)
                entries.Add(new(item.AssetId, item.EditId,
                    item.TargetXmpSha256 is null ? "added" : proposedSha is null ? "deleted" : "modified",
                    item.TargetXmpSha256, proposedSha, path, lifecycle));
        }
        var unsigned = new MergeReviewView(proposal.ProposalDigest, resolved, proposal.Retained, "");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned)));
        return new(proposal, unsigned with { ReviewDigest = digest }, entries, generated);

        string Choice(string key)
        {
            if (!request.Resolutions.TryGetValue(key, out var value) || value is not ("target" or "source"))
                throw new VersionControlProblem("unresolved_divergence");
            used.Add(key);
            return value;
        }
    }

    public async Task<MergeProposalView> PrepareAsync(string owner, string library, MergePrepareRequest request, CancellationToken ct)
    {
        var targetBranch = RefNames.Normalize(request.TargetBranch);
        var sourceBranch = RefNames.Normalize(request.SourceBranch);
        if (targetBranch == sourceBranch || !CommitId(request.ExpectedTargetHead) || !CommitId(request.ExpectedSourceHead))
            throw new VersionControlProblem("invalid_request", 400);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var targetHead = await BranchHeadAsync(connection, owner, library, targetBranch, ct);
        var sourceHead = await BranchHeadAsync(connection, owner, library, sourceBranch, ct);
        if (targetHead != request.ExpectedTargetHead || sourceHead != request.ExpectedSourceHead)
            throw new VersionControlProblem("stale_head");
        if (await ScalarAsync(connection,
            "SELECT 1 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND branch_name IN ($3,$4) LIMIT 1",
            ct, owner, library, targetBranch, sourceBranch) is not null)
            throw new VersionControlProblem("stale_working_state");
        var graph = await QueryService.LoadAsync(connection, owner, library, ct);
        var comparison = GraphQueries.Compare(graph, targetHead, sourceHead);
        var affected = new List<MergeAffectedView>(comparison.Changes.Count);
        foreach (var change in comparison.Changes)
        {
            if (await ScalarAsync(connection, """
                SELECT 1 FROM assets a LEFT JOIN edit_identities e ON e.id=$3 AND e.asset_id=a.id
                WHERE a.owner_id=$1 AND a.id=$2 AND a.state IN ('active','restored')
                  AND ($3::uuid IS NULL OR e.id IS NOT NULL)
                """, ct, owner, change.AssetId, change.EditId) is null)
                throw new VersionControlProblem("cross_library_reference", 403);
            await VersionControlAdapters.ValidateEntriesAsync(connection, owner, library,
                [new VersionControlEntry(change.AssetId, change.EditId, "deleted", change.BaseContentSha256,
                    change.OursContentSha256, null, null),
                 new VersionControlEntry(change.AssetId, change.EditId, "deleted", change.TheirsContentSha256,
                    null, null, null)], storage, ct);
            var conflicts = new List<string>();
            var fieldDifferences = new List<XmpFieldView>();
            string? proposedSha;
            if (comparison.BaseCommitId is null)
            {
                proposedSha = null;
                conflicts.Add("whole_xmp");
            }
            else if (change.OursContentSha256 == change.TheirsContentSha256) proposedSha = change.OursContentSha256;
            else if (change.OursContentSha256 == change.BaseContentSha256) proposedSha = change.TheirsContentSha256;
            else if (change.TheirsContentSha256 == change.BaseContentSha256) proposedSha = change.OursContentSha256;
            else if (change.BaseContentSha256 is not null && change.OursContentSha256 is not null && change.TheirsContentSha256 is not null)
            {
                var baseBytes = await ReadXmpAsync(connection, change.BaseContentSha256, ct);
                var targetBytes = await ReadXmpAsync(connection, change.OursContentSha256, ct);
                var sourceBytes = await ReadXmpAsync(connection, change.TheirsContentSha256, ct);
                var assessment = XmpMergePolicy.Assess(baseBytes, targetBytes, sourceBytes);
                fieldDifferences.AddRange(assessment.Fields.Select(field => new XmpFieldView(field.Field, field.Base, field.Target,
                    field.Source, field.RequiresResolution)));
                conflicts.AddRange(assessment.Fields.Where(field => field.RequiresResolution).Select(field => field.Field));
                if (!assessment.CanCompose)
                {
                    fieldDifferences.Add(new("whole_xmp", assessment.UnavailableReason, null, null, true));
                    conflicts.Add("whole_xmp");
                }
                proposedSha = conflicts.Count == 0
                    ? Convert.ToHexStringLower(SHA256.HashData(XmpMergePolicy.Compose(baseBytes, targetBytes, sourceBytes, new Dictionary<string, string>())))
                    : null;
            }
            else { proposedSha = null; conflicts.Add("whole_xmp"); }
            var proposedPath = SelectState(change.BasePath, change.OursPath, change.TheirsPath, "path", conflicts);
            var proposedLifecycle = SelectState(change.BaseLifecycle, change.OursLifecycle, change.TheirsLifecycle, "lifecycle", conflicts);
            if (comparison.BaseCommitId is null)
            {
                if (change.OursPath != change.TheirsPath && !conflicts.Contains("path", StringComparer.Ordinal)) conflicts.Add("path");
                if (change.OursLifecycle != change.TheirsLifecycle && !conflicts.Contains("lifecycle", StringComparer.Ordinal)) conflicts.Add("lifecycle");
            }
            affected.Add(new(change.AssetId, change.EditId, change.BaseContentSha256, change.OursContentSha256,
                change.TheirsContentSha256, proposedSha, change.BasePath, change.OursPath, change.TheirsPath,
                proposedPath, change.BaseLifecycle, change.OursLifecycle, change.TheirsLifecycle, proposedLifecycle,
                conflicts, fieldDifferences,
                change.OursContentSha256 is null ? "unavailable" : "not_requested",
                change.TheirsContentSha256 is null ? "unavailable" : "not_requested"));
        }
        var affectedKeys = affected.Select(item => $"{item.AssetId:D}/{item.EditId?.ToString("D") ?? "source"}")
            .ToHashSet(StringComparer.Ordinal);
        var retained = GraphQueries.State(graph, targetHead).Where(pair => !affectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MergeRetainedView(pair.Value.AssetId, pair.Value.EditId,
                pair.Value.ContentSha256, pair.Value.Path, pair.Value.Lifecycle)).ToArray();
        var body = new MergeProposalView(targetBranch, sourceBranch, targetHead, sourceHead,
            comparison.BaseCommitId, XmpMergePolicy.Version, affected, retained, "");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
        LineageTelemetry.MergePrepares.Add(1);
        LineageTelemetry.MergeConflicts.Add(affected.Sum(item => item.Conflicts.Count));
        await transaction.CommitAsync(ct);
        var visible = new List<MergeAffectedView>(affected.Count);
        foreach (var item in affected)
        {
            var target = await lineage.PreviewStatesAsync(owner, library,
                [new ImageVersionView(targetHead, item.AssetId, item.EditId, item.TargetXmpSha256,
                    item.TargetPath, item.TargetLifecycle, item.TargetPreviewState)], ct);
            var source = await lineage.PreviewStatesAsync(owner, library,
                [new ImageVersionView(sourceHead, item.AssetId, item.EditId, item.SourceXmpSha256,
                    item.SourcePath, item.SourceLifecycle, item.SourcePreviewState)], ct);
            visible.Add(item with
            {
                TargetPreviewState = target[0].PreviewState,
                SourcePreviewState = source[0].PreviewState,
                TargetPreviewProvenance = target[0].PreviewProvenance,
                SourcePreviewProvenance = source[0].PreviewProvenance
            });
        }
        return body with { ProposalDigest = digest, Affected = visible };
    }

    private Task<byte[]> ReadXmpAsync(NpgsqlConnection connection, string digest, CancellationToken ct) =>
        VerifiedXmpReader.ReadAsync(connection, storage, digest, ct);

    private static async Task<string> BranchHeadAsync(NpgsqlConnection connection, string owner, string library, string branch, CancellationToken ct)
    {
        var result = await ScalarAsync(connection,
            "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'",
            ct, owner, library, branch) as string;
        return result?.Trim() ?? throw new VersionControlProblem("ref_not_found", 404);
    }

    private static string? SelectState(string? baseline, string? target, string? source, string field, List<string> conflicts)
    {
        if (target == source || source == baseline) return target;
        if (target == baseline) return source;
        conflicts.Add(field);
        return null;
    }

    private static bool CommitId(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
