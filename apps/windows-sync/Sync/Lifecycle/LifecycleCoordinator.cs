using System.Security.Cryptography;
using System.Text;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Sync.Lifecycle;

public sealed class LifecycleCoordinator(SyncJournal journal)
{
    public async Task<long> RecordAsync(string identity, JournalOperationKind kind, string? destination, long expectedVersion, CancellationToken cancellationToken = default)
    {
        if (kind == JournalOperationKind.Upload) throw new ArgumentException("Upload uses the stable-write path.", nameof(kind));
        var item = await journal.GetItemAsync(identity, cancellationToken) ?? throw new FileNotFoundException("placeholder_not_found");
        var fingerprint = $"{kind}\n{identity}\n{expectedVersion}\n{destination}";
        var key = $"lifecycle:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant()}";
        return await journal.QueueAsync(key, identity, kind, item.BaseSha256, item.Sha256, destination, cancellationToken, expectedVersion);
    }
}
