namespace Platform.Contracts.VersionControl;

public sealed record WorkingChangeRequest(Guid AssetId, Guid? EditId, string Branch, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle, long ExpectedVersion);
public sealed record StageRequest(IReadOnlyList<Guid> WorkingChangeIds, string? ExpectedHead, long ExpectedVersion);
public sealed record CommitRequest(Guid StagingSetId, string Branch, string? ExpectedHead, string Message, DateTimeOffset AuthoredAt, IReadOnlyList<string>? AdditionalParents = null);
public sealed record RefRequest(string Name, string CommitId, string? ExpectedHead = null, long? ExpectedVersion = null, bool Confirm = false);
public sealed record CheckoutRequest(string Branch, string DirtyAction, bool ConfirmDiscard = false);
public sealed record RewriteRequest(string Branch, string NewHead, string PreviewToken, bool Confirm);
public sealed record VisibilityRequest(string CommitId, bool Hidden);

public sealed record VersionControlResult(string OperationId, string? CommitId = null, string? RefName = null, long? Version = null);
public sealed record WorkingChangeView(Guid Id, Guid AssetId, Guid? EditId, string Branch, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle, long Version, bool Staged);
public sealed record RefView(string Name, string Kind, string CommitId, bool Protected, long Version);
public sealed record ChangeView(Guid AssetId, Guid? EditId, string Change, string? BaseContentSha256, string? ContentSha256, string? Path, string? Lifecycle);
public sealed record CompareView(string? BaseCommitId, string OursCommitId, string TheirsCommitId, IReadOnlyList<ChangeView> Changes);
public sealed record CommitView(string CommitId, IReadOnlyList<string> Parents, string AuthorId, DateTimeOffset AuthoredAt, DateTimeOffset ReceivedAt, string Message, IReadOnlyList<ChangeView> Changes, bool Hidden);
public sealed record IntegrityView(bool Healthy, IReadOnlyList<string> Codes, int CommitsChecked);

public sealed class VersionControlProblem(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
