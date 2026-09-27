namespace Platform.Contracts.VersionControl;

using System.Text.Json;

public sealed record WorkingChangeRequest(Guid AssetId, Guid? EditId, string Branch, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle, long ExpectedVersion);
public sealed record StageRequest(IReadOnlyList<Guid> WorkingChangeIds, string? ExpectedHead, long ExpectedVersion);
public sealed record CommitRequest(Guid StagingSetId, string Branch, string? ExpectedHead, string Message, DateTimeOffset AuthoredAt);
public sealed record RefRequest(string Name, string? CommitId = null, string? ExpectedHead = null, long? ExpectedVersion = null, bool Confirm = false);
public sealed record CheckoutRequest(string Branch, string DirtyAction, bool ConfirmDiscard = false, long ExpectedVersion = 0);
public sealed record RestoreRequest(string Branch, string TargetCommitId, string Mode, string Message, string? ExpectedHead);
public sealed record RewriteRequest(string Branch, string NewHead, string PreviewToken, bool Confirm);
public sealed record VisibilityRequest(string CommitId, bool Hidden);
public sealed record MergeRequest(Guid StagingSetId, string TargetBranch, string ExpectedTargetHead, string SourceCommitId, string BaseCommitId, string Message, DateTimeOffset AuthoredAt);

public sealed record VersionControlResult(string OperationId, string? CommitId = null, string? RefName = null, long? Version = null, Guid? RecoveryId = null, Guid? StashId = null);
public sealed record WorkingChangeView(Guid Id, Guid AssetId, Guid? EditId, string Branch, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle, long Version, bool Staged);
public sealed record RefView(string Name, string Kind, string CommitId, bool Protected, long Version);
public sealed record StashView(Guid Id, string SourceRef, JsonElement Snapshot, DateTimeOffset CreatedAt);
public sealed record CheckoutStateView(string? Branch, long Version, long BrowserWorkingCount,
    long GalleryWorkingCount, long DeviceConflictCount);
public sealed record ReflogView(Guid Id, string RefName, string? OldHead, string? NewHead, string Operation, string Reason, DateTimeOffset OccurredAt, DateTimeOffset? RecoveryExpiresAt);
public sealed record ChangeView(Guid AssetId, Guid? EditId, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle);
public sealed record DivergenceChangeView(Guid AssetId, Guid? EditId, string? BaseContentSha256, string? OursContentSha256, string? TheirsContentSha256, string? BasePath, string? OursPath, string? TheirsPath, string? BaseLifecycle, string? OursLifecycle, string? TheirsLifecycle);
public sealed record CompareView(string? BaseCommitId, string OursCommitId, string TheirsCommitId, IReadOnlyList<DivergenceChangeView> Changes);
public sealed record CommitView(string CommitId, IReadOnlyList<string> Parents, string AuthorId, DateTimeOffset AuthoredAt, DateTimeOffset ReceivedAt, string Message, IReadOnlyList<ChangeView> Changes, bool Hidden);
public sealed record IntegrityView(bool Healthy, IReadOnlyList<string> Codes, int CommitsChecked);
public sealed record RewritePreview(string Branch, string? OldHead, string NewHead, IReadOnlyList<string> DisplacedCommitIds, string ConfirmationToken);
public sealed record MergeEvidence(string? BaseCommitId, string SourceCommitId, string TargetCommitId,
    IReadOnlyList<string> RequiredResolutionKeys, string? SourceBranch = null);
public sealed record GeneratedXmpContent(Guid AssetId, Guid EditId, string Sha256, long ByteLength,
    string StorageVersion, string? BaseSha256, string? TargetSha256, string? SourceSha256, string PolicyVersion);

public sealed class VersionControlProblem(string code, int status = 409, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}
