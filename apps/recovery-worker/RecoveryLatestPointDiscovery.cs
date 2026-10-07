using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryLatestPointDiscovery
{
    private const int MaximumHeadReadAttempts = 2;
    internal static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    public static async Task<(CopyPointer Pointer, RecoveryPointManifestV1 Manifest)> ReadLatestSealedPointAsync(
        IAmazonDynamoDB dynamo,
        IAmazonS3 s3,
        string controlTable,
        string ownerId,
        string libraryId,
        RecoverySigningTrust signerTrust,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dynamo);
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlTable);
        _ = RecoveryOwnerPointIndex.ScopeHash(ownerId, libraryId);

        var indexKey = RecoveryPointIndexBootstrapRunner.IndexKey(ownerId, libraryId);
        var memberPartition = RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId);
        for (var attempt = 0; attempt < MaximumHeadReadAttempts; attempt++)
        {
            var marker = await GetStrongAsync(dynamo, controlTable, indexKey, cancellationToken);
            if (marker is not { Count: > 0 })
                throw new RecoveryWorkerException("scope_index_bootstrap_required");
            RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);
            var head = ReadHead(marker);

            var memberKey = RecoveryPointIndexBootstrapRunner.OwnerPointKey(ownerId, libraryId, head.PointId);
            var member = await GetStrongAsync(dynamo, controlTable, memberKey, cancellationToken);
            ValidateMembership(member, memberPartition, ownerId, libraryId, head);

            var copyKey = CopyKey(head.PointId);
            var copyItem = await GetStrongAsync(dynamo, controlTable, copyKey, cancellationToken);
            if (copyItem is not { Count: > 0 })
                throw new RecoveryWorkerException("scope_index_point_missing");
            if (ReadString(copyItem, "pk") != copyKey["pk"].S || ReadString(copyItem, "sk") != "COPY")
                throw new RecoveryWorkerException("scope_index_point_binding_mismatch");
            var pointer = CopyPointer.From(copyItem);
            if (pointer is null || pointer.PointId != head.PointId ||
                pointer.State is not ("copied" or "access_incomplete") ||
                pointer.OwnerId != ownerId || pointer.LibraryId != libraryId ||
                pointer.SourceManifestSha256 != head.SourceManifestSha256)
                throw new RecoveryWorkerException("scope_index_point_binding_mismatch");

            // This existing verifier checks the signed checkpoint, exact source-manifest
            // receipt, exact S3 version/hash, and canonical manifest bytes.
            var (manifest, _) = await RecoveryVerifierRunner.ReadSourceManifestAsync(s3, pointer, signerTrust,
                cancellationToken);
            if (manifest.RecoveryPointId != head.PointId || manifest.OwnerId != ownerId ||
                manifest.LibraryId != libraryId || manifest.CaptureId != pointer.CaptureId ||
                manifest.SourceRegion != pointer.SourceRegion || manifest.Digest() != head.SourceManifestSha256 ||
                manifest.SnapshotCutoffUtc != head.SnapshotCutoffUtc ||
                manifest.ChangeWatermark.LastSequence != head.ChangeSequence)
                throw new RecoveryWorkerException("latest_copy_manifest_binding_mismatch");

            var markerAfter = await GetStrongAsync(dynamo, controlTable, indexKey, cancellationToken);
            if (markerAfter is not { Count: > 0 })
                throw new RecoveryWorkerException("scope_index_changed_during_read");
            RecoveryPointIndexBootstrapRunner.ValidateComplete(markerAfter);
            var latestAfter = ReadHead(markerAfter);
            if (latestAfter == head) return (pointer, manifest);
        }

        throw new RecoveryWorkerException("latest_copy_head_unstable");
    }

    internal static async Task<(CopyPointer Pointer, RecoveryPointManifestV1 Manifest, DateTimeOffset CheckpointVerifiedAtUtc)?>
        RepairFinalizedPointAsync(IAmazonDynamoDB dynamo, IAmazonS3 s3, string controlTable,
            RecoveryPointManifestV1 expectedManifest, string expectedSourceManifestSha256,
            RecoverySigningTrust signerTrust, CancellationToken cancellationToken)
    {
        if (!IsHash(expectedSourceManifestSha256))
            throw new RecoveryWorkerException("latest_copy_manifest_digest_invalid");
        var copyKey = CopyKey(expectedManifest.RecoveryPointId);
        var copyItem = await GetStrongAsync(dynamo, controlTable, copyKey, cancellationToken);
        if (copyItem is not { Count: > 0 }) return null;
        var state = ReadString(copyItem, "state");
        if (state == "copying") return null;
        if (state is not ("copied" or "access_incomplete")) return null;

        var pointer = CopyPointer.From(copyItem)
            ?? throw new RecoveryWorkerException("scope_index_point_missing");
        if (ReadString(copyItem, "pk") != copyKey["pk"].S || ReadString(copyItem, "sk") != "COPY" ||
            pointer.PointId != expectedManifest.RecoveryPointId || pointer.CaptureId != expectedManifest.CaptureId ||
            pointer.OwnerId != expectedManifest.OwnerId || pointer.LibraryId != expectedManifest.LibraryId ||
            pointer.SourceManifestSha256 != expectedSourceManifestSha256)
            throw new RecoveryWorkerException("scope_index_point_binding_mismatch");

        var memberKey = RecoveryPointIndexBootstrapRunner.OwnerPointKey(pointer.OwnerId, pointer.LibraryId,
            pointer.PointId);
        var member = await GetStrongAsync(dynamo, controlTable, memberKey, cancellationToken);
        var head = new Head(pointer.PointId, expectedSourceManifestSha256,
            expectedManifest.SnapshotCutoffUtc, expectedManifest.ChangeWatermark.LastSequence);
        ValidateMembership(member, RecoveryOwnerPointIndex.PartitionKey(pointer.OwnerId, pointer.LibraryId),
            pointer.OwnerId, pointer.LibraryId, head);

        var (manifest, checkpointVerifiedAtUtc) = await RecoveryVerifierRunner.ReadSourceManifestAsync(s3,
            pointer, signerTrust, cancellationToken);
        if (manifest.RecoveryPointId != expectedManifest.RecoveryPointId ||
            manifest.CaptureId != expectedManifest.CaptureId || manifest.OwnerId != expectedManifest.OwnerId ||
            manifest.LibraryId != expectedManifest.LibraryId || manifest.Digest() != expectedSourceManifestSha256 ||
            manifest.SnapshotCutoffUtc != expectedManifest.SnapshotCutoffUtc ||
            manifest.ChangeWatermark.LastSequence != expectedManifest.ChangeWatermark.LastSequence)
            throw new RecoveryWorkerException("latest_copy_manifest_binding_mismatch");

        await AdvanceLatestSealedPointAsync(dynamo, controlTable, manifest, expectedSourceManifestSha256,
            checkpointVerifiedAtUtc, cancellationToken);
        return (pointer, manifest, checkpointVerifiedAtUtc);
    }

    public static async Task AdvanceLatestSealedPointAsync(IAmazonDynamoDB dynamo, string controlTable,
        RecoveryPointManifestV1 manifest, string sourceManifestSha256,
        DateTimeOffset checkpointVerifiedAtUtc, CancellationToken cancellationToken)
    {
        if (!IsHash(sourceManifestSha256)) throw new RecoveryWorkerException("latest_copy_manifest_digest_invalid");
        ValidateCheckpointTimeBinding(manifest, checkpointVerifiedAtUtc, DateTimeOffset.UtcNow);
        var cutoff = manifest.SnapshotCutoffUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var sequence = manifest.ChangeWatermark.LastSequence;
        var key = RecoveryPointIndexBootstrapRunner.IndexKey(manifest.OwnerId, manifest.LibraryId);
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = controlTable,
                Key = key,
                UpdateExpression = "SET latestSealedPointId=:point, latestSealedSourceManifestSha256=:manifest, latestSealedSnapshotCutoffUtc=:cutoff, latestSealedChangeSequence=:sequence",
                ConditionExpression = "#status=:complete AND indexVersion=:version AND (attribute_not_exists(latestSealedSnapshotCutoffUtc) OR latestSealedSnapshotCutoffUtc < :cutoff OR (latestSealedSnapshotCutoffUtc=:cutoff AND latestSealedChangeSequence < :sequence))",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":complete"] = S("complete"),
                    [":version"] = N(1),
                    [":point"] = S(manifest.RecoveryPointId.ToString("D").ToLowerInvariant()),
                    [":manifest"] = S(sourceManifestSha256),
                    [":cutoff"] = S(cutoff),
                    [":sequence"] = N(sequence)
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            var marker = await GetStrongAsync(dynamo, controlTable, key, cancellationToken);
            if (marker is not { Count: > 0 }) throw new RecoveryWorkerException("scope_index_bootstrap_required");
            RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);
            var current = ReadHead(marker, "latestSealed");
            var nextCutoff = manifest.SnapshotCutoffUtc.ToUniversalTime();
            var comparison = current.SnapshotCutoffUtc.CompareTo(nextCutoff);
            if (comparison > 0 || comparison == 0 && current.ChangeSequence > sequence) return;
            if (comparison == 0 && current.ChangeSequence == sequence && current.PointId == manifest.RecoveryPointId &&
                current.SourceManifestSha256 == sourceManifestSha256) return;
            if (comparison == 0 && current.ChangeSequence == sequence)
                throw new RecoveryWorkerException("latest_sealed_snapshot_conflict");
            throw new RecoveryWorkerException("latest_sealed_head_cas_failed");
        }
    }

    internal static void ValidateCheckpointTimeBinding(RecoveryPointManifestV1 manifest,
        DateTimeOffset checkpointVerifiedAtUtc, DateTimeOffset nowUtc)
    {
        ValidateSourceManifestTimeBinding(manifest, nowUtc);
        if (checkpointVerifiedAtUtc < manifest.CapturedAtUtc || checkpointVerifiedAtUtc < manifest.SnapshotCutoffUtc)
            throw new RecoveryWorkerException("recovery_checkpoint_precedes_snapshot");
        if (checkpointVerifiedAtUtc > nowUtc.ToUniversalTime().Add(MaximumFutureClockSkew))
            throw new RecoveryWorkerException("recovery_checkpoint_time_in_future");
    }

    internal static void ValidateSourceManifestTimeBinding(RecoveryPointManifestV1 manifest, DateTimeOffset nowUtc)
    {
        var latestAllowed = nowUtc.ToUniversalTime().Add(MaximumFutureClockSkew);
        if (manifest.CapturedAtUtc > latestAllowed || manifest.SnapshotCutoffUtc > latestAllowed)
            throw new RecoveryWorkerException("recovery_point_time_in_future");
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetStrongAsync(IAmazonDynamoDB dynamo,
        string table, Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private static Head ReadHead(Dictionary<string, AttributeValue> marker, string prefix = "latestSealed")
    {
        var fields = new[]
        {
            prefix + "PointId", prefix + "SourceManifestSha256", prefix + "SnapshotCutoffUtc", prefix + "ChangeSequence"
        };
        var present = fields.Count(marker.ContainsKey);
        if (present == 0) throw new RecoveryWorkerException("recovery_copy_missing");
        if (present != fields.Length) throw new RecoveryWorkerException("copy_head_marker_incomplete");

        var pointText = ReadString(marker, fields[0]);
        var digest = ReadString(marker, fields[1]);
        if (!Guid.TryParseExact(pointText, "D", out var pointId) || pointId == Guid.Empty ||
            pointText != pointId.ToString("D").ToLowerInvariant() || !IsHash(digest) ||
            !DateTimeOffset.TryParse(ReadString(marker, fields[2]), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var cutoff) || cutoff.Offset != TimeSpan.Zero ||
            !long.TryParse(ReadNumber(marker, fields[3]), NumberStyles.None,
                CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
            throw new RecoveryWorkerException("copy_head_marker_invalid");
        return new(pointId, digest, cutoff, sequence);
    }

    private static void ValidateMembership(Dictionary<string, AttributeValue>? item, string expectedPartition,
        string ownerId, string libraryId, Head head)
    {
        var pointText = head.PointId.ToString("D").ToLowerInvariant();
        if (item is not { Count: > 0 })
            throw new RecoveryWorkerException("latest_copy_membership_missing");
        if (ReadString(item, "pk") != expectedPartition ||
            ReadString(item, "sk") != RecoveryOwnerPointIndex.PointSortKey(head.PointId) ||
            ReadString(item, "pointId") != pointText ||
            ReadString(item, "sourceManifestSha256") != head.SourceManifestSha256 ||
            ReadBoolean(item, "bootstrapMember") is null)
            throw new RecoveryWorkerException("latest_copy_membership_binding_mismatch");

        // The owner/library pair is committed by the partition key hash; computing it
        // here also validates their bounds before any S3 artifact is read.
        if (expectedPartition != RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId))
            throw new RecoveryWorkerException("latest_copy_membership_binding_mismatch");
    }

    private static Dictionary<string, AttributeValue> CopyKey(Guid pointId) => new()
    {
        ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()),
        ["sk"] = S("COPY")
    };

    private static string ReadString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;

    private static string ReadNumber(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.N ?? string.Empty : string.Empty;

    private static bool? ReadBoolean(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.BOOL : null;

    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private sealed record Head(Guid PointId, string SourceManifestSha256, DateTimeOffset SnapshotCutoffUtc,
        long ChangeSequence);
}
