namespace Platform.Contracts.WindowsSync;

public sealed record WindowsSyncDeviceRequest(string Branch);
public sealed record WindowsSyncDeviceView(string DeviceId, string LibraryId, string Branch, string? Cursor, long Version, bool Revoked, string OperationId);
public sealed record WindowsSyncNamespaceItem(Guid AssetId, Guid? EditId, Guid ContentId, string Path, string Sha256, long Length, string? Lifecycle);
public sealed record WindowsSyncNamespacePage(IReadOnlyList<WindowsSyncNamespaceItem> Items, string? Cursor, bool HasMore);
public sealed record WindowsSyncMutationView(string OperationId, string State, long Version, string? ContentSha256 = null, string? ConflictCode = null);
