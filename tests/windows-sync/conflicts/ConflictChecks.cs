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
        var remoteItem = new RemoteItem(asset, edit, Guid.NewGuid(), "unicode/รูปภาพ.xmp", Digest(initial), initial.Length);
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
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
