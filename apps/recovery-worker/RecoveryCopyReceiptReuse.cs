using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCopyReceiptReuse
{
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);
    private const int MaximumCheckpointBytes = 1024 * 1024;
    private const int MaximumReceiptChunkBytes = 8 * 1024 * 1024;

    public static async Task<IReadOnlyDictionary<ReceiptIdentity, PrivateCopyReceiptV1>> ReadLatestAsync(
        IAmazonDynamoDB dynamo,
        IAmazonS3 evidenceS3,
        string controlTable,
        string ownerId,
        string libraryId,
        string sourceCaptureBucket,
        string sourceContentBucket,
        string payloadBucket,
        string evidenceBucket,
        string sourceAccountId,
        string sourceRegion,
        string destinationAccountId,
        string destinationRegion,
        string workerBuildDigest,
        RecoverySigningTrust trust,
        CancellationToken cancellationToken)
    {
        var markerResponse = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = controlTable,
            Key = RecoveryPointIndexBootstrapRunner.IndexKey(ownerId, libraryId),
            ConsistentRead = true
        }, cancellationToken);
        if (markerResponse.Item is not { Count: > 0 } marker)
            throw new RecoveryWorkerException("scope_index_bootstrap_required");
        RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);

        var headFields = new[]
        {
            "latestCopiedPointId", "latestSourceManifestSha256", "latestSnapshotCutoffUtc", "latestChangeSequence"
        };
        var present = headFields.Count(marker.ContainsKey);
        if (present == 0) return new Dictionary<ReceiptIdentity, PrivateCopyReceiptV1>();
        if (present != headFields.Length)
            throw new RecoveryWorkerException("copy_head_marker_incomplete");

        var pointIdText = GetString(marker, "latestCopiedPointId");
        var sourceManifestSha256 = GetString(marker, "latestSourceManifestSha256");
        if (!Guid.TryParseExact(pointIdText, "D", out var pointId) || pointId == Guid.Empty ||
            pointIdText != pointId.ToString("D").ToLowerInvariant() || !IsHash(sourceManifestSha256) ||
            !DateTimeOffset.TryParse(GetString(marker, "latestSnapshotCutoffUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var snapshotCutoffUtc) || snapshotCutoffUtc.Offset != TimeSpan.Zero ||
            !long.TryParse(GetNumber(marker, "latestChangeSequence"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var changeSequence) || changeSequence < 0)
            throw new RecoveryWorkerException("copy_head_marker_invalid");

        var pointerResponse = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = controlTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()),
                ["sk"] = S("COPY")
            },
            ConsistentRead = true
        }, cancellationToken);
        if (pointerResponse.Item is not { Count: > 0 } pointerItem)
            throw new RecoveryWorkerException("copy_head_point_missing");
        var pointer = CopyPointer.From(pointerItem);
        if (pointer is null || pointer.PointId != pointId || pointer.State != "copied" ||
            pointer.OwnerId != ownerId || pointer.LibraryId != libraryId ||
            pointer.SourceManifestSha256 != sourceManifestSha256 ||
            pointer.SourceCaptureBucket != sourceCaptureBucket || pointer.EvidenceBucket != evidenceBucket ||
            pointer.SourceAccountId != sourceAccountId || pointer.SourceRegion != sourceRegion ||
            pointer.RecoveryAccountId != destinationAccountId || pointer.RecoveryRegion != destinationRegion)
            throw new RecoveryWorkerException("copy_head_point_binding_invalid");

        var checkpointBytes = await RecoveryVerifierRunner.ReadExactBytesAsync(evidenceS3, evidenceBucket,
            pointer.CheckpointKey, pointer.CheckpointVersionId, pointer.CheckpointSha256,
            pointer.CheckpointByteLength, MaximumCheckpointBytes, cancellationToken);
        var checkpoint = WorkerArtifactCodec.VerifyCheckpoint(checkpointBytes, trust);
        var payload = checkpoint.Payload;
        if (payload.RecoveryPointId != pointId || payload.CaptureId != pointer.CaptureId ||
            payload.OwnerId != ownerId || payload.LibraryId != libraryId ||
            payload.SourceManifestSha256 != sourceManifestSha256 || payload.DestinationAccountId != destinationAccountId ||
            payload.SourceRegion != sourceRegion || payload.DestinationRegion != destinationRegion ||
            payload.RecoveryWorkerBuildDigest != workerBuildDigest || payload.FinalManifest is null ||
            payload.OwnerAccessProof is null || payload.FinalManifestSha256 != pointer.ManifestSha256 ||
            payload.FinalManifest.DestinationBucket != evidenceBucket || payload.FinalManifest.DestinationKey != pointer.ManifestKey ||
            payload.FinalManifest.DestinationVersionId != pointer.ManifestVersionId ||
            payload.FinalManifest.Sha256 != pointer.ManifestSha256)
            throw new RecoveryWorkerException("copy_head_checkpoint_binding_invalid");

        var sourceReceipt = payload.SourceManifest;
        if (sourceReceipt.SourceBucket != sourceCaptureBucket || sourceReceipt.SourceKey != pointer.SourceManifestKey ||
            sourceReceipt.SourceVersionId != pointer.SourceManifestVersionId || sourceReceipt.Sha256 != sourceManifestSha256 ||
            sourceReceipt.DestinationBucket != evidenceBucket)
            throw new RecoveryWorkerException("copy_head_source_manifest_receipt_invalid");
        var sourceManifestBytes = await RecoveryVerifierRunner.ReadExactBytesAsync(evidenceS3, evidenceBucket,
            sourceReceipt.DestinationKey, sourceReceipt.DestinationVersionId, sourceReceipt.Sha256,
            sourceReceipt.ByteLength, 512 * 1024 * 1024, cancellationToken);
        var sourceManifest = RecoveryPointManifestV1.ParseCanonical(sourceManifestBytes);
        if (sourceManifest.RecoveryPointId != pointId || sourceManifest.CaptureId != pointer.CaptureId ||
            sourceManifest.OwnerId != ownerId || sourceManifest.LibraryId != libraryId ||
            sourceManifest.SourceRegion != sourceRegion || sourceManifest.SnapshotCutoffUtc != snapshotCutoffUtc ||
            sourceManifest.ChangeWatermark.LastSequence != changeSequence)
            throw new RecoveryWorkerException("copy_head_snapshot_binding_invalid");

        var receipts = new Dictionary<ReceiptIdentity, PrivateCopyReceiptV1>();
        var prefix = $"points/{pointId:D}/receipts/";
        foreach (var chunk in payload.ReceiptChunks)
        {
            if (!chunk.Key.StartsWith(prefix, StringComparison.Ordinal))
                throw new RecoveryWorkerException("copy_head_receipt_chunk_location_invalid");
            var bytes = await RecoveryVerifierRunner.ReadExactBytesAsync(evidenceS3, evidenceBucket,
                chunk.Key, chunk.VersionId, chunk.Sha256, chunk.ByteLength, MaximumReceiptChunkBytes,
                cancellationToken);
            var chunkReceipts = JsonSerializer.Deserialize<PrivateCopyReceiptV1[]>(bytes, ArtifactJson)
                ?? throw new RecoveryWorkerException("copy_head_receipt_chunk_invalid");
            if (chunkReceipts.Length != chunk.ReceiptCount || chunkReceipts.Any(receipt => receipt is null) ||
                !RecoveryEvidenceCodec.SerializeCanonical(chunkReceipts).AsSpan().SequenceEqual(bytes))
                throw new RecoveryWorkerException("copy_head_receipt_chunk_invalid");
            foreach (var receipt in chunkReceipts)
            {
                ValidateReceipt(receipt);
                if (receipt.SourceBucket != sourceContentBucket || receipt.DestinationBucket != payloadBucket ||
                    receipt.DataClass is not ("OriginalImage" or "XmpSidecar") ||
                    receipt.DestinationKey != "payload/sha256/" + receipt.Sha256)
                    continue;
                var identity = ReceiptIdentity.From(receipt);
                if (receipts.TryGetValue(identity, out var previous) &&
                    previous.DestinationVersionId != receipt.DestinationVersionId)
                    throw new RecoveryWorkerException("copy_head_receipt_conflict");
                receipts[identity] = receipt;
            }
        }
        var expectedMedia = sourceManifest.Objects
            .Where(item => item.DataClass is RecoveryDataClass.OriginalImage or RecoveryDataClass.XmpSidecar)
            .Select(item => ReceiptIdentity.From(sourceContentBucket, item, payloadBucket,
                "payload/sha256/" + item.Sha256))
            .ToHashSet();
        if (receipts.Keys.Any(identity => !expectedMedia.Remove(identity)) || expectedMedia.Count != 0)
            throw new RecoveryWorkerException("copy_head_media_receipt_set_invalid");
        return receipts;
    }

    public static async Task AdvanceHeadAsync(IAmazonDynamoDB dynamo, string controlTable,
        RecoveryPointManifestV1 manifest, string sourceManifestSha256, CancellationToken cancellationToken)
    {
        var cutoff = manifest.SnapshotCutoffUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var sequence = manifest.ChangeWatermark.LastSequence;
        var key = RecoveryPointIndexBootstrapRunner.IndexKey(manifest.OwnerId, manifest.LibraryId);
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = controlTable,
                Key = key,
                UpdateExpression = "SET latestCopiedPointId=:point, latestSourceManifestSha256=:manifest, latestSnapshotCutoffUtc=:cutoff, latestChangeSequence=:sequence",
                ConditionExpression = "#status=:complete AND indexVersion=:version AND (attribute_not_exists(latestSnapshotCutoffUtc) OR latestSnapshotCutoffUtc < :cutoff OR (latestSnapshotCutoffUtc=:cutoff AND latestChangeSequence < :sequence))",
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
            var response = await dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = controlTable,
                Key = key,
                ConsistentRead = true
            }, cancellationToken);
            if (response.Item is not { Count: > 0 } marker)
                throw new RecoveryWorkerException("copy_head_marker_missing");
            RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);
            if (!TryReadHead(marker, out var currentPointId, out var currentManifestSha256,
                    out var currentCutoff, out var currentSequence))
                throw new RecoveryWorkerException("copy_head_marker_incomplete");
            var comparison = currentCutoff.CompareTo(manifest.SnapshotCutoffUtc.ToUniversalTime());
            if (comparison > 0 || comparison == 0 && currentSequence > sequence) return;
            if (comparison == 0 && currentSequence == sequence && currentPointId == manifest.RecoveryPointId &&
                currentManifestSha256 == sourceManifestSha256) return;
            if (comparison == 0 && currentSequence == sequence)
                throw new RecoveryWorkerException("copy_head_snapshot_conflict");
            throw new RecoveryWorkerException("copy_head_compare_and_set_failed");
        }
    }

    private static bool TryReadHead(Dictionary<string, AttributeValue> marker, out Guid pointId,
        out string manifestSha256, out DateTimeOffset cutoff, out long sequence)
    {
        pointId = Guid.Empty;
        manifestSha256 = string.Empty;
        cutoff = default;
        sequence = -1;
        return Guid.TryParseExact(GetString(marker, "latestCopiedPointId"), "D", out pointId) &&
            pointId != Guid.Empty && IsHash(manifestSha256 = GetString(marker, "latestSourceManifestSha256")) &&
            DateTimeOffset.TryParse(GetString(marker, "latestSnapshotCutoffUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out cutoff) && cutoff.Offset == TimeSpan.Zero &&
            long.TryParse(GetNumber(marker, "latestChangeSequence"), NumberStyles.None,
                CultureInfo.InvariantCulture, out sequence) && sequence >= 0;
    }

    private static void ValidateReceipt(PrivateCopyReceiptV1 receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.SourceBucket) || string.IsNullOrWhiteSpace(receipt.SourceKey) ||
            string.IsNullOrWhiteSpace(receipt.SourceVersionId) || !IsHash(receipt.Sha256) || receipt.ByteLength < 0 ||
            string.IsNullOrWhiteSpace(receipt.DestinationBucket) || string.IsNullOrWhiteSpace(receipt.DestinationKey) ||
            string.IsNullOrWhiteSpace(receipt.DestinationVersionId) || receipt.DestinationVersionId == "null" ||
            receipt.VerifiedAtUtc.Offset != TimeSpan.Zero)
            throw new RecoveryWorkerException("copy_head_receipt_invalid");
    }

    private static string GetString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;

    private static string GetNumber(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.N ?? string.Empty : string.Empty;

    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    internal readonly record struct ReceiptIdentity(string SourceBucket, string SourceKey, string SourceVersionId,
        string Sha256, long ByteLength, string DestinationBucket, string DestinationKey, string DataClass)
    {
        public static ReceiptIdentity From(PrivateCopyReceiptV1 receipt) => new(receipt.SourceBucket,
            receipt.SourceKey, receipt.SourceVersionId, receipt.Sha256, receipt.ByteLength,
            receipt.DestinationBucket, receipt.DestinationKey, receipt.DataClass);

        public static ReceiptIdentity From(string sourceBucket, RecoveryObjectReference source,
            string destinationBucket, string destinationKey) => new(sourceBucket, source.SourceKey,
            source.SourceVersionId, source.Sha256, source.ByteLength, destinationBucket, destinationKey,
            source.DataClass.ToString());
    }
}
