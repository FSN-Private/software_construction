using Microsoft.Data.Sqlite;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.State;

await FoundationChecks.RunAsync();
await ContractChecks.RunAsync();
await HydrationChecks.RunAsync();
await OfflineChecks.RunAsync();
Console.WriteLine("Windows sync checks passed.");

static class FoundationChecks
{
    public static async Task RunAsync()
    {
        var identity = new PlaceholderIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), 3);
        Check(PlaceholderIdentity.Decode(identity.Encode()) == identity, "Placeholder identity must round-trip without credentials or paths.");
        Check(PathMapper.ToLocalPath("trip/CON/photo?.xmp", identity.AssetId) == "trip/~CON/photo%003F.xmp", "Windows names must map deterministically.");
        Check(PathMapper.CollisionKey("caf\u00e9.xmp") == PathMapper.CollisionKey("cafe\u0301.xmp"), "Unicode-equivalent and case-insensitive paths must share a collision key.");

        var directory = Directory.CreateTempSubdirectory("windows-sync-tests-");
        try
        {
            var database = Path.Combine(directory.FullName, "journal.db");
            await using (var journal = new SyncJournal(database))
            {
                await journal.InitializeAsync();
                await journal.SaveDeviceAsync(new("device-1", "library-1", "main", "cursor-1", DeviceHealth.Online, false));
                await journal.QueueAsync("key-1", Convert.ToBase64String(identity.Encode()), JournalOperationKind.Upload, identity.Sha256, identity.Sha256, null);
            }
            await using (var reopened = new SyncJournal(database))
            {
                await reopened.InitializeAsync();
                var saved = await reopened.GetDeviceAsync();
                Check(saved is { DeviceId: "device-1", Branch: "main", Cursor: "cursor-1" } && (await reopened.PendingAsync()).Count == 1, "Device and pending state must survive a journal reopen.");
            }
            var legacyDatabase = Path.Combine(directory.FullName, "journal-v1.db");
            await using (var legacy = new SqliteConnection("Data Source=" + legacyDatabase + ";Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var command = legacy.CreateCommand();
                command.CommandText = """
                    CREATE TABLE device(singleton INTEGER PRIMARY KEY,device_id TEXT NOT NULL,library_id TEXT NOT NULL,branch TEXT NOT NULL,cursor TEXT,health INTEGER NOT NULL,revoked INTEGER NOT NULL,schema_version INTEGER NOT NULL DEFAULT 1);
                    CREATE TABLE items(identity TEXT PRIMARY KEY,logical_path TEXT NOT NULL,local_path TEXT NOT NULL,sha256 TEXT NOT NULL,length INTEGER NOT NULL,state INTEGER NOT NULL,pinned INTEGER NOT NULL,base_sha256 TEXT,local_sequence INTEGER NOT NULL DEFAULT 0,collision_key TEXT NOT NULL UNIQUE);
                    CREATE TABLE operations(sequence INTEGER PRIMARY KEY AUTOINCREMENT,idempotency_key TEXT NOT NULL UNIQUE,identity TEXT NOT NULL,kind INTEGER NOT NULL,base_sha256 TEXT,current_sha256 TEXT,destination TEXT,acknowledged INTEGER NOT NULL DEFAULT 0,attempts INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE conflicts(id TEXT PRIMARY KEY,identity TEXT NOT NULL,base_sha256 TEXT,local_sha256 TEXT NOT NULL,remote_sha256 TEXT NOT NULL,preserved_path TEXT NOT NULL,reason TEXT NOT NULL,resolved INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO operations(idempotency_key,identity,kind) VALUES('legacy-key','legacy-identity',1);
                    PRAGMA user_version=1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var upgraded = new SyncJournal(legacyDatabase))
            {
                await upgraded.InitializeAsync();
                Check((await upgraded.PendingAsync()) is [{ IdempotencyKey: "legacy-key", ExpectedVersion: 0 }], "Journal v1 must upgrade in place without losing pending operations.");
            }
            var credentials = new CredentialStore(Path.Combine(directory.FullName, "credential.dat"));
            try
            {
                credentials.Save("non-reusable-test-token");
                Check(credentials.Load() == "non-reusable-test-token" && !System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)).Contains("non-reusable-test-token", StringComparison.Ordinal), "Credentials must use OS protection and never enter the journal.");
                credentials.Delete();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                Console.WriteLine("DPAPI round-trip skipped because this build identity has no loaded Windows user profile; trusted-host validation remains required.");
            }
        }
        finally { directory.Delete(true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
