using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Sync;

public sealed class UploadCoordinator(string root, SyncJournal journal, IRemoteSyncClient remote, TimeSpan stabilityInterval)
{
    public async Task<long> RecordStableChangeAsync(string identity, string path, CancellationToken cancellationToken = default)
    {
        var item = await journal.GetItemAsync(identity, cancellationToken) ?? throw new FileNotFoundException("placeholder_not_found");
        if (!path.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("only_xmp_is_writable");
        var stable = await StableWriteDetector.WaitAsync(path, stabilityInterval, cancellationToken);
        if (string.Equals(stable.Sha256, item.BaseSha256, StringComparison.Ordinal)) return 0;
        var fingerprint = System.Text.Encoding.UTF8.GetBytes(identity + "\n" + stable.Sha256);
        var key = $"upload:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fingerprint)).ToLowerInvariant()}";
        var sequence = await journal.QueueAsync(key, identity, JournalOperationKind.Upload, item.BaseSha256, stable.Sha256, null, cancellationToken);
        await journal.SetItemStateAsync(identity, SyncItemState.Dirty, sha256: stable.Sha256, cancellationToken: cancellationToken);
        return sequence;
    }

    public async Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        if (device.Revoked) throw new InvalidOperationException("device_revoked");
        foreach (var operation in await journal.PendingAsync(cancellationToken))
        {
            var item = await journal.GetItemAsync(operation.Identity, cancellationToken) ?? throw new InvalidDataException("journal_identity_missing");
            await journal.MarkAttemptAsync(operation.Sequence, cancellationToken);
            try
            {
                if (operation.Kind == JournalOperationKind.Upload)
                {
                    await journal.SetItemStateAsync(item.Identity, SyncItemState.Uploading, cancellationToken: cancellationToken);
                    var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
                    var path = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                    await using var bytes = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var remoteItem = new RemoteItem(identity.AssetId, identity.EditId, identity.ContentId, item.LogicalPath, operation.CurrentSha256!, bytes.Length);
                    await remote.UploadWorkingAsync(device.LibraryId, device.Branch, operation.IdempotencyKey, remoteItem, operation.BaseSha256, bytes, cancellationToken);
                    await journal.SetItemStateAsync(item.Identity, item.Pinned ? SyncItemState.Pinned : SyncItemState.Synchronized, sha256: operation.CurrentSha256, cancellationToken: cancellationToken);
                    await journal.SetItemBaseAsync(item.Identity, operation.CurrentSha256!, cancellationToken);
                }
                else
                {
                    var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(item.Identity));
                    var result = await remote.ApplyLifecycleAsync(operation.Kind.ToString().ToLowerInvariant(), identity.AssetId, identity.EditId, operation.IdempotencyKey, operation.ExpectedVersion, operation.Destination, cancellationToken);
                    await journal.SetItemRemoteVersionAsync(item.Identity, identity.EditId.HasValue && operation.Kind != JournalOperationKind.Move, result.Version, cancellationToken);
                }
                await journal.AcknowledgeAsync(operation.Sequence, cancellationToken);
            }
            catch (RemoteSyncException error) when (error.IsConflict)
            {
                var local = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                var preserved = Path.Combine(root, ".conflicts", Guid.NewGuid().ToString("N") + ".local.xmp");
                Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
                if (File.Exists(local)) File.Copy(local, preserved, false);
                await journal.SaveConflictAsync(new(Guid.NewGuid(), item.Identity, operation.BaseSha256, operation.CurrentSha256 ?? item.Sha256, "remote", preserved, error.Code, false), cancellationToken);
                await journal.SetItemStateAsync(item.Identity, SyncItemState.Conflicted, cancellationToken: cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                await journal.SaveDeviceAsync(device with { Health = DeviceHealth.Offline }, CancellationToken.None);
                break;
            }
        }
    }
}
