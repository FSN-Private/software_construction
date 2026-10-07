using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

/// <summary>Canonical payload stored in recovery_audit_events.canonical_event.</summary>
public sealed record RecoveryAuditEventPayloadV1(
    int SchemaVersion,
    string OperationId,
    long Sequence,
    Guid EventId,
    string OwnerId,
    string LibraryId,
    string EventType,
    string Outcome,
    string? SafeResourceId,
    DateTimeOffset OccurredAtUtc,
    string PreviousSha256)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Sequence <= 0 || EventId == Guid.Empty)
            throw new InvalidDataException("invalid_recovery_audit_event_identity");
        RecoveryValidation.Text(OperationId, nameof(OperationId), 200);
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(EventType, nameof(EventType), 200);
        RecoveryValidation.Text(Outcome, nameof(Outcome), 100);
        if (SafeResourceId is not null) RecoveryValidation.Text(SafeResourceId, nameof(SafeResourceId), 200);
        RecoveryValidation.Utc(OccurredAtUtc, nameof(OccurredAtUtc));
        RecoveryValidation.Hash(PreviousSha256, nameof(PreviousSha256));
    }
}

public sealed record RecoveryAuditGenesisPayloadV1(
    int SchemaVersion,
    long Sequence,
    string HeadEventSha256,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Sequence != 0 || HeadEventSha256 != new string('0', 64))
            throw new InvalidDataException("invalid_recovery_audit_genesis");
        RecoveryValidation.Utc(CreatedAtUtc, nameof(CreatedAtUtc));
    }
}

public sealed record RecoveryAuditGenesisEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_audit_genesis")
            throw new InvalidDataException("invalid_recovery_audit_genesis_envelope");
        RecoveryValidation.Text(WorkerId, nameof(WorkerId), 200);
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("invalid_recovery_audit_genesis_signature_encoding", exception);
        }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("recovery_audit_genesis_signature_required");
    }
}

public sealed record RecoveryAuditGenesisArtifactV1(int SchemaVersion, RecoveryAuditGenesisEnvelopeV1 Envelope)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Envelope is null)
            throw new InvalidDataException("invalid_recovery_audit_genesis_artifact");
        Envelope.Validate();
    }
}

public sealed record VerifiedRecoveryAuditGenesis(
    RecoveryAuditGenesisPayloadV1 Payload,
    string ArtifactSha256,
    string SigningKeyId,
    string SigningPublicKeySha256);

public sealed record RecoveryAuditLedgerCheckpointPayloadV1(
    int SchemaVersion,
    Guid CheckpointId,
    long Sequence,
    string EventSha256,
    string SignerKeyId,
    string RecoveryAccount,
    DateTimeOffset CreatedAtUtc,
    string OfflinePinId)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || CheckpointId == Guid.Empty || Sequence <= 0)
            throw new InvalidDataException("invalid_recovery_audit_ledger_checkpoint");
        RecoveryValidation.Hash(EventSha256, nameof(EventSha256));
        RecoveryValidation.Text(SignerKeyId, nameof(SignerKeyId), 512);
        RecoveryValidation.Text(RecoveryAccount, nameof(RecoveryAccount), 12);
        RecoveryValidation.Utc(CreatedAtUtc, nameof(CreatedAtUtc));
        RecoveryValidation.Text(OfflinePinId, nameof(OfflinePinId), 512);
    }
}

public static class RecoveryAuditLedgerCodec
{
    public static DateTimeOffset NormalizePostgresTimestamp(DateTimeOffset value)
    {
        RecoveryValidation.Utc(value, nameof(value));
        return new DateTimeOffset(value.ToUniversalTime().Ticks / 10 * 10, TimeSpan.Zero);
    }

    public static byte[] SerializeEvent(RecoveryAuditEventPayloadV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryAuditEventPayloadV1 ParseEvent(ReadOnlySpan<byte> bytes)
    {
        RecoveryAuditEventPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryAuditEventPayloadV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("recovery_audit_event_empty");
        }
        catch (JsonException exception) { throw new InvalidDataException("recovery_audit_event_invalid_json", exception); }
        payload.Validate();
        if (!SerializeEvent(payload).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("recovery_audit_event_noncanonical");
        return payload;
    }

    public static byte[] SerializeGenesisPayload(RecoveryAuditGenesisPayloadV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryAuditGenesisEnvelopeV1 CreateGenesisEnvelope(
        RecoveryAuditGenesisPayloadV1 payload, string workerId, RecoveryEvidenceSignature signature)
    {
        var bytes = SerializeGenesisPayload(payload);
        var envelope = new RecoveryAuditGenesisEnvelopeV1(1, "recovery_audit_genesis", workerId,
            signature.KeyId, signature.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes),
            Convert.ToBase64String(bytes), Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeGenesisArtifact(RecoveryAuditGenesisEnvelopeV1 envelope)
    {
        var artifact = new RecoveryAuditGenesisArtifactV1(1, envelope);
        artifact.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(artifact);
    }

    public static VerifiedRecoveryAuditGenesis VerifyGenesisArtifact(
        ReadOnlySpan<byte> bytes, RecoverySigningTrust trust, DateTimeOffset nowUtc)
    {
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        RecoveryAuditGenesisArtifactV1 artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<RecoveryAuditGenesisArtifactV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("recovery_audit_genesis_artifact_empty");
        }
        catch (JsonException exception) { throw new InvalidDataException("recovery_audit_genesis_artifact_invalid_json", exception); }
        artifact.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(artifact).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("recovery_audit_genesis_artifact_noncanonical");
        var payloadBytes = Convert.FromBase64String(artifact.Envelope.CanonicalPayloadBase64);
        RecoveryAuditGenesisPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryAuditGenesisPayloadV1>(payloadBytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("recovery_audit_genesis_payload_empty");
        }
        catch (JsonException exception) { throw new InvalidDataException("recovery_audit_genesis_payload_invalid_json", exception); }
        payload.Validate();
        if (!SerializeGenesisPayload(payload).AsSpan().SequenceEqual(payloadBytes) || payload.CreatedAtUtc > nowUtc.AddMinutes(5))
            throw new InvalidDataException("recovery_audit_genesis_payload_invalid");
        _ = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, artifact.Envelope.PayloadSha256,
            artifact.Envelope.SignatureBase64, artifact.Envelope.SigningKeyId,
            artifact.Envelope.SigningPublicKeySha256, trust);
        return new(payload, RecoveryEvidenceCodec.Hash(bytes), artifact.Envelope.SigningKeyId,
            artifact.Envelope.SigningPublicKeySha256);
    }

    public static byte[] SerializeLedgerCheckpoint(RecoveryAuditLedgerCheckpointPayloadV1 payload)
    {
        var normalized = payload with { CreatedAtUtc = NormalizePostgresTimestamp(payload.CreatedAtUtc) };
        normalized.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(normalized);
    }
}
