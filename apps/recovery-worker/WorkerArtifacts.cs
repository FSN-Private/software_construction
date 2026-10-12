using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record PrivateCopyReceiptV1(string SourceBucket, string SourceKey, string SourceVersionId,
    string Sha256, long ByteLength, string DestinationBucket, string DestinationKey,
    string DestinationVersionId, string DataClass, DateTimeOffset VerifiedAtUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? FullByteVerifiedAtUtc = null);

internal sealed record PrivateArtifactReceiptV1(string SourceBucket, string SourceKey, string SourceVersionId,
    string DestinationBucket, string DestinationKey, string DestinationVersionId, string Sha256, long ByteLength);

internal sealed record PrivateCopyCheckpointPayloadV1(int SchemaVersion, Guid CaptureId, Guid RecoveryPointId,
    string OwnerId, string LibraryId, string SourceManifestSha256, string? FinalManifestSha256,
    string SourceRegion, string DestinationRegion, string DestinationAccountId,
    DateTimeOffset VerifiedAtUtc, string RecoveryWorkerBuildDigest, long CopyCount,
    IReadOnlyList<PrivateReceiptChunkV1> ReceiptChunks, PrivateArtifactReceiptV1 SourceManifest,
    PrivateArtifactReceiptV1? OwnerAccessProof, PrivateArtifactReceiptV1? FinalManifest);

internal sealed record PrivateReceiptChunkV1(int Sequence, int ReceiptCount, string Key,
    string VersionId, string Sha256, long ByteLength);

internal sealed record PrivateSignedCheckpointV1(int SchemaVersion, string MessageType, string WorkerId,
    string SigningKeyId, string SigningPublicKeySha256, string PayloadSha256,
    string CanonicalPayloadBase64, string SignatureBase64);

internal sealed record PrivateCheckpointReceiptV1(string Key, string VersionId, string Sha256,
    long ByteLength, string PayloadSha256);

internal sealed record CaptureReleaseBindingV1(int SchemaVersion, string ApiBuildDigest,
    string IngestWorkerBuildDigest, string RecoveryWorkerBuildDigest, string RendererBuildDigest, string SourceRevision);

internal sealed record CaptureConfigurationV1(int SchemaVersion, string Environment, string SourceRevision,
    string SourceAccountId, string SourceRegion, string MigrationDigest, string ConfigurationDigest,
    string DatabaseRootCertificateSha256);

internal sealed record VerifiedPrivateCheckpointV1(
    PrivateCopyCheckpointPayloadV1 Payload,
    string EnvelopeSha256,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

internal sealed class ReceiptJournalWriter(IAmazonS3 s3, string evidenceBucket, string kmsKey,
    Guid pointId, int chunkCapacity = 512)
{
    private readonly List<PrivateCopyReceiptV1> pending = new(chunkCapacity);
    private readonly List<PrivateReceiptChunkV1> chunks = [];
    private long count;
    private bool completed;

    public async Task AppendAsync(PrivateCopyReceiptV1 receipt, CancellationToken cancellationToken)
    {
        if (completed) throw new InvalidOperationException("receipt_journal_already_completed");
        if (receipt is null) throw new InvalidDataException("copy_receipt_required");
        pending.Add(receipt);
        count = checked(count + 1);
        if (pending.Count >= chunkCapacity) await FlushAsync(cancellationToken);
    }

    public async Task<(long Count, IReadOnlyList<PrivateReceiptChunkV1> Chunks)> CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (!completed)
        {
            await FlushAsync(cancellationToken);
            completed = true;
        }
        return (count, chunks.ToArray());
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (pending.Count == 0) return;
        var sequence = chunks.Count;
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(pending.ToArray());
        var hash = RecoveryEvidenceCodec.Hash(bytes);
        var key = $"points/{pointId:D}/receipts/{sequence:D8}-{hash}.json";
        var published = await S3ArtifactWriter.PutBytesAsync(s3, evidenceBucket, key, bytes, kmsKey, cancellationToken);
        chunks.Add(new(sequence, pending.Count, published.Key, published.VersionId, published.Sha256, published.ByteLength));
        pending.Clear();
    }
}

internal static class WorkerArtifactCodec
{
    public static VerifiedPrivateCheckpointV1 VerifyCheckpoint(
        ReadOnlySpan<byte> envelopeBytes,
        RecoverySigningTrust trust)
    {
        PrivateSignedCheckpointV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PrivateSignedCheckpointV1>(envelopeBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("checkpoint_envelope_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("checkpoint_envelope_invalid_json", exception);
        }
        if (envelope.SchemaVersion != 1 || envelope.MessageType != "recovery_copy_checkpoint" ||
            string.IsNullOrWhiteSpace(envelope.WorkerId))
            throw new InvalidDataException("checkpoint_envelope_invalid");

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("checkpoint_payload_encoding_invalid", exception);
        }
        var payloadSha256 = RecoveryEvidenceCodec.Hash(payloadBytes);
        if (payloadSha256 != envelope.PayloadSha256)
            throw new InvalidDataException("checkpoint_payload_digest_mismatch");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("checkpoint_signature_encoding_invalid", exception);
        }
        _ = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, payloadSha256,
            envelope.SignatureBase64, envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);

        PrivateCopyCheckpointPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<PrivateCopyCheckpointPayloadV1>(payloadBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("checkpoint_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("checkpoint_payload_invalid_json", exception);
        }
        var canonical = RecoveryEvidenceCodec.SerializeCanonical(payload);
        if (!canonical.AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("checkpoint_payload_noncanonical");
        ValidatePayload(payload);

        return new(payload, RecoveryEvidenceCodec.Hash(envelopeBytes), payloadSha256,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, signature);
    }

    private static void ValidatePayload(PrivateCopyCheckpointPayloadV1 payload)
    {
        if (payload.SchemaVersion != 1 || payload.CaptureId == Guid.Empty || payload.RecoveryPointId == Guid.Empty)
            throw new InvalidDataException("checkpoint_payload_identity_invalid");
        RequireText(payload.OwnerId, nameof(payload.OwnerId), 200);
        RequireText(payload.LibraryId, nameof(payload.LibraryId), 200);
        RequireHash(payload.SourceManifestSha256, nameof(payload.SourceManifestSha256));
        if (payload.FinalManifestSha256 is not null)
            RequireHash(payload.FinalManifestSha256, nameof(payload.FinalManifestSha256));
        RequireRegion(payload.SourceRegion, nameof(payload.SourceRegion));
        RequireRegion(payload.DestinationRegion, nameof(payload.DestinationRegion));
        if (payload.DestinationRegion == payload.SourceRegion)
            throw new InvalidDataException("checkpoint_region_not_independent");
        RequireText(payload.DestinationAccountId, nameof(payload.DestinationAccountId), 12);
        if (payload.DestinationAccountId.Length != 12 || payload.DestinationAccountId.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("checkpoint_account_invalid");
        RequireUtc(payload.VerifiedAtUtc, nameof(payload.VerifiedAtUtc));
        RequireHash(payload.RecoveryWorkerBuildDigest, nameof(payload.RecoveryWorkerBuildDigest));
        if (payload.CopyCount is < 0 or > 2_000_000) throw new InvalidDataException("checkpoint_receipt_count_limit");
        if (payload.ReceiptChunks is null || payload.SourceManifest is null)
            throw new InvalidDataException("checkpoint_artifacts_required");
        long receiptCount = 0;
        for (var index = 0; index < payload.ReceiptChunks.Count; index++)
        {
            var chunk = payload.ReceiptChunks[index];
            if (chunk is null || chunk.Sequence != index || chunk.ReceiptCount <= 0 ||
                string.IsNullOrWhiteSpace(chunk.Key) || string.IsNullOrWhiteSpace(chunk.VersionId) || chunk.ByteLength <= 0)
                throw new InvalidDataException("checkpoint_receipt_chunk_invalid");
            RequireHash(chunk.Sha256, nameof(chunk.Sha256));
            receiptCount = checked(receiptCount + chunk.ReceiptCount);
        }
        if (receiptCount != payload.CopyCount) throw new InvalidDataException("checkpoint_receipt_count_mismatch");
        ValidateArtifactReceipt(payload.SourceManifest);
        if (payload.OwnerAccessProof is not null) ValidateArtifactReceipt(payload.OwnerAccessProof);
        if (payload.FinalManifest is not null) ValidateArtifactReceipt(payload.FinalManifest);
    }

    private static void ValidateArtifactReceipt(PrivateArtifactReceiptV1 receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.SourceBucket) || string.IsNullOrWhiteSpace(receipt.SourceKey) ||
            string.IsNullOrWhiteSpace(receipt.SourceVersionId) || string.IsNullOrWhiteSpace(receipt.DestinationBucket) ||
            string.IsNullOrWhiteSpace(receipt.DestinationKey) || string.IsNullOrWhiteSpace(receipt.DestinationVersionId) ||
            receipt.ByteLength < 0)
            throw new InvalidDataException("checkpoint_artifact_receipt_invalid");
        RequireHash(receipt.Sha256, nameof(receipt.Sha256));
    }

    private static void RequireText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new InvalidDataException($"checkpoint_{name.ToLowerInvariant()}_invalid");
    }

    private static void RequireHash(string value, string name)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"checkpoint_{name.ToLowerInvariant()}_invalid");
    }

    private static void RequireRegion(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('-').Length < 3 ||
            value.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new InvalidDataException($"checkpoint_{name.ToLowerInvariant()}_invalid");
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"checkpoint_{name.ToLowerInvariant()}_not_utc");
    }
}
