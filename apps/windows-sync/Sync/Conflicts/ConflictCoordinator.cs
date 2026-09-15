using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Provider.Hydration;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Sync.Conflicts;

public sealed class ConflictCoordinator(string root, SyncJournal journal, IRemoteSyncClient remote)
{
    public async Task<bool> ApplyRemoteAsync(RemoteItem incoming, CancellationToken cancellationToken = default)
    {
        var current = (await journal.ListItemsAsync(cancellationToken: cancellationToken)).SingleOrDefault(item =>
        {
            var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
            return identity.AssetId == incoming.AssetId && identity.EditId == incoming.EditId;
        });
        if (current is null) return false;
        var localPath = Path.Combine(root, current.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        if (current.State is SyncItemState.Dirty or SyncItemState.Uploading or SyncItemState.Conflicted || await journal.HasPendingAsync(current.Identity, cancellationToken))
        {
            var id = Guid.NewGuid();
            var preserved = Path.Combine(root, ".conflicts", id.ToString("N"), Path.GetFileName(localPath));
            Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
            if (File.Exists(localPath)) File.Copy(localPath, preserved, false);
            await journal.SaveConflictAsync(new(id, current.Identity, current.BaseSha256, current.Sha256, incoming.Sha256, preserved, "remote_divergence", false), cancellationToken);
            await journal.SetItemStateAsync(current.Identity, SyncItemState.Conflicted, cancellationToken: cancellationToken);
            return false;
        }

        var identity = new PlaceholderIdentity(incoming.AssetId, incoming.EditId, incoming.ContentId, incoming.Sha256, incoming.Length);
        var encoded = Convert.ToBase64String(identity.Encode());
        await journal.ReplaceItemAsync(current.Identity, current with { Identity = encoded, Sha256 = incoming.Sha256, Length = incoming.Length, BaseSha256 = incoming.Sha256, State = SyncItemState.OnlineOnly }, cancellationToken);
        await new HydrationService(root, journal, remote).HydrateAsync(encoded, cancellationToken);
        return true;
    }
}
