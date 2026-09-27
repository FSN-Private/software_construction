using Platform.Contracts.VersionControl;
using Platform.Contracts.Rendering;

namespace Platform.Contracts.Lineage;

public sealed record LineageNodeView(CommitView Commit, IReadOnlyList<string> Branches, IReadOnlyList<string> Tags, IReadOnlyList<string> HiddenParentIds);

public sealed record LineagePageView(Guid AssetId, string Branch, string HeadCommitId, bool ShowHidden, int Offset, int? NextOffset, IReadOnlyList<LineageNodeView> Nodes);

public sealed record ImageVersionView(string CommitId, Guid AssetId, Guid? EditId, string? XmpSha256, string? Path, string? Lifecycle,
    string PreviewState, Guid? PreviewRequestId = null, RenderProvenance? PreviewProvenance = null);

public sealed record LineageComparisonView(string TargetCommitId, string SourceCommitId, string? BaseCommitId,
    IReadOnlyList<ImageVersionView> TargetStates, IReadOnlyList<ImageVersionView> SourceStates, IReadOnlyList<DivergenceChangeView> Differences,
    IReadOnlyList<ComparedXmpView>? MetadataDifferences = null);
public sealed record ComparedXmpView(Guid? EditId, IReadOnlyList<XmpFieldView> Fields, string State);

public sealed record MergePrepareRequest(string TargetBranch, string SourceBranch, string ExpectedTargetHead, string ExpectedSourceHead);
public sealed record MergeAffectedView(Guid AssetId, Guid? EditId, string? BaseXmpSha256, string? TargetXmpSha256,
    string? SourceXmpSha256, string? ProposedXmpSha256, string? BasePath, string? TargetPath, string? SourcePath,
    string? ProposedPath, string? BaseLifecycle, string? TargetLifecycle, string? SourceLifecycle,
    string? ProposedLifecycle, IReadOnlyList<string> Conflicts, IReadOnlyList<XmpFieldView> MetadataDifferences,
    string TargetPreviewState, string SourcePreviewState,
    RenderProvenance? TargetPreviewProvenance = null, RenderProvenance? SourcePreviewProvenance = null);
public sealed record XmpFieldView(string Field, string? Base, string? Target, string? Source, bool RequiresResolution);
public sealed record MergeRetainedView(Guid AssetId, Guid? EditId, string? XmpSha256, string? Path, string? Lifecycle);
public sealed record MergeProposalView(string TargetBranch, string SourceBranch, string TargetHead, string SourceHead,
    string? BaseCommitId, string PolicyVersion, IReadOnlyList<MergeAffectedView> Affected,
    IReadOnlyList<MergeRetainedView> Retained, string ProposalDigest);
public sealed record MergeReviewRequest(MergePrepareRequest Prepared, string ProposalDigest,
    IReadOnlyDictionary<string, string> Resolutions);
public sealed record MergeResolvedView(Guid AssetId, Guid? EditId, string? XmpSha256, string? XmpBase64,
    string? Path, string? Lifecycle, bool Changed);
public sealed record MergeReviewView(string ProposalDigest, IReadOnlyList<MergeResolvedView> Resolved,
    IReadOnlyList<MergeRetainedView> Retained, string ReviewDigest);
public sealed record MergeConfirmRequest(MergeReviewRequest Reviewed, string ReviewDigest,
    string Message, DateTimeOffset AuthoredAt, bool Confirm);
