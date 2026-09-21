using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Provider.Hydration;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Provider.Availability;

public sealed record AvailabilityOutcome(string Identity, bool Success, string? Error = null);

public sealed class AvailabilityService(string root, SyncJournal journal, ICloudFilesAdapter cloudFiles, HydrationService hydration, Func<long>? freeBytes = null)
{
    private readonly Func<long> availableBytes = freeBytes ?? (() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace);

    public async Task<IReadOnlyList<AvailabilityOutcome>> SetPinnedAsync(string localPath, bool pinned, CancellationToken cancellationToken = default)
    {
        var items = await journal.ListItemsAsync(localPath.Replace('\\', '/').Trim('/'), cancellationToken);
        var outcomes = new List<AvailabilityOutcome>(items.Count);
        foreach (var item in items)
        {
            var path = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (pinned && item.State == SyncItemState.OnlineOnly)
                {
                    if (availableBytes() < item.Length) throw new IOException("disk_full");
                    await hydration.HydrateAsync(item.Identity, cancellationToken);
                }
                cloudFiles.SetPinState(path, pinned);
                await journal.SetItemStateAsync(item.Identity, pinned ? SyncItemState.Pinned : SyncItemState.Available, pinned, cancellationToken: cancellationToken);
                outcomes.Add(new(item.Identity, true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var code = error.Message == "disk_full" ? "disk_full" : "availability_error";
                await journal.SetItemStateAsync(item.Identity, code == "disk_full" ? SyncItemState.QuotaError : SyncItemState.Error, cancellationToken: CancellationToken.None);
                outcomes.Add(new(item.Identity, false, code));
            }
        }
        return outcomes;
    }

    public async Task<bool> TryDehydrateAsync(string identity, CancellationToken cancellationToken = default)
    {
        var item = await journal.GetItemAsync(identity, cancellationToken) ?? throw new FileNotFoundException("placeholder_not_found");
        if (item.Pinned || item.State is SyncItemState.Dirty or SyncItemState.Uploading or SyncItemState.Conflicted || await journal.HasPendingAsync(identity, cancellationToken)) return false;
        cloudFiles.Dehydrate(Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar)));
        await journal.SetItemStateAsync(identity, SyncItemState.OnlineOnly, false, cancellationToken: cancellationToken);
        return true;
    }
}
