using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

public sealed record RecoveryAuditCheckpointPayloadV1(
    int SchemaVersion,
    long Sequence,
    Guid CheckpointId,
    string PreviousCheckpointSha256,
    DateTimeOffset CreatedAtUtc,
    Guid QualificationId,
    Guid RunId,
    string QualificationEnvelopeSha256,
    string QualificationPayloadSha256,
    Guid RecoveryPointId,
    string FullManifestSha256,
    string CopyCheckpointSha256)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Sequence <= 0 || CheckpointId == Guid.Empty ||
            QualificationId == Guid.Empty || RunId == Guid.Empty || RecoveryPointId == Guid.Empty)
            throw new InvalidDataException("invalid_audit_checkpoint_identity");
        RecoveryValidation.Hash(PreviousCheckpointSha256, nameof(PreviousCheckpointSha256));
        RecoveryValidation.Utc(CreatedAtUtc, nameof(CreatedAtUtc));
        RecoveryValidation.Hash(QualificationEnvelopeSha256, nameof(QualificationEnvelopeSha256));
        RecoveryValidation.Hash(QualificationPayloadSha256, nameof(QualificationPayloadSha256));
        RecoveryValidation.Hash(FullManifestSha256, nameof(FullManifestSha256));
        RecoveryValidation.Hash(CopyCheckpointSha256, nameof(CopyCheckpointSha256));
    }
}

public sealed record RecoveryAuditCheckpointEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_audit_checkpoint")
            throw new InvalidDataException("invalid_audit_checkpoint_envelope");
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try
        {
            _ = Convert.FromBase64String(CanonicalPayloadBase64);
            _ = Convert.FromBase64String(SignatureBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("invalid_audit_checkpoint_signature_encoding", exception);
        }
        if (string.IsNullOrWhiteSpace(SignatureBase64))
            throw new InvalidDataException("audit_checkpoint_signature_required");
    }
}

public sealed record RecoveryAuditCheckpointArtifactV1(
    int SchemaVersion,
    RecoveryAuditCheckpointEnvelopeV1 Checkpoint)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Checkpoint is null)
            throw new InvalidDataException("invalid_audit_checkpoint_artifact");
        Checkpoint.Validate();
    }
}

/// <summary>Operator-provisioned offline trust and monotonic head. The verifier never
/// accepts a public key or chain head selected by the release artifact itself.</summary>
public sealed record RecoveryOfflineAuditAnchorV1(
    int SchemaVersion,
    string SigningKeyId,
    string PublicKeySpkiBase64,
    string PublicKeySha256,
    long PreviousSequence,
    string PreviousCheckpointSha256,
    long HeadSequence,
    string HeadCheckpointSha256,
    DateTimeOffset AnchoredAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || PreviousSequence < 0 || HeadSequence != PreviousSequence + 1)
            throw new InvalidDataException("invalid_offline_audit_anchor");
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(PublicKeySha256, nameof(PublicKeySha256));
        RecoveryValidation.Hash(PreviousCheckpointSha256, nameof(PreviousCheckpointSha256));
        RecoveryValidation.Hash(HeadCheckpointSha256, nameof(HeadCheckpointSha256));
        RecoveryValidation.Utc(AnchoredAtUtc, nameof(AnchoredAtUtc));
        _ = new RecoverySigningTrust(SigningKeyId, PublicKeySpkiBase64, PublicKeySha256).ValidateAndGetPublicKey();
    }

    public RecoverySigningTrust SigningTrust() => new(SigningKeyId, PublicKeySpkiBase64, PublicKeySha256);
}

public sealed record VerifiedRecoveryAuditCheckpoint(
    RecoveryAuditCheckpointPayloadV1 Payload,
    byte[] CanonicalPayloadBytes,
    string EnvelopeSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryAuditCheckpointCodec
{
    public static byte[] SerializeAnchor(RecoveryOfflineAuditAnchorV1 anchor)
    {
        anchor.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(anchor);
    }

    public static RecoveryOfflineAuditAnchorV1 ParseAnchor(ReadOnlySpan<byte> bytes)
    {
        RecoveryOfflineAuditAnchorV1 anchor;
        try
        {
            anchor = JsonSerializer.Deserialize<RecoveryOfflineAuditAnchorV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("offline_audit_anchor_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("offline_audit_anchor_invalid_json", exception);
        }
        anchor.Validate();
        if (!SerializeAnchor(anchor).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("offline_audit_anchor_noncanonical");
        return anchor;
    }

    public static byte[] SerializePayload(RecoveryAuditCheckpointPayloadV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryAuditCheckpointPayloadV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryAuditCheckpointPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryAuditCheckpointPayloadV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("audit_checkpoint_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("audit_checkpoint_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!SerializePayload(payload).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("audit_checkpoint_payload_noncanonical");
        return payload;
    }

    public static RecoveryAuditCheckpointEnvelopeV1 CreateEnvelope(
        RecoveryAuditCheckpointPayloadV1 payload,
        RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryAuditCheckpointEnvelopeV1(1, "recovery_audit_checkpoint", signature.KeyId,
            signature.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes),
            Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeArtifact(RecoveryAuditCheckpointEnvelopeV1 checkpoint)
    {
        var artifact = new RecoveryAuditCheckpointArtifactV1(1, checkpoint);
        artifact.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(artifact);
    }

    public static RecoveryAuditCheckpointArtifactV1 ParseArtifact(ReadOnlySpan<byte> bytes)
    {
        RecoveryAuditCheckpointArtifactV1 artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<RecoveryAuditCheckpointArtifactV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("audit_checkpoint_artifact_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("audit_checkpoint_artifact_invalid_json", exception);
        }
        artifact.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(artifact).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("audit_checkpoint_artifact_noncanonical");
        return artifact;
    }

    public static VerifiedRecoveryAuditCheckpoint Verify(
        RecoveryAuditCheckpointEnvelopeV1 envelope,
        RecoverySigningTrust trust)
    {
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParsePayload(bytes);
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256,
            envelope.SignatureBase64, envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, RecoveryEvidenceCodec.Hash(SerializeArtifact(envelope)),
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, signature);
    }

    public static void RequireQualificationBinding(
        VerifiedRecoveryAuditCheckpoint checkpoint,
        RecoveryOfflineAuditAnchorV1 anchor,
        VerifiedRecoveryQualification qualification,
        string qualificationEnvelopeSha256,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(qualification);
        anchor.Validate();
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var payload = checkpoint.Payload;
        var qualified = qualification.Payload;
        if (checkpoint.SigningKeyId != anchor.SigningKeyId ||
            checkpoint.SigningPublicKeySha256 != anchor.PublicKeySha256 ||
            payload.Sequence != anchor.HeadSequence ||
            payload.PreviousCheckpointSha256 != anchor.PreviousCheckpointSha256 ||
            checkpoint.EnvelopeSha256 != anchor.HeadCheckpointSha256 ||
            anchor.AnchoredAtUtc < payload.CreatedAtUtc || anchor.AnchoredAtUtc > nowUtc.AddMinutes(5) ||
            payload.CreatedAtUtc > nowUtc.AddMinutes(5) || payload.CreatedAtUtc < qualified.QualifiedAtUtc ||
            payload.QualificationId != qualified.QualificationId || payload.RunId != qualified.RunId ||
            payload.RecoveryPointId != qualified.Point.PointId ||
            payload.QualificationEnvelopeSha256 != qualificationEnvelopeSha256 ||
            payload.QualificationPayloadSha256 != qualification.PayloadSha256 ||
            payload.FullManifestSha256 != qualified.Point.ManifestSha256 ||
            payload.CopyCheckpointSha256 != qualified.Point.CheckpointSha256)
            throw new InvalidDataException("offline_audit_checkpoint_binding_mismatch");
    }
}
