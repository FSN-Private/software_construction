using System.Security.Cryptography;
using System.Text;
using Platform.WindowsSync.Platform;
using Platform.WindowsSync.State;

namespace Platform.WindowsSync.Sync.Lifecycle;

public sealed class LifecycleCoordinator(SyncJournal journal)
{
    public async Task<long> RecordAsync(string identity, JournalOperationKind kind, string? destination, CancellationToken cancellationToken = default)
    {
        if (kind == JournalOperationKind.Upload) throw new ArgumentException("Upload uses the stable-write path.", nameof(kind));
        var item = await journal.GetItemAsync(identity, cancellationToken) ?? throw new FileNotFoundException("placeholder_not_found");
        var placeholder = PlaceholderIdentity.Decode(Convert.FromBase64String(identity));
        var expectedVersion = kind == JournalOperationKind.Move || !placeholder.EditId.HasValue ? item.AssetVersion : item.EditVersion ?? throw new InvalidDataException("edit_version_missing");
        var pending = await journal.PendingAsync(cancellationToken);
        var duplicate = pending.FirstOrDefault(operation => operation.Identity == identity && operation.Kind == kind && operation.Destination == destination);
        if (duplicate is not null) return duplicate.Sequence;
        foreach (var operation in pending.Where(operation => operation.Kind != JournalOperationKind.Upload))
        {
            var pendingIdentity = PlaceholderIdentity.Decode(Convert.FromBase64String(operation.Identity));
            var sameTarget = kind == JournalOperationKind.Move || !placeholder.EditId.HasValue
                ? pendingIdentity.AssetId == placeholder.AssetId && (operation.Kind == JournalOperationKind.Move || !pendingIdentity.EditId.HasValue)
                : pendingIdentity.EditId == placeholder.EditId && operation.Kind != JournalOperationKind.Move;
            if (sameTarget) expectedVersion = Math.Max(expectedVersion, operation.ExpectedVersion + 1);
        }
        var fingerprint = $"{kind}\n{identity}\n{expectedVersion}\n{destination}";
        var key = $"lifecycle:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant()}";
        return await journal.QueueAsync(key, identity, kind, item.BaseSha256, item.Sha256, destination, cancellationToken, expectedVersion);
    }
}
