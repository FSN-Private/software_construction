using System.Security.Cryptography;
using Platform.WindowsSync.Device;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Provider;
using Platform.WindowsSync.Provider.Hydration;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;
using Platform.WindowsSync.Sync.Checkout;
using Platform.WindowsSync.Sync.Conflicts;
using Platform.WindowsSync.UI;

static class ConflictChecks
{
    public static async Task RunAsync()
    {
        var initial = "base xmp"u8.ToArray();
        var remoteBytes = "web metadata edit"u8.ToArray();
        var asset = Guid.NewGuid();
        var edit = Guid.NewGuid();
        var remoteItem = new RemoteItem(asset, edit, Guid.NewGuid(), "unicode/รูปภาพ.xmp", Digest(initial), initial.Length, EditVersion: 1);
        var remote = new FakeRemoteSyncClient([remoteItem], new Dictionary<Guid, byte[]> { [remoteItem.ContentId] = initial });
        var cloud = new FakeCloudFilesAdapter();
        var directory = Directory.CreateTempSubdirectory("windows-sync-conflicts-");
        try
        {
            await using var journal = new SyncJournal(Path.Combine(directory.FullName, "journal.db"));
            await journal.InitializeAsync();
            var provider = new NamespaceProvider(directory.FullName, journal, cloud, remote);
            await provider.RegisterAsync(new("device-a", "library-a", "main", null, DeviceHealth.Online, false));
            await provider.SynchronizeAsync();
            var item = (await journal.ListItemsAsync()).Single();
            await new HydrationService(directory.FullName, journal, remote).HydrateAsync(item.Identity);
            var local = Path.Combine(directory.FullName, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));

            await journal.SetItemStateAsync(item.Identity, SyncItemState.Dirty);
            var incoming = remoteItem with { ContentId = Guid.NewGuid(), Sha256 = Digest(remoteBytes), Length = remoteBytes.Length };
            remote = new FakeRemoteSyncClient([incoming], new Dictionary<Guid, byte[]> { [incoming.ContentId] = remoteBytes });
            var conflicts = new ConflictCoordinator(directory.FullName, journal, remote);
            var applied = await conflicts.ApplyRemoteAsync(incoming);
            TestAssert.That(!applied && (await journal.GetItemAsync(item.Identity))?.State == SyncItemState.Conflicted, "A web/remote edit must not overwrite a dirty local XMP.");
            TestAssert.That((await journal.ListConflictsAsync()) is [{ Resolved: false }], "Both divergent inputs must remain represented by a visible conflict.");
            TestAssert.That((await File.ReadAllBytesAsync(local)).SequenceEqual(initial), "The dirty local path must retain its bytes.");

            var checkout = new CheckoutCoordinator(directory.FullName, journal, remote);
            await ThrowsAsync<InvalidOperationException>(() => checkout.CheckoutAsync("other", CheckoutDirtyAction.Block, false));
            var preserved = await checkout.CheckoutAsync("other", CheckoutDirtyAction.Preserve, false);
            TestAssert.That(preserved.StashId is not null && (await journal.GetDeviceAsync())?.Branch == "other", "Checkout preserve must create a recoverable local stash and change only this device branch.");

            await new DeviceService(journal).RevokeAsync();
            await ThrowsAsync<InvalidOperationException>(() => provider.SynchronizeAsync());
            TestAssert.That(File.Exists(local), "Revocation must retain local bytes for explicit owner cleanup/export.");

            foreach (var state in Enum.GetValues<SyncItemState>())
            {
                var status = CompanionStatus.For(state);
                TestAssert.That(!string.IsNullOrWhiteSpace(status.AccessibleName) && status.Icon != status.AccessibleName, "Every status must have a non-color accessible name.");
            }
            foreach (var health in Enum.GetValues<DeviceHealth>()) TestAssert.That(!string.IsNullOrWhiteSpace(CompanionStatus.For(health).KeyboardAction), "Every device health state must expose keyboard-operable guidance.");
        }
        finally { directory.Delete(true); }

        await BranchReconciliationAsync();
    }

    private static async Task BranchReconciliationAsync()
    {
        var main = new RemoteItem(Guid.NewGuid(), null, Guid.NewGuid(), "shared/photo.dng", new string('a', 64), 10, AssetVersion: 3);
        var other = new RemoteItem(Guid.NewGuid(), null, Guid.NewGuid(), "shared/photo.dng", new string('b', 64), 20, AssetVersion: 7);
        var collision = new RemoteItem(Guid.NewGuid(), null, Guid.NewGuid(), "SHARED/PHOTO.DNG", new string('c', 64), 30, AssetVersion: 2);
        var remote = new FakeRemoteSyncClient([main], new Dictionary<Guid, byte[]>());
        remote.SetBranchItems("other", [other, collision]);
        var directory = Directory.CreateTempSubdirectory("windows-sync-branches-");
        try
        {
            await using var journal = new SyncJournal(Path.Combine(directory.FullName, "journal.db"));
            await journal.InitializeAsync();
            var provider = new NamespaceProvider(directory.FullName, journal, new FakeCloudFilesAdapter(), remote);
            await provider.RegisterAsync(new("branch-device", "library-a", "main", null, DeviceHealth.Online, false));
            await provider.SynchronizeAsync();
            await new CheckoutCoordinator(directory.FullName, journal, remote).CheckoutAsync("other", CheckoutDirtyAction.Block, false);
            await provider.SynchronizeAsync();
            var branchItems = await journal.ListItemsAsync();
            var selected = branchItems.Single(candidate => PlaceholderIdentity.Decode(Convert.FromBase64String(candidate.Identity)).AssetId == other.AssetId);
            var selectedIdentity = PlaceholderIdentity.Decode(Convert.FromBase64String(selected.Identity));
            TestAssert.That(selectedIdentity.AssetId == other.AssetId && selected.LocalPath == "shared/photo.dng" && selected.AssetVersion == 7, "Checkout reconciliation must remove a clean stale placeholder and preserve the selected branch path without a collision suffix.");
            TestAssert.That(branchItems.Count == 2 && branchItems.Single(candidate => candidate.Identity != selected.Identity).LocalPath != collision.Path, "Two selected-branch items with the same Windows collision key must both survive through deterministic suffixing.");

            await journal.SetItemStateAsync(selected.Identity, SyncItemState.Dirty);
            remote.SetBranchItems("other", []);
            await journal.SaveDeviceAsync((await journal.GetDeviceAsync())! with { Cursor = null });
            await provider.SynchronizeAsync();
            TestAssert.That((await journal.GetItemAsync(selected.Identity))?.State == SyncItemState.Conflicted && (await journal.ListConflictsAsync()).Any(conflict => conflict.Reason == "remote_missing"), "A dirty item absent from a full remote snapshot must remain recoverable as a conflict.");
        }
        finally { directory.Delete(true); }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
