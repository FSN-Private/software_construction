using System.Security.Cryptography;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Provider.Hydration;

public sealed class HydrationService(string root, SyncJournal journal, IRemoteSyncClient remote)
{
    public async Task HydrateAsync(string identityValue, CancellationToken cancellationToken = default)
    {
        var item = await journal.GetItemAsync(identityValue, cancellationToken) ?? throw new FileNotFoundException("placeholder_not_found");
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        var identity = PlaceholderIdentity.Decode(Convert.FromBase64String(identityValue));
        var destination = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        await journal.SetItemStateAsync(identityValue, SyncItemState.Hydrating, cancellationToken: cancellationToken);
        try
        {
            await using var source = await remote.DownloadAsync(device.LibraryId, identity.ContentId, cancellationToken);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            await using (var completed = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                if (completed.Length != identity.Length) throw new InvalidDataException("checksum_mismatch");
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(completed, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(actual, identity.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("checksum_mismatch");
            }
            ProviderMutationTracker.Suppress(destination);
            File.Move(temporary, destination, true);
            await journal.SetItemStateAsync(identityValue, item.Pinned ? SyncItemState.Pinned : SyncItemState.Available, sha256: identity.Sha256, cancellationToken: cancellationToken);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            await journal.SetItemStateAsync(identityValue, SyncItemState.Error, cancellationToken: CancellationToken.None);
            throw;
        }
    }
}
