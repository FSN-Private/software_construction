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

/// <summary>A sequence/hash pair that must occur as a verified prefix of the complete
/// global audit chain. It is independent of the final restored database state.</summary>
public sealed record RecoveryAuditHeadV1(long Sequence, string EventSha256)
{
    public void Validate()
    {
        if (Sequence < 0 || EventSha256 is not { Length: 64 } ||
            EventSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            (Sequence == 0 && EventSha256 != new string('0', 64)))
            throw new InvalidDataException("invalid_recovery_audit_head");
    }
}

/// <summary>The externally provisioned minimum and, when present, the most recent
/// independently verified recovery-owned head for this owner/library scope.</summary>
public sealed record RecoveryAuditHeadRequirementV1(
    RecoveryAuditHeadV1 Minimum,
    RecoveryAuditHeadV1? PreviouslyVerified,
    RecoveryAuditHeadV1? RequiredHistoricalHead = null)
{
    public void Validate()
    {
        if (Minimum is null) throw new InvalidDataException("recovery_audit_minimum_required");
        Minimum.Validate();
        PreviouslyVerified?.Validate();
        RequiredHistoricalHead?.Validate();
        if (PreviouslyVerified is { } previous &&
            (previous.Sequence < Minimum.Sequence ||
             (previous.Sequence == Minimum.Sequence && previous.EventSha256 != Minimum.EventSha256)))
            throw new InvalidDataException("recovery_audit_head_regressed");
        if (RequiredHistoricalHead is { } historical &&
            (historical.Sequence < Minimum.Sequence ||
             PreviouslyVerified is { } prior && historical.Sequence < prior.Sequence))
            throw new InvalidDataException("recovery_audit_historical_head_regressed");
    }

    public void RequireHeadsInChain(IReadOnlyDictionary<long, string> verifiedHeads)
    {
        ArgumentNullException.ThrowIfNull(verifiedHeads);
        Validate();
        if (!Contains(verifiedHeads, Minimum))
            throw new InvalidDataException("recovery_audit_minimum_not_in_chain");
        if (PreviouslyVerified is { } previous && !Contains(verifiedHeads, previous))
            throw new InvalidDataException("recovery_audit_previous_head_not_in_chain");
        if (RequiredHistoricalHead is { } historical && !Contains(verifiedHeads, historical))
            throw new InvalidDataException("recovery_audit_historical_head_not_in_chain");
    }

    private static bool Contains(IReadOnlyDictionary<long, string> chain, RecoveryAuditHeadV1 head) =>
        chain.TryGetValue(head.Sequence, out var actual) && actual == head.EventSha256;
}

/// <summary>Recovery-only signed proof that a particular immutable database snapshot
/// passed the global audit-chain check against its independent minimum and prior head.
/// This proof stays in the recovery account and is never mirrored to the primary catalog.</summary>
public sealed record RecoveryAuditHeadProofPayloadV1(
    int SchemaVersion,
    Guid RecoveryPointId,
    Guid CaptureId,
    string OwnerId,
    string LibraryId,
    string SourceManifestSha256,
    string FullManifestSha256,
    DateTimeOffset SnapshotCutoffUtc,
    string SnapshotLsn,
    RecoveryAuditHeadV1 VerifiedHead,
    RecoveryAuditHeadV1 MinimumHead,
    RecoveryAuditHeadV1? PreviouslyVerifiedHead,
    string GenesisArtifactSha256,
    DateTimeOffset VerifiedAtUtc,
    string RecoveryWorkerBuildDigest)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || RecoveryPointId == Guid.Empty || CaptureId == Guid.Empty ||
            VerifiedHead is null || MinimumHead is null)
            throw new InvalidDataException("invalid_audit_head_proof_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Hash(SourceManifestSha256, nameof(SourceManifestSha256));
        RecoveryValidation.Hash(FullManifestSha256, nameof(FullManifestSha256));
        RecoveryValidation.Utc(SnapshotCutoffUtc, nameof(SnapshotCutoffUtc));
        if (!RecoveryValidation.IsLsn(SnapshotLsn)) throw new InvalidDataException("invalid_audit_head_proof_lsn");
        VerifiedHead.Validate();
        MinimumHead.Validate();
        PreviouslyVerifiedHead?.Validate();
        var requirement = new RecoveryAuditHeadRequirementV1(MinimumHead, PreviouslyVerifiedHead);
        requirement.Validate();
        if (VerifiedHead.Sequence < MinimumHead.Sequence ||
            PreviouslyVerifiedHead is { } previous && VerifiedHead.Sequence < previous.Sequence)
            throw new InvalidDataException("audit_head_proof_precedes_required_head");
        RecoveryValidation.Hash(GenesisArtifactSha256, nameof(GenesisArtifactSha256));
        RecoveryValidation.Utc(VerifiedAtUtc, nameof(VerifiedAtUtc));
        if (VerifiedAtUtc < SnapshotCutoffUtc) throw new InvalidDataException("audit_head_proof_timestamp_invalid");
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
    }
}

public sealed record RecoveryAuditHeadProofEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public void Validate()
    {
        if (EnvelopeSchemaVersion != 1 || MessageType != "recovery_audit_head_proof")
            throw new InvalidDataException("invalid_audit_head_proof_envelope");
        RecoveryValidation.Text(WorkerId, nameof(WorkerId), 200);
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_audit_head_proof_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("audit_head_proof_signature_required");
    }
}

public sealed record VerifiedRecoveryAuditHeadProof(
    RecoveryAuditHeadProofPayloadV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

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

    public static byte[] SerializeAuditHeadProofPayload(RecoveryAuditHeadProofPayloadV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryAuditHeadProofEnvelopeV1 CreateAuditHeadProofEnvelope(
        RecoveryAuditHeadProofPayloadV1 payload, string workerId, RecoveryEvidenceSignature signature)
    {
        var bytes = SerializeAuditHeadProofPayload(payload);
        var envelope = new RecoveryAuditHeadProofEnvelopeV1(1, "recovery_audit_head_proof", workerId,
            signature.KeyId, signature.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes),
            Convert.ToBase64String(bytes), Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeAuditHeadProofEnvelope(RecoveryAuditHeadProofEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryAuditHeadProofEnvelopeV1 ParseAuditHeadProofEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryAuditHeadProofEnvelopeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RecoveryAuditHeadProofEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("audit_head_proof_envelope_empty");
        }
        catch (JsonException exception) { throw new InvalidDataException("audit_head_proof_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeAuditHeadProofEnvelope(envelope).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("audit_head_proof_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryAuditHeadProof VerifyAuditHeadProof(
        RecoveryAuditHeadProofEnvelopeV1 envelope, RecoverySigningTrust trust)
    {
        envelope.Validate();
        var payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        RecoveryAuditHeadProofPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryAuditHeadProofPayloadV1>(payloadBytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("audit_head_proof_payload_empty");
        }
        catch (JsonException exception) { throw new InvalidDataException("audit_head_proof_payload_invalid_json", exception); }
        payload.Validate();
        if (!SerializeAuditHeadProofPayload(payload).AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("audit_head_proof_payload_noncanonical");
        var signature = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, envelope.PayloadSha256,
            envelope.SignatureBase64, envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, payloadBytes, RecoveryEvidenceCodec.Hash(payloadBytes), envelope.WorkerId,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, signature);
    }

    public static void RequireAuditHeadProofBinding(RecoveryAuditHeadProofPayloadV1 proof,
        Guid recoveryPointId, Guid captureId, string ownerId, string libraryId,
        string sourceManifestSha256, string fullManifestSha256, DateTimeOffset snapshotCutoffUtc,
        string snapshotLsn, string genesisArtifactSha256, string recoveryWorkerBuildDigest)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.RecoveryPointId != recoveryPointId || proof.CaptureId != captureId ||
            proof.OwnerId != ownerId || proof.LibraryId != libraryId ||
            proof.SourceManifestSha256 != sourceManifestSha256 || proof.FullManifestSha256 != fullManifestSha256 ||
            proof.SnapshotCutoffUtc != snapshotCutoffUtc || proof.SnapshotLsn != snapshotLsn ||
            proof.GenesisArtifactSha256 != genesisArtifactSha256 ||
            proof.RecoveryWorkerBuildDigest != recoveryWorkerBuildDigest)
            throw new InvalidDataException("audit_head_proof_point_binding_invalid");
    }
}
