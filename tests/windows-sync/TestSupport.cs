using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Remote;

static class TestAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class FakeCloudFilesAdapter : ICloudFilesAdapter
{
    public string Version => "fake-v1";
    public List<string> Placeholders { get; } = [];
    public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Dehydrated { get; } = [];
    public void RegisterRoot(string root, ReadOnlySpan<byte> rootIdentity) => Directory.CreateDirectory(root);
    public void CreatePlaceholders(string directory, IReadOnlyList<PlaceholderDefinition> placeholders) => Placeholders.AddRange(placeholders.Select(item => Path.Combine(directory, item.RelativeName)));
    public void UpdatePlaceholder(string path, PlaceholderIdentity identity) { }
    public void SetPinState(string path, bool pinned) { if (pinned) Pinned.Add(path); else Pinned.Remove(path); }
    public void Hydrate(string path) { }
    public void Dehydrate(string path) => Dehydrated.Add(path);
}

sealed class FakeRemoteSyncClient(IReadOnlyList<RemoteItem> items, IReadOnlyDictionary<Guid, byte[]> content) : IRemoteSyncClient
{
    public bool Online { get; set; } = true;
    public List<string> Mutations { get; } = [];
    public string? ConflictCode { get; set; }
    public bool ExpireCursorOnce { get; set; }
    public long DownloadedBytes { get; private set; }

    public Task<DeviceRegistration> RegisterDeviceAsync(string library, string branch, CancellationToken cancellationToken)
    {
        RequireOnline();
        return Task.FromResult(new DeviceRegistration("fake-device", library, branch, null, 1, false, "fake-operation"));
    }

    public Task<DeviceRegistration> RevokeDeviceAsync(string library, CancellationToken cancellationToken)
    {
        RequireOnline();
        Mutations.Add("revoke:" + library);
        return Task.FromResult(new DeviceRegistration("fake-device", library, "main", null, 2, true, "fake-operation"));
    }

    public Task<RemotePage> EnumerateAsync(string library, string branch, string? cursor, CancellationToken cancellationToken)
    {
        RequireOnline();
        if (cursor is not null && ExpireCursorOnce) { ExpireCursorOnce = false; throw new RemoteSyncException("cursor_expired", System.Net.HttpStatusCode.Conflict, "fake-operation"); }
        var offset = int.TryParse(cursor, out var value) ? value : 0;
        var page = items.Skip(offset).Take(1000).ToArray();
        return Task.FromResult(new RemotePage(page, offset + page.Length < items.Count ? (offset + page.Length).ToString() : null, offset + page.Length < items.Count));
    }

    public Task<Stream> DownloadAsync(string library, Guid contentId, CancellationToken cancellationToken)
    {
        RequireOnline();
        var bytes = content[contentId];
        DownloadedBytes += bytes.Length;
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public async Task<RemoteMutation> UploadWorkingAsync(string library, string branch, string idempotencyKey, RemoteItem item, string? baseSha256, Stream bytes, CancellationToken cancellationToken)
    {
        RequireOnline();
        if (ConflictCode is not null) throw new RemoteSyncException(ConflictCode, System.Net.HttpStatusCode.Conflict, "fake-operation");
        using var sink = new MemoryStream();
        await bytes.CopyToAsync(sink, cancellationToken);
        Mutations.Add("upload:" + idempotencyKey);
        return new("fake-operation", "working", 1, item.Sha256);
    }

    public Task<RemoteMutation> ApplyLifecycleAsync(string kind, Guid identity, string idempotencyKey, long expectedVersion, string? destination, CancellationToken cancellationToken)
    {
        RequireOnline();
        Mutations.Add(kind + ":" + idempotencyKey);
        return Task.FromResult(new RemoteMutation("fake-operation", kind, expectedVersion + 1));
    }

    public Task<CheckoutResult> CheckoutAsync(string library, string branch, string dirtyAction, bool confirmDiscard, long expectedVersion, string idempotencyKey, CancellationToken cancellationToken)
    {
        RequireOnline();
        Mutations.Add("checkout:" + branch);
        return Task.FromResult(new CheckoutResult("fake-operation", branch, expectedVersion + 1, dirtyAction == "preserve" ? Guid.NewGuid() : null));
    }

    private void RequireOnline()
    {
        if (!Online) throw new HttpRequestException("offline");
    }
}
