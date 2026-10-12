using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record VerifiedPurgeCopyPoint(
    CopyPointer Pointer,
    RecoveryPointManifestV1 SourceManifest,
    RecoveryPointManifestV1 FinalManifest,
    PrivateCopyCheckpointPayloadV1 Checkpoint,
    IReadOnlyList<PrivateCopyReceiptV1> Receipts,
    string CheckpointPayloadSha256,
    string CheckpointEnvelopeSha256);

internal sealed record VerifiedRecoveryPurgeMissingVersion(
    RecoveryPurgeTargetVersionV1 Target,
    string AdmissionRecordId,
    string AdmissionDigest,
    string ResultRecordId,
    string ResultDigest);

internal sealed record VerifiedRecoveryPurgeResumeContext(
    Guid OperationId,
    string PlanDigest,
    long Generation,
    IReadOnlyList<VerifiedRecoveryPurgeMissingVersion> MissingTargets);

/// <summary>Re-verifies purge inputs without turning a partially deleted copy into a healthy
/// recovery point. Only exact planned versions with durable admission and result receipts may be
/// absent; every remaining signed receipt is still byte-verified.</summary>
internal static class RecoveryPurgePointVerifier
{
    private const int MaximumManifestBytes = 512 * 1024 * 1024;
    private const int MaximumReceiptChunkBytes = 8 * 1024 * 1024;
    private const int MaximumReceipts = 2_000_000;
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    public static async Task<VerifiedPurgeCopyPoint> VerifyAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        CopyPointer pointer,
        RecoverySigningTrust signerTrust,
        VerifiedRecoveryPurgeResumeContext? resumeContext,
        CancellationToken cancellationToken)
    {
        var sourceAccount = configuration.RequiredAccountId("SourceAccountId");
        var recoveryAccount = configuration.RequiredAccountId("DestinationAccountId");
        var sourceRegion = configuration.RequiredRegion("SourceRegion");
        var recoveryRegion = configuration.RequiredRegion("DestinationRegion");
        if (sourceAccount == recoveryAccount || sourceRegion == recoveryRegion ||
            pointer.SourceAccountId != sourceAccount || pointer.SourceRegion != sourceRegion ||
            pointer.RecoveryAccountId != recoveryAccount || pointer.RecoveryRegion != recoveryRegion ||
            pointer.State != "copied")
            throw new RecoveryWorkerException("purge_copy_binding_invalid");

        if (resumeContext is not null && (resumeContext.OperationId == Guid.Empty ||
            !IsHash(resumeContext.PlanDigest) || resumeContext.Generation <= 0 ||
            resumeContext.MissingTargets is null || resumeContext.MissingTargets.Count > MaximumReceipts))
            throw new RecoveryWorkerException("purge_resume_context_invalid");

        var checkpointBytes = await RecoveryVerifierRunner.ReadExactBytesAsync(s3, pointer.EvidenceBucket,
            pointer.CheckpointKey, pointer.CheckpointVersionId, pointer.CheckpointSha256,
            pointer.CheckpointByteLength, 1024 * 1024, cancellationToken);
        var checkpoint = WorkerArtifactCodec.VerifyCheckpoint(checkpointBytes, signerTrust);
        var payload = checkpoint.Payload;
        if (payload.RecoveryPointId != pointer.PointId || payload.CaptureId != pointer.CaptureId ||
            payload.OwnerId != pointer.OwnerId || payload.LibraryId != pointer.LibraryId ||
            payload.DestinationAccountId != recoveryAccount || payload.SourceRegion != sourceRegion ||
            payload.DestinationRegion != recoveryRegion || payload.RecoveryWorkerBuildDigest != configuration.WorkerBuildDigest ||
            payload.FinalManifest is null || payload.OwnerAccessProof is null || payload.VerifiedAtUtc > DateTimeOffset.UtcNow)
            throw new RecoveryWorkerException("purge_checkpoint_binding_invalid");

        var sourceReceipt = payload.SourceManifest;
        if (sourceReceipt.SourceBucket != pointer.SourceCaptureBucket || sourceReceipt.SourceKey != pointer.SourceManifestKey ||
            sourceReceipt.SourceVersionId != pointer.SourceManifestVersionId || sourceReceipt.DestinationBucket != pointer.EvidenceBucket ||
            sourceReceipt.Sha256 != payload.SourceManifestSha256 || sourceReceipt.Sha256 != pointer.SourceManifestSha256)
            throw new RecoveryWorkerException("purge_source_manifest_receipt_mismatch");
        var sourceManifestBytes = await RecoveryVerifierRunner.ReadExactBytesAsync(s3, sourceReceipt.DestinationBucket,
            sourceReceipt.DestinationKey, sourceReceipt.DestinationVersionId, sourceReceipt.Sha256,
            sourceReceipt.ByteLength, MaximumManifestBytes, cancellationToken);
        var sourceManifest = RecoveryPointManifestV1.ParseCanonical(sourceManifestBytes);
        if (sourceManifest.RecoveryPointId != pointer.PointId || sourceManifest.CaptureId != pointer.CaptureId ||
            sourceManifest.OwnerId != pointer.OwnerId || sourceManifest.LibraryId != pointer.LibraryId ||
            sourceManifest.SourceRegion != sourceRegion || payload.VerifiedAtUtc < sourceManifest.CapturedAtUtc)
            throw new RecoveryWorkerException("purge_source_manifest_binding_invalid");

        var finalReceipt = payload.FinalManifest;
        if (finalReceipt.DestinationBucket != pointer.EvidenceBucket || finalReceipt.DestinationKey != pointer.ManifestKey ||
            finalReceipt.DestinationVersionId != pointer.ManifestVersionId || finalReceipt.Sha256 != pointer.ManifestSha256 ||
            payload.FinalManifestSha256 != pointer.ManifestSha256)
            throw new RecoveryWorkerException("purge_final_manifest_receipt_mismatch");
        var finalManifestBytes = await RecoveryVerifierRunner.ReadExactBytesAsync(s3, pointer.EvidenceBucket,
            pointer.ManifestKey, pointer.ManifestVersionId, pointer.ManifestSha256, finalReceipt.ByteLength,
            MaximumManifestBytes, cancellationToken);
        var finalManifest = RecoveryPointManifestV1.ParseCanonical(finalManifestBytes);
        if (finalManifest.RecoveryPointId != sourceManifest.RecoveryPointId || finalManifest.CaptureId != sourceManifest.CaptureId ||
            finalManifest.OwnerId != sourceManifest.OwnerId || finalManifest.LibraryId != sourceManifest.LibraryId ||
            finalManifest.SnapshotId != sourceManifest.SnapshotId || finalManifest.SnapshotLsn != sourceManifest.SnapshotLsn ||
            finalManifest.SnapshotCutoffUtc != sourceManifest.SnapshotCutoffUtc ||
            finalManifest.ChangeWatermark != sourceManifest.ChangeWatermark ||
            finalManifest.MigrationDigest != sourceManifest.MigrationDigest ||
            finalManifest.Components.Count != sourceManifest.Components.Count + 1 ||
            !finalManifest.Components.Any(component => component.Name == "owner-access-proof" &&
                component.DataClass == RecoveryDataClass.AccessEvidence))
            throw new RecoveryWorkerException("purge_final_manifest_binding_invalid");

        var receipts = await ReadReceiptChunksAsync(s3, pointer.EvidenceBucket, payload, cancellationToken);
        var expected = BuildExpectedReceipts(configuration, pointer, sourceManifest, finalManifest);
        RecoveryVerifierRunner.ValidateReceiptSet(receipts, expected);
        var authorizedMissing = BuildAuthorizedMissingSet(resumeContext, pointer.PointId, receipts);
        foreach (var receipt in receipts)
        {
            var identity = (pointer.PointId, receipt.DestinationBucket, receipt.DestinationKey, receipt.DestinationVersionId);
            if (authorizedMissing.Contains(identity))
            {
                await RequireExactVersionAbsentAsync(s3, receipt, cancellationToken);
                continue;
            }
            await S3ArtifactWriter.VerifyExactAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
                receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, cancellationToken);
        }

        await VerifyOwnerAccessProofAsync(configuration, s3, payload.OwnerAccessProof,
            pointer.OwnerId, pointer.LibraryId, payload.VerifiedAtUtc, cancellationToken);
        return new(pointer, sourceManifest, finalManifest, payload, receipts,
            checkpoint.PayloadSha256, checkpoint.EnvelopeSha256);
    }

    private static IReadOnlyList<PrivateCopyReceiptV1> BuildExpectedReceipts(
        RecoveryWorkerConfiguration configuration,
        CopyPointer pointer,
        RecoveryPointManifestV1 source,
        RecoveryPointManifestV1 final)
    {
        var expected = new List<PrivateCopyReceiptV1>(source.Objects.Count + source.Components.Count + 1);
        var payloadBucket = configuration.Required("DestinationBucket");
        foreach (var item in source.Objects)
            expected.Add(new(configuration.Required("SourceBucket"), item.SourceKey, item.SourceVersionId,
                item.Sha256, item.ByteLength, payloadBucket, "payload/sha256/" + item.Sha256, "",
                item.DataClass.ToString(), default));
        foreach (var component in source.Components)
        {
            var (sourceBucket, sourceKey) = ParseS3Uri(component.SourceKey);
            var isDatabase = component.DataClass == RecoveryDataClass.DatabaseSnapshot;
            expected.Add(new(sourceBucket, sourceKey, component.SourceVersionId, component.Sha256,
                component.ByteLength, isDatabase ? payloadBucket : pointer.EvidenceBucket,
                isDatabase ? $"snapshots/{source.RecoveryPointId:D}/{component.Name}" :
                    $"points/{source.RecoveryPointId:D}/components/{component.Name}", "",
                component.DataClass.ToString(), default));
        }
        var ownerProof = final.Components.SingleOrDefault(component => component.Name == "owner-access-proof")
            ?? throw new RecoveryWorkerException("purge_owner_access_component_missing");
        var (ownerBucket, ownerKey) = ParseS3Uri(ownerProof.SourceKey);
        expected.Add(new(ownerBucket, ownerKey, ownerProof.SourceVersionId, ownerProof.Sha256,
            ownerProof.ByteLength, ownerBucket, ownerKey, ownerProof.SourceVersionId,
            RecoveryDataClass.AccessEvidence.ToString(), default));
        return expected;
    }

    private static async Task<IReadOnlyList<PrivateCopyReceiptV1>> ReadReceiptChunksAsync(
        IAmazonS3 s3,
        string evidenceBucket,
        PrivateCopyCheckpointPayloadV1 payload,
        CancellationToken cancellationToken)
    {
        if (payload.CopyCount is < 0 or > MaximumReceipts)
            throw new RecoveryWorkerException("purge_receipt_count_limit");
        var receipts = new List<PrivateCopyReceiptV1>(checked((int)payload.CopyCount));
        foreach (var chunk in payload.ReceiptChunks)
        {
            var bytes = await RecoveryVerifierRunner.ReadExactBytesAsync(s3, evidenceBucket, chunk.Key,
                chunk.VersionId, chunk.Sha256, chunk.ByteLength, MaximumReceiptChunkBytes, cancellationToken);
            var parsed = JsonSerializer.Deserialize<PrivateCopyReceiptV1[]>(bytes, ArtifactJson)
                ?? throw new RecoveryWorkerException("purge_receipt_chunk_invalid");
            if (!RecoveryEvidenceCodec.SerializeCanonical(parsed).AsSpan().SequenceEqual(bytes) ||
                parsed.Length != chunk.ReceiptCount || parsed.Any(item => item is null))
                throw new RecoveryWorkerException("purge_receipt_chunk_invalid");
            receipts.AddRange(parsed);
            if (receipts.Count > MaximumReceipts) throw new RecoveryWorkerException("purge_receipt_count_limit");
        }
        if (receipts.Count != payload.CopyCount) throw new RecoveryWorkerException("purge_receipt_count_mismatch");
        return receipts;
    }

    internal static HashSet<(Guid, string, string, string)> BuildAuthorizedMissingSet(
        VerifiedRecoveryPurgeResumeContext? context,
        Guid pointId,
        IReadOnlyList<PrivateCopyReceiptV1> receipts)
    {
        var missing = new HashSet<(Guid, string, string, string)>();
        if (context is null) return missing;
        var byDestination = receipts.ToDictionary(receipt =>
            (pointId, receipt.DestinationBucket, receipt.DestinationKey, receipt.DestinationVersionId));
        foreach (var item in context.MissingTargets)
        {
            var target = item.Target;
            // A deduplicated immutable destination version can be referenced by
            // several independently signed points. Bind each point's receipt to
            // the exact journalled physical version, not only the plan's first point.
            var identity = (pointId, target.Bucket, target.Key, target.VersionId);
            if (!byDestination.ContainsKey(identity)) continue;
            var classMatches = target.Kind switch
            {
                "media" => receiptClassIsMedia(byDestination, identity),
                "database_snapshot" => byDestination.TryGetValue(identity, out var snapshotReceipt) &&
                                        snapshotReceipt.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString(),
                _ => false
            };
            if (target.Kind is not ("media" or "database_snapshot") || item.AdmissionRecordId is not { Length: > 0 } ||
                item.ResultRecordId is not { Length: > 0 } || !IsHash(item.AdmissionDigest) || !IsHash(item.ResultDigest) ||
                !byDestination.TryGetValue(identity, out var receipt) || receipt.Sha256 != target.Sha256 ||
                receipt.ByteLength != target.ByteLength || !classMatches)
                throw new RecoveryWorkerException("purge_resume_target_binding_invalid");
            if (!missing.Add(identity)) throw new RecoveryWorkerException("purge_resume_target_duplicate");
        }
        return missing;

        static bool receiptClassIsMedia(
            IReadOnlyDictionary<(Guid, string, string, string), PrivateCopyReceiptV1> byDestination,
            (Guid, string, string, string) identity) => byDestination.TryGetValue(identity, out var mediaReceipt) &&
                mediaReceipt.DataClass is "OriginalImage" or "XmpSidecar";
    }

    private static async Task RequireExactVersionAbsentAsync(IAmazonS3 s3, PrivateCopyReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = receipt.DestinationBucket,
                Key = receipt.DestinationKey,
                VersionId = receipt.DestinationVersionId
            }, cancellationToken);
            if (response.VersionId != receipt.DestinationVersionId)
                throw new RecoveryWorkerException("purge_resume_version_mismatch");
            throw new RecoveryWorkerException("purge_resume_target_still_present");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound &&
                                                   exception.ErrorCode == "NoSuchVersion")
        {
            // A purge-only absence proof is separate from recovery health verification.
        }
    }

    private static async Task VerifyOwnerAccessProofAsync(RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3, PrivateArtifactReceiptV1 receipt, string ownerId, string libraryId,
        DateTimeOffset signedCheckpointAtUtc, CancellationToken cancellationToken)
    {
        if (receipt.SourceBucket != receipt.DestinationBucket || receipt.SourceKey != receipt.DestinationKey ||
            receipt.SourceVersionId != receipt.DestinationVersionId)
            throw new RecoveryWorkerException("purge_owner_access_receipt_invalid");
        var bytes = await RecoveryVerifierRunner.ReadExactBytesAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
            receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, 64 * 1024, cancellationToken);
        var envelope = RecoveryOwnerAccessCodec.ParseEnvelope(bytes);
        var trust = new RecoverySigningTrust(configuration.Required("OwnerApprovalTrust__KeyId"),
            configuration.Required("OwnerApprovalTrust__PublicKeySpkiBase64"),
            configuration.RequiredHash("OwnerApprovalTrust__PublicKeySha256"));
        var expected = new RecoveryOwnerAccessExpectationV1(ownerId, libraryId,
            configuration.RequiredHash("ConfigurationDigest"), configuration.RequiredHash("ApiBuildDigest"),
            configuration.Required("SourceRevision"), configuration.RequiredHash("MigrationDigest"),
            configuration.WorkerBuildDigest, configuration.RequiredHash("RecoveryTemplateDigest"),
            configuration.Required("OwnerUserPoolId"), configuration.Required("OwnerUserPoolClientId"),
            configuration.Required("OwnerUserPoolIssuer"));
        VerifyHistoricalOwnerAccess(envelope, trust, expected, signedCheckpointAtUtc);
    }

    internal static void VerifyHistoricalOwnerAccess(RecoveryOwnerAccessEnvelopeV1 envelope, RecoverySigningTrust trust,
        RecoveryOwnerAccessExpectationV1 expected, DateTimeOffset signedCheckpointAtUtc)
    {
        if (signedCheckpointAtUtc.Offset != TimeSpan.Zero || signedCheckpointAtUtc > DateTimeOffset.UtcNow)
            throw new InvalidDataException("purge_checkpoint_time_invalid");
        // Old owner-access evidence proves the original sealed point only. Current
        // deletion authority still comes from the separate fresh MFA plan approval.
        _ = RecoveryOwnerAccessCodec.Verify(envelope, trust, expected, signedCheckpointAtUtc);
    }

    private static (string Bucket, string Key) ParseS3Uri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "s3" ||
            string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.AbsolutePath))
            throw new RecoveryWorkerException("purge_component_uri_invalid");
        return (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
