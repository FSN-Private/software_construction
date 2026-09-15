using System.Security.Cryptography;
using Platform.WindowsSync.Provider;
using Platform.WindowsSync.Provider.Hydration;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

static class HydrationChecks
{
    public static async Task RunAsync()
    {
        var bytes = "exact remote image bytes"u8.ToArray();
        var asset = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var remoteItem = new RemoteItem(asset, null, contentId, "2026/trip/image.dng", digest, bytes.Length);
        var remote = new FakeRemoteSyncClient([remoteItem], new Dictionary<Guid, byte[]> { [contentId] = bytes });
        var cloud = new FakeCloudFilesAdapter();
        var directory = Directory.CreateTempSubdirectory("windows-sync-hydration-");
        try
        {
            await using var journal = new SyncJournal(Path.Combine(directory.FullName, "state", "journal.db"));
            await journal.InitializeAsync();
            var provider = new NamespaceProvider(directory.FullName, journal, cloud, remote);
            await provider.RegisterAsync(new("device-a", "library-a", "main", null, DeviceHealth.Online, false));
            var populated = await provider.SynchronizeAsync();
            TestAssert.That(populated == 1 && cloud.Placeholders.Count == 1, "Branch enumeration must create one opaque placeholder without downloading bytes.");

            var item = (await journal.ListItemsAsync()).Single();
            await new HydrationService(directory.FullName, journal, remote).HydrateAsync(item.Identity);
            var local = Path.Combine(directory.FullName, item.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            TestAssert.That(await File.ReadAllBytesAsync(local) is var hydrated && hydrated.SequenceEqual(bytes), "Hydration must expose exact authoritative bytes.");
            TestAssert.That((await journal.GetItemAsync(item.Identity))?.State == SyncItemState.Available, "A verified hydration must become available.");

            await journal.SaveDeviceAsync((await journal.GetDeviceAsync())! with { Cursor = "1000" });
            remote.ExpireCursorOnce = true;
            TestAssert.That(await provider.SynchronizeAsync() == 0, "An expired cursor must reconcile without recreating or deleting existing placeholders.");

            var advancedBytes = "permitted remote xmp bytes"u8.ToArray();
            var advanced = remoteItem with { ContentId = Guid.NewGuid(), Sha256 = Convert.ToHexString(SHA256.HashData(advancedBytes)).ToLowerInvariant(), Length = advancedBytes.Length };
            remote = new FakeRemoteSyncClient([advanced], new Dictionary<Guid, byte[]> { [advanced.ContentId] = advancedBytes });
            provider = new NamespaceProvider(directory.FullName, journal, cloud, remote);
            await provider.SynchronizeAsync();
            var advancedItem = (await journal.ListItemsAsync()).Single();
            TestAssert.That(advancedItem.LocalPath == item.LocalPath && (await File.ReadAllBytesAsync(local)).SequenceEqual(advancedBytes), "An eligible remote revision must update the same stable path when local state is clean.");

            var bad = remoteItem with { AssetId = Guid.NewGuid(), ContentId = Guid.NewGuid(), Sha256 = new string('0', 64), Path = "2026/trip/bad.dng" };
            remote = new FakeRemoteSyncClient([bad], new Dictionary<Guid, byte[]> { [bad.ContentId] = bytes });
            provider = new NamespaceProvider(directory.FullName, journal, cloud, remote);
            await provider.SynchronizeAsync();
            var badItem = (await journal.ListItemsAsync()).Single(candidate => candidate.LogicalPath.EndsWith("bad.dng", StringComparison.Ordinal));
            await TestAssertThrowsAsync<InvalidDataException>(() => new HydrationService(directory.FullName, journal, remote).HydrateAsync(badItem.Identity));
            TestAssert.That(!File.Exists(Path.Combine(directory.FullName, badItem.LocalPath.Replace('/', Path.DirectorySeparatorChar))), "Failed hydration must not expose partial bytes.");
        }
        finally { directory.Delete(true); }
    }

    private static async Task TestAssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
