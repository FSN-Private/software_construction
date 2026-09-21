using System.Diagnostics;
using System.Security.Cryptography;
using Platform.WindowsSync.Provider;
using Platform.WindowsSync.Remote;
using Platform.WindowsSync.State;

static class PerformanceChecks
{
    public static async Task RunAsync()
    {
        var digest = Convert.ToHexString(SHA256.HashData("remote-object"u8)).ToLowerInvariant();
        var firstPage = Enumerable.Range(0, 1000).Select(index => Item(index, digest)).ToArray();
        var listingSamples = new List<double>();
        for (var device = 0; device < 5; device++)
        {
            var directory = Directory.CreateTempSubdirectory("windows-sync-listing-");
            try
            {
                await using var journal = new SyncJournal(Path.Combine(directory.FullName, "journal.db"));
                await journal.InitializeAsync();
                var remote = new FakeRemoteSyncClient(firstPage, new Dictionary<Guid, byte[]>());
                var provider = new NamespaceProvider(directory.FullName, journal, new FakeCloudFilesAdapter(), remote);
                await provider.RegisterAsync(new($"device-{device}", "library-a", "main", null, DeviceHealth.Online, false));
                var stopwatch = Stopwatch.StartNew();
                TestAssert.That(await provider.SynchronizeAsync() == 1000, "Each independent device must enumerate the first 1,000 placeholders.");
                listingSamples.Add(stopwatch.Elapsed.TotalSeconds);
                TestAssert.That(remote.DownloadedBytes == 0, "Namespace listing must not download original content.");
            }
            finally { directory.Delete(true); }
        }
        listingSamples.Sort();
        TestAssert.That(listingSamples[^1] <= 2, $"First 1,000-placeholder listing p95 exceeded two seconds: {listingSamples[^1]:F3}s.");
        Console.WriteLine($"Windows sync listing p95 (5 devices, 1,000 placeholders): {listingSamples[^1]:F3}s");

        var namespaceItems = Enumerable.Range(0, 100_000).Select(index => Item(index, digest)).ToArray();
        var capacityDirectory = Directory.CreateTempSubdirectory("windows-sync-capacity-");
        try
        {
            await using var journal = new SyncJournal(Path.Combine(capacityDirectory.FullName, "journal.db"));
            await journal.InitializeAsync();
            var remote = new FakeRemoteSyncClient(namespaceItems, new Dictionary<Guid, byte[]>());
            var provider = new NamespaceProvider(capacityDirectory.FullName, journal, new FakeCloudFilesAdapter(), remote);
            await provider.RegisterAsync(new("capacity-device", "library-a", "main", null, DeviceHealth.Online, false));
            var stopwatch = Stopwatch.StartNew();
            TestAssert.That(await provider.SynchronizeAsync() == 100_000, "The provider must enumerate a 100,000-image namespace.");
            TestAssert.That((await journal.ListItemsAsync()).Count == 100_000 && remote.DownloadedBytes == 0, "A multi-terabyte logical namespace must remain placeholder-only until accessed or pinned.");
            TestAssert.That(await provider.SynchronizeAsync() == 0 && remote.Enumerations == 101, "An unchanged terminal cursor must poll once without re-enumerating the 100,000-item namespace.");
            Console.WriteLine($"Windows sync 100,000-placeholder capacity: {stopwatch.Elapsed.TotalSeconds:F3}s, downloaded bytes: {remote.DownloadedBytes}");
        }
        finally { capacityDirectory.Delete(true); }
    }

    private static RemoteItem Item(int index, string digest)
    {
        var assetBytes = new byte[16];
        BitConverter.GetBytes(index + 1).CopyTo(assetBytes, 0);
        var contentBytes = new byte[16];
        BitConverter.GetBytes(index + 1).CopyTo(contentBytes, 0);
        contentBytes[15] = 1;
        return new(new(assetBytes), null, new(contentBytes), $"library/{index / 1000:D3}/image-{index:D6}.dng", digest, 50L * 1024 * 1024);
    }
}
