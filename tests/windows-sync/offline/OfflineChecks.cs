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

                var renameAsset = Guid.NewGuid();
                var renameEdit = Guid.NewGuid();
                var renameOriginal = "rename/photo.dng";
                var renameSidecar = "rename/photo_01.dng.xmp";
                var renamedOriginal = "rename/photo2.dng";
                var renamedSidecar = "rename/photo2_01.dng.xmp";
                var renameOriginalIdentity = Convert.ToBase64String(new PlaceholderIdentity(renameAsset, null, Guid.NewGuid(), Digest(original), original.Length).Encode());
                var renameEditIdentity = Convert.ToBase64String(new PlaceholderIdentity(renameAsset, renameEdit, Guid.NewGuid(), Digest(original), original.Length).Encode());
                await reopened.SaveItemAsync(new(renameOriginalIdentity, renameOriginal, renameOriginal, Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 4));
                await reopened.SaveItemAsync(new(renameEditIdentity, renameSidecar, renameSidecar, Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 4, 4));
                var oldOriginal = Path.Combine(directory.FullName, renameOriginal.Replace('/', Path.DirectorySeparatorChar));
                var oldSidecar = Path.Combine(directory.FullName, renameSidecar.Replace('/', Path.DirectorySeparatorChar));
                var newOriginal = Path.Combine(directory.FullName, renamedOriginal.Replace('/', Path.DirectorySeparatorChar));
                var newSidecar = Path.Combine(directory.FullName, renamedSidecar.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(oldOriginal)!);
                await File.WriteAllBytesAsync(oldOriginal, original);
                await File.WriteAllBytesAsync(oldSidecar, original);
                File.Move(oldOriginal, newOriginal);
                File.Move(oldSidecar, newSidecar);
                await using (var host = new SyncProviderHost(directory.FullName, reopened, remote, cloud))
                    await host.RecordRenameAsync(oldSidecar, newSidecar);
                var renameMove = (await reopened.PendingAsync()).Single(operation => operation.Kind == JournalOperationKind.Move && operation.Identity == renameOriginalIdentity);
                TestAssert.That(renameMove.Destination == renamedOriginal && !((await reopened.PendingAsync()).Any(operation => operation.Identity == renameEditIdentity && operation.Kind == JournalOperationKind.Move)), "A coordinated sidecar rename must produce exactly one asset move using the original image path.");
                await replay.ReplayAsync();
                TestAssert.That(remote.LifecycleCalls.Any(call => call.Kind == "move" && call.AssetId == renameAsset && call.EditId is null), "Coordinated sidecar rename replay must target only the asset lifecycle endpoint.");

                var orderedAsset = Guid.NewGuid();
                var orderedEdit = Guid.NewGuid();
                var orderedOriginalIdentity = Convert.ToBase64String(new PlaceholderIdentity(orderedAsset, null, Guid.NewGuid(), Digest(original), original.Length).Encode());
                var orderedEditIdentity = Convert.ToBase64String(new PlaceholderIdentity(orderedAsset, orderedEdit, Guid.NewGuid(), Digest(original), original.Length).Encode());
                await reopened.SaveItemAsync(new(orderedOriginalIdentity, "ordered/photo.dng", "ordered/photo.dng", Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 1));
                await reopened.SaveItemAsync(new(orderedEditIdentity, "ordered/photo.dng.xmp", "ordered/photo.dng.xmp", Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 1, 1));
                var orderedOldOriginal = Path.Combine(directory.FullName, "ordered", "photo.dng");
                var orderedOldSidecar = Path.Combine(directory.FullName, "ordered", "photo.dng.xmp");
                var orderedNewOriginal = Path.Combine(directory.FullName, "ordered", "photo2.dng");
                var orderedNewSidecar = Path.Combine(directory.FullName, "ordered", "photo2.dng.xmp");
                Directory.CreateDirectory(Path.GetDirectoryName(orderedOldOriginal)!);
                await File.WriteAllBytesAsync(orderedOldOriginal, original);
                await File.WriteAllBytesAsync(orderedOldSidecar, original);
                File.Move(orderedOldOriginal, orderedNewOriginal);
                File.Move(orderedOldSidecar, orderedNewSidecar);
                await using (var host = new SyncProviderHost(directory.FullName, reopened, remote, cloud))
                {
                    await host.RecordRenameAsync(orderedOldOriginal, orderedNewOriginal);
                    await host.RecordRenameAsync(orderedOldSidecar, orderedNewSidecar);
                }
                TestAssert.That((await reopened.PendingAsync()).Count(operation => operation.Kind == JournalOperationKind.Move && operation.Identity == orderedOriginalIdentity) == 1 && (await reopened.GetItemAsync(orderedEditIdentity)) is { LocalPath: "ordered/photo2.dng.xmp", State: not SyncItemState.Conflicted }, "Original-first and sidecar-first watcher ordering must converge on one asset move without a sidecar conflict.");

                var orphanAsset = Guid.NewGuid();
                var orphanEdit = Guid.NewGuid();
                var orphanOriginalIdentity = Convert.ToBase64String(new PlaceholderIdentity(orphanAsset, null, Guid.NewGuid(), Digest(original), original.Length).Encode());
                var orphanEditIdentity = Convert.ToBase64String(new PlaceholderIdentity(orphanAsset, orphanEdit, Guid.NewGuid(), Digest(original), original.Length).Encode());
                await reopened.SaveItemAsync(new(orphanOriginalIdentity, "orphan/photo.dng", "orphan/photo.dng", Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 1));
                await reopened.SaveItemAsync(new(orphanEditIdentity, "orphan/photo.dng.xmp", "orphan/photo.dng.xmp", Digest(original), original.Length, SyncItemState.Available, false, Digest(original), 0, 1, 1));
                await using (var host = new SyncProviderHost(directory.FullName, reopened, remote, cloud))
                    await host.RecordRenameAsync(Path.Combine(directory.FullName, "orphan", "photo.dng.xmp"), Path.Combine(directory.FullName, "orphan", "sidecar-only.dng.xmp"));
                TestAssert.That((await reopened.GetItemAsync(orphanEditIdentity))?.State == SyncItemState.Conflicted && (await reopened.ListConflictsAsync()).Any(conflict => conflict.Identity == orphanEditIdentity && conflict.Reason == "sidecar_rename_requires_asset") && !(await reopened.PendingAsync()).Any(operation => operation.Identity == orphanEditIdentity && operation.Kind == JournalOperationKind.Move), "An edit-only sidecar rename must preserve a visible conflict and never move the asset.");
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
