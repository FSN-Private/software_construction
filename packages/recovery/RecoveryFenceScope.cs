using System.Security.Cryptography;
using System.Text;

namespace Platform.Recovery;

/// <summary>Canonical DynamoDB fence partition identifiers shared by copy and purge workers.</summary>
public static class RecoveryFenceScope
{
    public static string SnapshotScopeId(string ownerId, string libraryId)
    {
        RecoveryValidation.Text(ownerId, nameof(ownerId), 200);
        RecoveryValidation.Text(libraryId, nameof(libraryId), 200);
        return "SNAPSHOT#" + Hash(ownerId + "\0" + libraryId);
    }

    public static string MediaScopeId(RecoveryLogicalReference logicalReference, string category, string payloadSha256)
    {
        ArgumentNullException.ThrowIfNull(logicalReference);
        logicalReference.Validate();
        if (category is not ("original" or "xmp")) throw new InvalidDataException("invalid_media_fence_category");
        RecoveryValidation.Hash(payloadSha256, nameof(payloadSha256));
        var canonical = string.Join("\0", logicalReference.OwnerId, logicalReference.LibraryId,
            logicalReference.AssetId?.ToString("D") ?? "original",
            logicalReference.EditId?.ToString("D") ?? "original", category, payloadSha256.ToLowerInvariant());
        return "MEDIA#" + Hash(canonical);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
