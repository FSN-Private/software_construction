using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Query;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl.MergePublishing;

public sealed class MergeService(QueryService queries, CommitService commits)
{
    public async Task<VersionControlResult> PublishAsync(string owner, string library, string client, MergeRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        var divergence = await queries.CompareAsync(owner, library, request.ExpectedTargetHead, request.SourceCommitId, cancellationToken);
        if (divergence.BaseCommitId != request.BaseCommitId) throw new VersionControlProblem("unresolved_divergence");
        var required = divergence.Changes.Where(change => Changed(change.BaseContentSha256, change.BasePath, change.BaseLifecycle, change.OursContentSha256, change.OursPath, change.OursLifecycle) && Changed(change.BaseContentSha256, change.BasePath, change.BaseLifecycle, change.TheirsContentSha256, change.TheirsPath, change.TheirsLifecycle)).Select(change => $"{change.AssetId:D}/{change.EditId?.ToString("D") ?? "source"}").Order(StringComparer.Ordinal).ToArray();
        var commit = new CommitRequest(request.StagingSetId, request.TargetBranch, request.ExpectedTargetHead, request.Message, request.AuthoredAt);
        return await commits.PublishAsync(owner, library, client, commit, idempotencyKey, operationId, cancellationToken, new(request.BaseCommitId, request.SourceCommitId, request.ExpectedTargetHead, required));
    }

    private static bool Changed(string? baseContent, string? basePath, string? baseLifecycle, string? content, string? path, string? lifecycle) => baseContent != content || basePath != path || baseLifecycle != lifecycle;
}
