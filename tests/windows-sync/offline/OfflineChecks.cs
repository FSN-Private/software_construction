using System.Security.Cryptography;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.Provider;
using Platform.WindowsSync.Provider.Availability;
using Platform.WindowsSync.Provider.Hydration;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;
using Platform.WindowsSync.Sync;
using Platform.WindowsSync.Sync.Lifecycle;

static class OfflineChecks
{
    public static async Task RunAsync()
    {
        var original = "initial xmp"u8.ToArray();
        var updated = "offline xmp change"u8.ToArray();
        var asset = Guid.NewGuid();
        var edit = Guid.NewGuid();
        var item = new RemoteItem(asset, edit, Guid.NewGuid(), "project/photo.dng.xmp", Digest(original), original.Length);
        var remote = new FakeRemoteSyncClient([item], new Dictionary<Guid, byte[]> { [item.ContentId] = original });
        var cloud = new FakeCloudFilesAdapter();
        var directory = Directory.CreateTempSubdirectory("windows-sync-offline-");
        var database = Path.Combine(directory.FullName, "state", "journal.db");
        string identity;
        string local;
        try
        {
            await using (var journal = new SyncJournal(database))
            {
                await journal.InitializeAsync();
                var provider = new NamespaceProvider(directory.FullName, journal, cloud, remote);
                await provider.RegisterAsync(new("device-offline", "library-a", "main", null, DeviceHealth.Online, false));
                await provider.SynchronizeAsync();
                var saved = (await journal.ListItemsAsync()).Single();
                identity = saved.Identity;
                local = Path.Combine(directory.FullName, saved.LocalPath.Replace('/', Path.DirectorySeparatorChar));
                var availability = new AvailabilityService(directory.FullName, journal, cloud, new HydrationService(directory.FullName, journal, remote));
                var pin = await availability.SetPinnedAsync("project", true);
                TestAssert.That(pin is [{ Success: true }] && cloud.Pinned.Contains(local), "Recursive pin must hydrate and report each requested item.");
                TestAssert.That((await journal.GetItemAsync(identity))?.State == SyncItemState.Pinned, "Pinned content must remain available offline.");

                remote.Online = false;
                await File.WriteAllBytesAsync(local, updated);
                await new UploadCoordinator(directory.FullName, journal, remote, TimeSpan.FromMilliseconds(5)).RecordStableChangeAsync(identity, local);
                await new LifecycleCoordinator(journal).RecordAsync(identity, JournalOperationKind.Delete, null, 1);
                var beforeDehydrate = (await journal.PendingAsync()).Count;
                TestAssert.That(!await availability.TryDehydrateAsync(identity), "Dirty or pending content must not dehydrate.");
                TestAssert.That((await journal.PendingAsync()).Count == beforeDehydrate, "Dehydration must never create delete intent.");
            }

            remote.Online = true;
            await using (var reopened = new SyncJournal(database))
            {
                await reopened.InitializeAsync();
                var replay = new UploadCoordinator(directory.FullName, reopened, remote, TimeSpan.FromMilliseconds(5));
                await replay.ReplayAsync();
                await replay.ReplayAsync();
                TestAssert.That(remote.Mutations.Count(value => value.StartsWith("upload:", StringComparison.Ordinal)) == 1, "Restart replay must publish a stable edit exactly once.");
                TestAssert.That(remote.Mutations.Count(value => value.StartsWith("delete:", StringComparison.Ordinal)) == 1, "Offline delete must replay as a distinct lifecycle mutation.");
                TestAssert.That((await reopened.PendingAsync()).Count == 0, "Acknowledged operations must leave no pending replay.");
            }

            await using (var quotaJournal = new SyncJournal(Path.Combine(directory.FullName, "quota.db")))
            {
                await quotaJournal.InitializeAsync();
                await quotaJournal.SaveItemAsync(new(identity, "quota/file.xmp", "quota/file.xmp", Digest(original), original.Length, SyncItemState.OnlineOnly, false, Digest(original), 0));
                var outcome = await new AvailabilityService(directory.FullName, quotaJournal, cloud, new HydrationService(directory.FullName, quotaJournal, remote), () => 0).SetPinnedAsync("quota", true);
                TestAssert.That(outcome is [{ Success: false, Error: "disk_full" }], "Recursive pin must report disk-space failures per item.");
            }
        }
        finally { directory.Delete(true); }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
