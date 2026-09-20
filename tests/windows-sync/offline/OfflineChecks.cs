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
        var item = new RemoteItem(asset, edit, Guid.NewGuid(), "project/photo.dng.xmp", Digest(original), original.Length, EditVersion: 4);
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
                var lifecycle = new LifecycleCoordinator(journal);
                var deleteSequence = await lifecycle.RecordAsync(identity, JournalOperationKind.Delete, null);
                TestAssert.That(await lifecycle.RecordAsync(identity, JournalOperationKind.Delete, null) == deleteSequence, "Duplicate lifecycle notifications must reuse the pending operation.");
                await lifecycle.RecordAsync(identity, JournalOperationKind.Restore, item.Path);
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
                TestAssert.That(remote.Mutations.Count(value => value.StartsWith("restore:", StringComparison.Ordinal)) == 1, "Offline restore must replay after its delete.");
                TestAssert.That(remote.LifecycleCalls is [{ Kind: "delete", EditId: not null, ExpectedVersion: 4 }, { Kind: "restore", EditId: not null, ExpectedVersion: 5 }], "Edit lifecycle replay must target the edit identity and chain authoritative versions in journal order.");
                TestAssert.That((await reopened.PendingAsync()).Count == 0, "Acknowledged operations must leave no pending replay.");

                File.Delete(local);
                remote.ConflictCode = "lifecycle_conflict";
                await new LifecycleCoordinator(reopened).RecordAsync(identity, JournalOperationKind.Delete, null);
                await replay.ReplayAsync();
                TestAssert.That((await reopened.GetItemAsync(identity))?.State == SyncItemState.Conflicted && (await reopened.ListConflictsAsync()).Any(conflict => conflict.Reason == "lifecycle_conflict"), "A lifecycle conflict after local deletion must remain visible even when there are no local bytes to copy.");
                remote.ConflictCode = null;
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
