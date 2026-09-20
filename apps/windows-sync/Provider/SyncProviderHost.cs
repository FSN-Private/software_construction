using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;
using Platform.WindowsSync.Sync;
using Platform.WindowsSync.Sync.Lifecycle;

namespace Platform.WindowsSync.Provider;

public sealed class SyncProviderHost : IAsyncDisposable
{
    private readonly string root;
    private readonly SyncJournal journal;
    private readonly IRemoteSyncClient remote;
    private readonly NamespaceProvider provider;
    private readonly UploadCoordinator uploads;
    private readonly LifecycleCoordinator lifecycle;
    private readonly FileSystemWatcher watcher;
    private CloudFilesConnection? connection;

    public SyncProviderHost(string root, SyncJournal journal, IRemoteSyncClient remote, ICloudFilesAdapter cloudFiles)
    {
        this.root = Path.GetFullPath(root);
        this.journal = journal;
        this.remote = remote;
        provider = new(this.root, journal, cloudFiles, remote);
        uploads = new(this.root, journal, remote, TimeSpan.FromMilliseconds(750));
        lifecycle = new(journal);
        watcher = new(this.root) { Filter = "*", IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, args) => QueueStableChange(args.FullPath);
        watcher.Created += (_, args) => QueueCreated(args.FullPath);
        watcher.Renamed += (_, args) => QueueRename(args.OldFullPath, args.FullPath);
        watcher.Deleted += (_, args) => QueueDelete(args.FullPath);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await provider.SynchronizeAsync(cancellationToken);
        connection = new(root, async (identity, token) =>
        {
            var device = await journal.GetDeviceAsync(token) ?? throw new InvalidOperationException("device_not_registered");
            await using var content = await remote.DownloadAsync(device.LibraryId, identity.ContentId, token);
            using var memory = new MemoryStream(identity.Length <= int.MaxValue ? (int)identity.Length : 0);
            await content.CopyToAsync(memory, token);
            return memory.ToArray();
        }, (identity, success) => _ = journal.SetItemStateAsync(Convert.ToBase64String(identity.Encode()), success ? SyncItemState.Available : SyncItemState.Error));
        watcher.EnableRaisingEvents = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await provider.SynchronizeAsync(cancellationToken);
                await uploads.ReplayAsync(cancellationToken);
            }
            catch (RemoteSyncException error) when (error.IsAuthenticationRequired)
            {
                var device = await journal.GetDeviceAsync(CancellationToken.None);
                if (device is not null) await journal.SaveDeviceAsync(device with { Health = DeviceHealth.AuthenticationRequired }, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                var device = await journal.GetDeviceAsync(CancellationToken.None);
                if (device is not null) await journal.SaveDeviceAsync(device with { Health = DeviceHealth.Offline }, CancellationToken.None);
            }
        }
    }

    private void QueueStableChange(string fullPath)
    {
        if (!ProviderMutationTracker.Consume(fullPath)) _ = RecordStableChangeAsync(fullPath);
    }

    private async Task RecordStableChangeAsync(string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, relative, StringComparison.OrdinalIgnoreCase));
            if (item is not null && item.State is not (SyncItemState.OnlineOnly or SyncItemState.Hydrating) && File.Exists(fullPath) && fullPath.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase)) await uploads.RecordStableChangeAsync(item.Identity, fullPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    private void QueueCreated(string fullPath)
    {
        if (!ProviderMutationTracker.Consume(fullPath)) _ = RecordCreatedAsync(fullPath);
    }

    private async Task RecordCreatedAsync(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, relative, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if ((await journal.PendingAsync()).Any(operation => operation.Identity == item.Identity && operation.Kind == JournalOperationKind.Delete))
            await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Restore, item.LogicalPath);
        QueueStableChange(fullPath);
    }

    private void QueueRename(string oldPath, string newPath)
    {
        if (!ProviderMutationTracker.Consume(oldPath) && !ProviderMutationTracker.Consume(newPath)) _ = RecordRenameAsync(oldPath, newPath);
    }

    internal async Task RecordRenameAsync(string oldPath, string newPath)
    {
        var oldRelative = Path.GetRelativePath(root, oldPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, oldRelative, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        var destination = Path.GetRelativePath(root, newPath).Replace('\\', '/');
        var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
        if (!identity.EditId.HasValue)
        {
            await RecordAssetRenameAsync(item, destination);
            return;
        }

        var original = (await journal.ListItemsAsync()).SingleOrDefault(candidate =>
        {
            var candidateIdentity = PlaceholderIdentity.Decode(Convert.FromBase64String(candidate.Identity));
            return candidateIdentity.AssetId == identity.AssetId && !candidateIdentity.EditId.HasValue;
        });
        if (original is not null && IsCanonicalSidecar(original.LogicalPath, destination))
        {
            await journal.SetItemPathAsync(item.Identity, destination, destination);
            return;
        }
        if (original is not null && TryAssetDestination(original.LogicalPath, item.LogicalPath, destination, out var assetDestination) && File.Exists(Path.Combine(root, assetDestination.Replace('/', Path.DirectorySeparatorChar))))
        {
            await RecordAssetRenameAsync(original, assetDestination);
            return;
        }

        await journal.SaveConflictAsync(new(Guid.NewGuid(), item.Identity, item.BaseSha256, item.Sha256, item.Sha256, newPath, "sidecar_rename_requires_asset", false));
        await journal.SetItemStateAsync(item.Identity, SyncItemState.Conflicted);
    }

    private async Task RecordAssetRenameAsync(SyncItem item, string destination)
    {
        if (string.Equals(item.LocalPath, destination, StringComparison.OrdinalIgnoreCase)) return;
        await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Move, destination);
        await journal.SetItemPathAsync(item.Identity, destination, destination);
    }

    private static bool TryAssetDestination(string assetPath, string sidecarPath, string destination, out string assetDestination)
    {
        assetDestination = string.Empty;
        var assetSlash = assetPath.LastIndexOf('/');
        var assetName = assetPath[(assetSlash + 1)..];
        var dot = assetName.LastIndexOf('.');
        if (dot <= 0) return false;
        var prefix = assetName[..dot];
        var extension = assetName[dot..];
        var sidecarName = sidecarPath[(sidecarPath.LastIndexOf('/') + 1)..];
        if (!sidecarName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = sidecarName[prefix.Length..];
        if (!suffix.EndsWith(extension + ".xmp", StringComparison.OrdinalIgnoreCase)) return false;
        var destinationSlash = destination.LastIndexOf('/');
        var destinationName = destination[(destinationSlash + 1)..];
        if (!destinationName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || destinationName.Length == suffix.Length) return false;
        assetDestination = destination[..(destinationSlash + 1)] + destinationName[..^suffix.Length] + extension;
        return true;
    }

    private static bool IsCanonicalSidecar(string assetPath, string sidecarPath)
    {
        var assetSlash = assetPath.LastIndexOf('/');
        var sidecarSlash = sidecarPath.LastIndexOf('/');
        if (!string.Equals(assetPath[..(assetSlash + 1)], sidecarPath[..(sidecarSlash + 1)], StringComparison.OrdinalIgnoreCase)) return false;
        var assetName = assetPath[(assetSlash + 1)..];
        var dot = assetName.LastIndexOf('.');
        if (dot <= 0) return false;
        var prefix = assetName[..dot];
        var suffix = assetName[dot..] + ".xmp";
        var sidecarName = sidecarPath[(sidecarSlash + 1)..];
        if (!sidecarName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !sidecarName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var ordinal = sidecarName[prefix.Length..^suffix.Length];
        return ordinal.Length == 0 || ordinal.Length >= 3 && ordinal[0] == '_' && ordinal[1..].All(char.IsDigit);
    }

    private void QueueDelete(string fullPath)
    {
        if (!ProviderMutationTracker.Consume(fullPath)) _ = RecordDeleteAsync(fullPath);
    }

    private async Task RecordDeleteAsync(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, relative, StringComparison.OrdinalIgnoreCase));
        if (item is not null) await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Delete, null);
    }

    public ValueTask DisposeAsync()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
