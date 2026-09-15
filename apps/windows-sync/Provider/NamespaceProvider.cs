using System.Text;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;
using Platform.WindowsSync.Sync.Conflicts;

namespace Platform.WindowsSync.Provider;

public sealed class NamespaceProvider(string root, SyncJournal journal, ICloudFilesAdapter cloudFiles, IRemoteSyncClient remote)
{
    public async Task RegisterAsync(SyncDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Revoked) throw new InvalidOperationException("device_revoked");
        var registered = await remote.RegisterDeviceAsync(device.LibraryId, device.Branch, cancellationToken);
        cloudFiles.RegisterRoot(root, Encoding.UTF8.GetBytes($"v1\n{device.DeviceId}\n{device.LibraryId}"));
        await journal.SaveDeviceAsync(device with { Branch = registered.Branch, Cursor = registered.Cursor, Health = DeviceHealth.Online, Version = registered.Version }, cancellationToken);
    }

    public async Task<int> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        if (device.Revoked) throw new InvalidOperationException("device_revoked");
        var existing = (await journal.ListItemsAsync(cancellationToken: cancellationToken)).ToDictionary(item => PathMapper.CollisionKey(item.LocalPath), StringComparer.Ordinal);
        var byObject = existing.Values.ToDictionary(item =>
        {
            var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
            return (identity.AssetId, identity.EditId);
        });
        var count = 0;
        var cursor = device.Cursor;
        var reconciledExpiredCursor = false;
        do
        {
            RemotePage page;
            try { page = await remote.EnumerateAsync(device.LibraryId, device.Branch, cursor, cancellationToken); }
            catch (RemoteSyncException error) when (error.Code == "cursor_expired" && !reconciledExpiredCursor)
            {
                cursor = null;
                reconciledExpiredCursor = true;
                await journal.SaveDeviceAsync(device with { Cursor = null }, cancellationToken);
                continue;
            }
            var saved = new List<SyncItem>(page.Items.Count);
            var placeholders = new Dictionary<string, List<PlaceholderDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in page.Items)
            {
                var identity = new PlaceholderIdentity(item.AssetId, item.EditId, item.ContentId, item.Sha256, item.Length);
                var local = PathMapper.ToLocalPath(item.Path, item.EditId ?? item.AssetId);
                var key = PathMapper.CollisionKey(local);
                var encoded = Convert.ToBase64String(identity.Encode());
                if (byObject.TryGetValue((item.AssetId, item.EditId), out var prior))
                {
                    if (existing.TryGetValue(key, out var pathCollision) && pathCollision.Identity != prior.Identity) local = PathMapper.ResolveCollision(local, item.EditId ?? item.AssetId);
                    var contentChanged = prior.Identity != encoded;
                    var pathChanged = !string.Equals(prior.LocalPath, local, StringComparison.Ordinal);
                    if (!contentChanged && !pathChanged) continue;
                    var unsafeState = prior.State is SyncItemState.Dirty or SyncItemState.Uploading or SyncItemState.Conflicted || await journal.HasPendingAsync(prior.Identity, cancellationToken);
                    if (unsafeState)
                    {
                        await new ConflictCoordinator(root, journal, remote).ApplyRemoteAsync(item, cancellationToken);
                        continue;
                    }
                    if (pathChanged)
                    {
                        var source = Path.Combine(root, prior.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                        var destination = Path.Combine(root, local.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (File.Exists(source)) File.Move(source, destination, false);
                        await journal.SetItemPathAsync(prior.Identity, item.Path, local, cancellationToken);
                        existing.Remove(PathMapper.CollisionKey(prior.LocalPath));
                        prior = prior with { LogicalPath = item.Path, LocalPath = local };
                    }
                    if (contentChanged)
                    {
                        if (prior.State == SyncItemState.OnlineOnly)
                            await journal.ReplaceItemAsync(prior.Identity, prior with { Identity = encoded, Sha256 = item.Sha256, Length = item.Length, BaseSha256 = item.Sha256 }, cancellationToken);
                        else
                            await new ConflictCoordinator(root, journal, remote).ApplyRemoteAsync(item, cancellationToken);
                        cloudFiles.UpdatePlaceholder(Path.Combine(root, local.Replace('/', Path.DirectorySeparatorChar)), identity);
                        prior = (await journal.GetItemAsync(encoded, cancellationToken))!;
                    }
                    existing[PathMapper.CollisionKey(local)] = prior;
                    byObject[(item.AssetId, item.EditId)] = prior;
                    continue;
                }
                if (existing.TryGetValue(key, out var collision))
                {
                    if (collision.Identity == encoded) continue;
                    local = PathMapper.ResolveCollision(local, item.EditId ?? item.AssetId);
                }
                var relative = local.Replace('/', Path.DirectorySeparatorChar);
                var directory = Path.GetDirectoryName(Path.Combine(root, relative))!;
                Directory.CreateDirectory(directory);
                if (!placeholders.TryGetValue(directory, out var definitions)) placeholders[directory] = definitions = [];
                definitions.Add(new(Path.GetFileName(relative), identity, DateTimeOffset.UtcNow));
                var state = new SyncItem(encoded, item.Path, local, item.Sha256, item.Length, SyncItemState.OnlineOnly, false, item.Sha256, 0);
                saved.Add(state);
                existing[PathMapper.CollisionKey(local)] = state;
                byObject[(item.AssetId, item.EditId)] = state;
                count++;
            }
            foreach (var group in placeholders) cloudFiles.CreatePlaceholders(group.Key, group.Value);
            await journal.SaveItemsAsync(saved, cancellationToken);
            cursor = page.Cursor;
            await journal.SaveDeviceAsync(device with { Cursor = cursor }, cancellationToken);
            if (!page.HasMore) break;
        } while (cursor is not null);
        return count;
    }
}
