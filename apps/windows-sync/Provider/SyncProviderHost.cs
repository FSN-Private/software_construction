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

    private void QueueStableChange(string fullPath) => _ = RecordStableChangeAsync(fullPath);

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

    private void QueueCreated(string fullPath) => _ = RecordCreatedAsync(fullPath);

    private async Task RecordCreatedAsync(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, relative, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if ((await journal.PendingAsync()).Any(operation => operation.Identity == item.Identity && operation.Kind == JournalOperationKind.Delete))
            await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Restore, item.LogicalPath, item.LocalSequence);
        QueueStableChange(fullPath);
    }

    private void QueueRename(string oldPath, string newPath) => _ = RecordRenameAsync(oldPath, newPath);

    private async Task RecordRenameAsync(string oldPath, string newPath)
    {
        var oldRelative = Path.GetRelativePath(root, oldPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, oldRelative, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        var destination = Path.GetRelativePath(root, newPath).Replace('\\', '/');
        await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Move, destination, item.LocalSequence);
        await journal.SetItemPathAsync(item.Identity, destination, destination);
    }

    private void QueueDelete(string fullPath) => _ = RecordDeleteAsync(fullPath);

    private async Task RecordDeleteAsync(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var item = (await journal.ListItemsAsync()).SingleOrDefault(candidate => string.Equals(candidate.LocalPath, relative, StringComparison.OrdinalIgnoreCase));
        if (item is not null) await lifecycle.RecordAsync(item.Identity, JournalOperationKind.Delete, null, item.LocalSequence);
    }

    public ValueTask DisposeAsync()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
