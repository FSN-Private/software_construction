using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Sync.Checkout;

public enum CheckoutDirtyAction { Block, Preserve, Discard }
public sealed record LocalCheckoutResult(string Branch, long Version, Guid? StashId);

public sealed class CheckoutCoordinator(string root, SyncJournal journal, IRemoteSyncClient remote)
{
    public async Task<LocalCheckoutResult> CheckoutAsync(string branch, CheckoutDirtyAction action, bool confirmDiscard, CancellationToken cancellationToken = default)
    {
        var device = await journal.GetDeviceAsync(cancellationToken) ?? throw new InvalidOperationException("device_not_registered");
        var unsafeState = await journal.HasUnsafeStateAsync(cancellationToken);
        if (unsafeState && action == CheckoutDirtyAction.Block) throw new InvalidOperationException("dirty_checkout");
        if (unsafeState && action == CheckoutDirtyAction.Discard && !confirmDiscard) throw new InvalidOperationException("confirmation_required");

        Guid? localStash = null;
        if (unsafeState && action == CheckoutDirtyAction.Preserve)
        {
            localStash = Guid.NewGuid();
            var stash = Directory.CreateDirectory(Path.Combine(root, ".checkout-stashes", localStash.Value.ToString("N")));
            foreach (var item in await journal.ListItemsAsync(cancellationToken: cancellationToken))
            {
                if (item.State is not (SyncItemState.Dirty or SyncItemState.Uploading or SyncItemState.Conflicted) && !await journal.HasPendingAsync(item.Identity, cancellationToken)) continue;
                var source = Path.Combine(root, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source)) continue;
                var target = Path.Combine(stash.FullName, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
            }
        }

        var key = $"checkout:{device.DeviceId}:{device.Branch}:{branch}:{device.Cursor}:{action}";
        var result = await remote.CheckoutAsync(device.LibraryId, branch, action.ToString().ToLowerInvariant(), confirmDiscard, device.Version, key, cancellationToken);
        var registration = await remote.RegisterDeviceAsync(device.LibraryId, branch, cancellationToken);
        if (unsafeState) await journal.ClearUnsafeStateAsync(cancellationToken);
        await journal.SaveDeviceAsync(device with { Branch = registration.Branch, Cursor = registration.Cursor, Health = DeviceHealth.Online, Version = registration.CheckoutVersion }, cancellationToken);
        return new(registration.Branch, registration.CheckoutVersion, localStash ?? result.StashId);
    }
}
