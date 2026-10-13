using System.Text.Json;

namespace Platform.Recovery;

public sealed record RecoverySnapshotPreservationProofV1(int SchemaVersion, Guid PointId, Guid CaptureId,
    string OwnerId, string LibraryId, string CheckpointSha256, string SourceManifestSha256,
    string Bucket, string Key, string VersionId, string Sha256, long ByteLength, string MigrationDigest,
    string SchemaInventorySha256, string ValidationSha256, DateTimeOffset VerifiedAtUtc,
    string VerifierId, string WorkerBuildDigest);

public sealed record RecoverySnapshotPreservationEnvelopeV1(int SchemaVersion, string MessageType,
    string SigningKeyId, string SigningPublicKeySha256, string PayloadSha256,
    string CanonicalPayloadBase64, string SignatureBase64);

public static class RecoverySnapshotPreservationCodec
{
    public static RecoverySnapshotPreservationEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 64 * 1024) throw new InvalidDataException("snapshot_proof_size_invalid");
        return ParseCanonical<RecoverySnapshotPreservationEnvelopeV1>(bytes);
    }

    public static RecoverySnapshotPreservationProofV1 Verify(RecoverySnapshotPreservationEnvelopeV1 envelope,
        RecoverySigningTrust trust, string workerBuildDigest, DateTimeOffset nowUtc)
    {
        if (envelope.SchemaVersion != 1 || envelope.MessageType != "recovery_snapshot_media_free" ||
            RecoveryEvidenceCodec.SerializeCanonical(envelope).Length > 64 * 1024)
            throw new InvalidDataException("snapshot_proof_envelope_invalid");
        RecoveryValidation.Hash(workerBuildDigest, nameof(workerBuildDigest));
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64); }
        catch (Exception error) when (error is FormatException or ArgumentNullException)
        { throw new InvalidDataException("snapshot_proof_encoding_invalid", error); }
        _ = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        var proof = ParseCanonical<RecoverySnapshotPreservationProofV1>(bytes);
        if (proof.SchemaVersion != 1 || proof.PointId == Guid.Empty || proof.CaptureId == Guid.Empty ||
            proof.VersionId == "null" || proof.ByteLength <= 0 || proof.ByteLength > 95L * 1024 * 1024 * 1024 ||
            proof.WorkerBuildDigest != workerBuildDigest)
            throw new InvalidDataException("snapshot_proof_identity_invalid");
        foreach (var value in new[] { proof.OwnerId, proof.LibraryId, proof.VerifierId })
            RecoveryValidation.Text(value, nameof(proof), 200);
        RecoveryValidation.Text(proof.Bucket, nameof(proof.Bucket), 255);
        RecoveryValidation.Text(proof.Key, nameof(proof.Key), 1024);
        RecoveryValidation.Text(proof.VersionId, nameof(proof.VersionId), 1024);
        foreach (var value in new[] { proof.CheckpointSha256, proof.SourceManifestSha256, proof.Sha256,
                     proof.MigrationDigest, proof.SchemaInventorySha256, proof.ValidationSha256 })
            RecoveryValidation.Hash(value, nameof(proof));
        RecoveryValidation.Utc(proof.VerifiedAtUtc, nameof(proof.VerifiedAtUtc));
        if (proof.VerifiedAtUtc > nowUtc) throw new InvalidDataException("snapshot_proof_time_invalid");
        return proof;
    }

    private static T ParseCanonical<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("snapshot_proof_empty");
            if (!RecoveryEvidenceCodec.SerializeCanonical(value).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("snapshot_proof_noncanonical");
            return value;
        }
        catch (JsonException error) { throw new InvalidDataException("snapshot_proof_json_invalid", error); }
    }
}
