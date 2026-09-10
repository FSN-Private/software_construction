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
        var commit = new CommitRequest(request.StagingSetId, request.TargetBranch, request.ExpectedTargetHead, request.Message, request.AuthoredAt);
        return await commits.PublishAsync(owner, library, client, commit, idempotencyKey, operationId, cancellationToken, new(request.BaseCommitId, request.SourceCommitId, request.ExpectedTargetHead));
    }
}
