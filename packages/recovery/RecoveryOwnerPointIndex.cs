namespace Platform.Recovery;

/// <summary>Strongly consistent, append-only scope index used by the recovery purge scanner.</summary>
public static class RecoveryOwnerPointIndex
{
    public static string ScopeHash(string ownerId, string libraryId)
    {
        RecoveryValidation.Text(ownerId, nameof(ownerId), 200);
        RecoveryValidation.Text(libraryId, nameof(libraryId), 200);
        return RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes(ownerId + "\0" + libraryId));
    }

    public static string PartitionKey(string ownerId, string libraryId) => "OWNER#" + ScopeHash(ownerId, libraryId);

    public static string PointSortKey(Guid pointId)
    {
        if (pointId == Guid.Empty) throw new InvalidDataException("owner_point_index_identity_invalid");
        return "POINT#" + pointId.ToString("D").ToLowerInvariant();
    }

    public static string ComputeBootstrapPointSetSha256(string ownerId, string libraryId,
        IEnumerable<Guid> pointIds)
    {
        ArgumentNullException.ThrowIfNull(pointIds);
        RecoveryValidation.Text(ownerId, nameof(ownerId), 200);
        RecoveryValidation.Text(libraryId, nameof(libraryId), 200);
        var points = pointIds.Select(pointId =>
        {
            if (pointId == Guid.Empty) throw new InvalidDataException("owner_point_index_identity_invalid");
            return pointId.ToString("D").ToLowerInvariant();
        }).Order(StringComparer.Ordinal).ToArray();
        if (points.Distinct(StringComparer.Ordinal).Count() != points.Length)
            throw new InvalidDataException("owner_point_index_duplicate");
        var payload = new BootstrapPointSetV1(1, ownerId, libraryId, points);
        return RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(payload));
    }

    public static void ValidateStrongIndex(string ownerId, string libraryId, int bootstrapPointCount,
        string bootstrapPointSetSha256, long indexedPointCount,
        IReadOnlyCollection<RecoveryOwnerPointMembership> indexedPoints,
        IReadOnlyDictionary<Guid, string> discoveredPoints)
    {
        ArgumentNullException.ThrowIfNull(indexedPoints);
        ArgumentNullException.ThrowIfNull(discoveredPoints);
        RecoveryValidation.Text(ownerId, nameof(ownerId), 200);
        RecoveryValidation.Text(libraryId, nameof(libraryId), 200);
        if (bootstrapPointCount < 0 || indexedPointCount < bootstrapPointCount ||
            indexedPoints.Count != indexedPointCount || !IsLowerSha256(bootstrapPointSetSha256))
            throw new InvalidDataException("owner_point_index_count_invalid");

        var indexByPoint = new Dictionary<Guid, string>();
        var bootstrapPointIds = new List<Guid>();
        foreach (var item in indexedPoints)
        {
            if (item.PointId == Guid.Empty || !IsLowerSha256(item.SourceManifestSha256) ||
                !indexByPoint.TryAdd(item.PointId, item.SourceManifestSha256))
                throw new InvalidDataException("owner_point_index_member_invalid");
            if (item.BootstrapMember) bootstrapPointIds.Add(item.PointId);
        }

        if (bootstrapPointIds.Count != bootstrapPointCount ||
            ComputeBootstrapPointSetSha256(ownerId, libraryId, bootstrapPointIds) != bootstrapPointSetSha256 ||
            discoveredPoints.Count != indexByPoint.Count)
            throw new InvalidDataException("owner_point_index_baseline_mismatch");

        foreach (var (pointId, digest) in discoveredPoints)
            if (!indexByPoint.TryGetValue(pointId, out var indexedDigest) || indexedDigest != digest)
                throw new InvalidDataException("owner_point_index_discovery_mismatch");
    }

    private static bool IsLowerSha256(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record BootstrapPointSetV1(int SchemaVersion, string OwnerId, string LibraryId,
        IReadOnlyList<string> PointIds);
}

public sealed record RecoveryOwnerPointMembership(Guid PointId, string SourceManifestSha256, bool BootstrapMember);
