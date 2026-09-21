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
        await journal.SaveDeviceAsync(device with { Branch = registered.Branch, Cursor = registered.Cursor, Health = DeviceHealth.Online, Version = registered.CheckoutVersion }, cancellationToken);
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
        var fullReconciliation = cursor is null;
        var seen = new HashSet<(Guid AssetId, Guid? EditId)>();
        var reconciledExpiredCursor = false;
        do
        {
            RemotePage page;
            try { page = await remote.EnumerateAsync(device.LibraryId, device.Branch, cursor, cancellationToken); }
            catch (RemoteSyncException error) when (error.Code == "cursor_expired" && !reconciledExpiredCursor)
            {
                cursor = null;
                fullReconciliation = true;
                seen.Clear();
                reconciledExpiredCursor = true;
                await journal.SaveDeviceAsync(device with { Cursor = null }, cancellationToken);
                continue;
            }
            var saved = new List<SyncItem>(page.Items.Count);
            var placeholders = new Dictionary<string, List<PlaceholderDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in page.Items)
            {
                seen.Add((item.AssetId, item.EditId));
                var identity = new PlaceholderIdentity(item.AssetId, item.EditId, item.ContentId, item.Sha256, item.Length);
                var local = PathMapper.ToLocalPath(item.Path, item.EditId ?? item.AssetId);
                var key = PathMapper.CollisionKey(local);
                var encoded = Convert.ToBase64String(identity.Encode());
                if (byObject.TryGetValue((item.AssetId, item.EditId), out var prior))
                {
                    if (existing.TryGetValue(key, out var pathCollision) && pathCollision.Identity != prior.Identity)
                    {
                        if (Seen(pathCollision, seen) || await IsUnsafeAsync(pathCollision, cancellationToken)) local = PathMapper.ResolveCollision(local, item.EditId ?? item.AssetId);
                        else await RemoveAsync(pathCollision, existing, byObject, cancellationToken);
                    }
                    var contentChanged = prior.Identity != encoded;
                    var pathChanged = !string.Equals(prior.LocalPath, local, StringComparison.Ordinal);
                    var versionChanged = prior.AssetVersion != item.AssetVersion || prior.EditVersion != item.EditVersion;
                    if (!contentChanged && !pathChanged)
                    {
                        if (versionChanged) await journal.SaveItemAsync(prior with { AssetVersion = item.AssetVersion, EditVersion = item.EditVersion }, cancellationToken);
                        continue;
                    }
                    var unsafeState = await IsUnsafeAsync(prior, cancellationToken);
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
                        if (File.Exists(source))
                        {
                            ProviderMutationTracker.Suppress(source);
                            ProviderMutationTracker.Suppress(destination);
                            File.Move(source, destination, false);
                        }
                        await journal.SetItemPathAsync(prior.Identity, item.Path, local, cancellationToken);
                        existing.Remove(PathMapper.CollisionKey(prior.LocalPath));
                        prior = prior with { LogicalPath = item.Path, LocalPath = local, AssetVersion = item.AssetVersion, EditVersion = item.EditVersion };
                        await journal.SetItemRemoteVersionAsync(prior.Identity, false, item.AssetVersion, cancellationToken);
                        if (item.EditVersion.HasValue) await journal.SetItemRemoteVersionAsync(prior.Identity, true, item.EditVersion.Value, cancellationToken);
                    }
                    if (contentChanged)
                    {
                        if (prior.State == SyncItemState.OnlineOnly)
                            await journal.ReplaceItemAsync(prior.Identity, prior with { Identity = encoded, Sha256 = item.Sha256, Length = item.Length, BaseSha256 = item.Sha256, AssetVersion = item.AssetVersion, EditVersion = item.EditVersion }, cancellationToken);
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
                    if (Seen(collision, seen) || await IsUnsafeAsync(collision, cancellationToken)) local = PathMapper.ResolveCollision(local, item.EditId ?? item.AssetId);
                    else await RemoveAsync(collision, existing, byObject, cancellationToken);
                }
                var relative = local.Replace('/', Path.DirectorySeparatorChar);
                var directory = Path.GetDirectoryName(Path.Combine(root, relative))!;
                Directory.CreateDirectory(directory);
                if (!placeholders.TryGetValue(directory, out var definitions)) placeholders[directory] = definitions = [];
                definitions.Add(new(Path.GetFileName(relative), identity, DateTimeOffset.UtcNow));
                var state = new SyncItem(encoded, item.Path, local, item.Sha256, item.Length, SyncItemState.OnlineOnly, false, item.Sha256, 0, item.AssetVersion, item.EditVersion);
                saved.Add(state);
                existing[PathMapper.CollisionKey(local)] = state;
                byObject[(item.AssetId, item.EditId)] = state;
                count++;
            }
            foreach (var group in placeholders) cloudFiles.CreatePlaceholders(group.Key, group.Value);
            await journal.SaveItemsAsync(saved, cancellationToken);
            cursor = page.Cursor;
            await journal.SaveDeviceAsync(device with { Cursor = cursor }, cancellationToken);
            if (!page.HasMore)
            {
                if (fullReconciliation)
                    foreach (var stale in (await journal.ListItemsAsync(cancellationToken: cancellationToken)).Where(candidate =>
                    {
                        var value = PlaceholderIdentity.Decode(Convert.FromBase64String(candidate.Identity));
                        return !seen.Contains((value.AssetId, value.EditId));
                    }))
                        if (await IsUnsafeAsync(stale, cancellationToken)) await PreserveMissingAsync(stale, cancellationToken);
                        else await RemoveAsync(stale, existing, byObject, cancellationToken);
                break;
            }
        } while (cursor is not null);
        return count;
    }

    private async Task<bool> IsUnsafeAsync(SyncItem item, CancellationToken cancellationToken) =>
        item.State is SyncItemState.Dirty or SyncItemState.Uploading or SyncItemState.Conflicted || await journal.HasPendingAsync(item.Identity, cancellationToken);

    private static bool Seen(SyncItem item, ISet<(Guid AssetId, Guid? EditId)> seen)
    {
        var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
        return seen.Contains((identity.AssetId, identity.EditId));
    }

    private async Task RemoveAsync(SyncItem item, IDictionary<string, SyncItem> existing, IDictionary<(Guid AssetId, Guid? EditId), SyncItem> byObject, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        ProviderMutationTracker.Suppress(path);
        if (File.Exists(path)) File.Delete(path);
        await journal.DeleteItemAsync(item.Identity, cancellationToken);
        existing.Remove(PathMapper.CollisionKey(item.LocalPath));
        var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
        byObject.Remove((identity.AssetId, identity.EditId));
    }

    private async Task PreserveMissingAsync(SyncItem item, CancellationToken cancellationToken)
    {
        if (item.State == SyncItemState.Conflicted) return;
        var id = Guid.NewGuid();
        var source = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        var preserved = Path.Combine(root, ".conflicts", id.ToString("N"), Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
        if (File.Exists(source)) File.Copy(source, preserved, false);
        await journal.SaveConflictAsync(new(id, item.Identity, item.BaseSha256, item.Sha256, "missing", preserved, "remote_missing", false), cancellationToken);
        await journal.SetItemStateAsync(item.Identity, SyncItemState.Conflicted, cancellationToken: cancellationToken);
    }
}
