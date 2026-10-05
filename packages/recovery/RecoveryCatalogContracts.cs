using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

/// <summary>Compact, safe-to-mirror class coverage. It contains no object keys, versions,
/// filenames, logical paths, or per-object identities.</summary>
public sealed record RecoveryCatalogCoverageV1(
    string DataClass,
    long ExpectedItemCount,
    long ObservedItemCount,
    long ExpectedByteLength,
    long ObservedByteLength,
    string Outcome,
    IReadOnlyList<string> SafeCodes)
{
    public void Validate()
    {
        if (SafeCodes is null || SafeCodes.Any(code => code is null))
            throw new InvalidDataException("invalid_catalog_safe_codes");
        if (!RecoveryEvidenceCodec.ObservationClasses.Contains(DataClass, StringComparer.Ordinal))
            throw new InvalidDataException("invalid_catalog_class");
        if (!RecoveryEvidenceCodec.ObservationOutcomes.Contains(Outcome, StringComparer.Ordinal))
            throw new InvalidDataException("invalid_catalog_outcome");
        if (ExpectedItemCount < 0 || ObservedItemCount < 0 || ExpectedByteLength < 0 || ObservedByteLength < 0)
            throw new InvalidDataException("invalid_catalog_coverage");
        RecoveryEvidenceCodec.ValidateSafeCodes(SafeCodes);
        if (Outcome == "healthy" && (ExpectedItemCount != ObservedItemCount ||
            ExpectedByteLength != ObservedByteLength || SafeCodes.Count != 0))
            throw new InvalidDataException("healthy_catalog_coverage_mismatch");
    }
}

/// <summary>A bounded point assertion sent to the primary status service. Full manifests,
/// receipts and exact object locators remain in recovery-owned private storage.</summary>
public sealed record RecoveryCatalogPayloadV1(
    int SchemaVersion,
    Guid RecoveryPointId,
    Guid CaptureId,
    string OwnerId,
    string LibraryId,
    string SourceAccountId,
    string SourceRegion,
    string RecoveryAccountId,
    string RecoveryRegion,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset SnapshotCutoffUtc,
    string SnapshotId,
    string SnapshotLsn,
    long ChangeSequence,
    DateTimeOffset? LatestChangedAtUtc,
    string MigrationDigest,
    string ApiBuildDigest,
    string IngestWorkerBuildDigest,
    string RecoveryWorkerBuildDigest,
    string RendererBuildDigest,
    string RecoveryTemplateDigest,
    string FullManifestSha256,
    string CheckpointSha256,
    DateTimeOffset VerifiedAtUtc,
    string VerifierBuild,
    IReadOnlyList<RecoveryCatalogCoverageV1> Coverage)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || RecoveryPointId == Guid.Empty || CaptureId == Guid.Empty)
            throw new InvalidDataException("invalid_catalog_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        ValidateAccountId(SourceAccountId);
        ValidateAccountId(RecoveryAccountId);
        RecoveryValidation.Region(SourceRegion);
        RecoveryValidation.Region(RecoveryRegion);
        if (SourceAccountId == RecoveryAccountId || SourceRegion == RecoveryRegion)
            throw new InvalidDataException("recovery_environment_not_independent");
        RecoveryValidation.Utc(CapturedAtUtc, nameof(CapturedAtUtc));
        RecoveryValidation.Utc(SnapshotCutoffUtc, nameof(SnapshotCutoffUtc));
        RecoveryValidation.Utc(VerifiedAtUtc, nameof(VerifiedAtUtc));
        RecoveryValidation.Text(SnapshotId, nameof(SnapshotId), 128);
        if (SnapshotCutoffUtc > CapturedAtUtc || VerifiedAtUtc < SnapshotCutoffUtc)
            throw new InvalidDataException("invalid_catalog_timestamps");
        if (!RecoveryValidation.IsLsn(SnapshotLsn) || ChangeSequence < 0 ||
            ((ChangeSequence == 0) != (LatestChangedAtUtc is null)))
            throw new InvalidDataException("invalid_catalog_watermark");
        if (LatestChangedAtUtc is { } latest)
        {
            RecoveryValidation.Utc(latest, nameof(LatestChangedAtUtc));
            if (latest > SnapshotCutoffUtc) throw new InvalidDataException("catalog_change_after_cutoff");
        }
        RecoveryValidation.Hash(MigrationDigest, nameof(MigrationDigest));
        RecoveryValidation.Hash(ApiBuildDigest, nameof(ApiBuildDigest));
        RecoveryValidation.Hash(IngestWorkerBuildDigest, nameof(IngestWorkerBuildDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Hash(RendererBuildDigest, nameof(RendererBuildDigest));
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Hash(FullManifestSha256, nameof(FullManifestSha256));
        RecoveryValidation.Hash(CheckpointSha256, nameof(CheckpointSha256));
        RecoveryValidation.Text(VerifierBuild, nameof(VerifierBuild), 200);
        if (Coverage is null || Coverage.Count != RecoveryEvidenceCodec.ObservationClasses.Length || Coverage.Any(item => item is null))
            throw new InvalidDataException("catalog_coverage_incomplete");
        foreach (var item in Coverage) item.Validate();
        if (Coverage.Select(item => item.DataClass).Distinct(StringComparer.Ordinal).Count() != Coverage.Count ||
            RecoveryEvidenceCodec.ObservationClasses.Except(Coverage.Select(item => item.DataClass), StringComparer.Ordinal).Any())
            throw new InvalidDataException("catalog_coverage_incomplete");
        if (!RecoveryEvidenceCodec.ObservationClasses.SequenceEqual(Coverage.Select(item => item.DataClass), StringComparer.Ordinal))
            throw new InvalidDataException("catalog_coverage_noncanonical_order");
        if (Coverage.Any(item => item.Outcome != "healthy" || item.ExpectedItemCount != item.ObservedItemCount ||
            item.ExpectedByteLength != item.ObservedByteLength))
            throw new InvalidDataException("unverified_catalog_coverage");
    }

    private static void ValidateAccountId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 12 || value.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("invalid_catalog_account_id");
    }
}

public sealed record RecoveryCatalogEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string SafeCatalogPayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_point")
            throw new InvalidDataException("invalid_recovery_catalog_envelope");
        RecoveryValidation.Text(WorkerId, nameof(WorkerId), 200);
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(SafeCatalogPayloadSha256, nameof(SafeCatalogPayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException) { throw new InvalidDataException("invalid_catalog_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("catalog_signature_required");
    }
}

public sealed record VerifiedRecoveryCatalog(
    RecoveryCatalogPayloadV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryCatalogCodec
{
    public static byte[] SerializePayload(RecoveryCatalogPayloadV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryCatalogPayloadV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryCatalogPayloadV1 payload;
        try { payload = JsonSerializer.Deserialize<RecoveryCatalogPayloadV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("catalog_payload_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("catalog_payload_invalid_json", exception); }
        payload.Validate();
        if (!SerializePayload(payload).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("catalog_payload_noncanonical");
        return payload;
    }

    public static RecoveryCatalogEnvelopeV1 CreateEnvelope(
        RecoveryCatalogPayloadV1 payload,
        string workerId,
        RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryCatalogEnvelopeV1(1, "recovery_point", workerId, signature.KeyId,
            signature.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes),
            Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeEnvelope(RecoveryCatalogEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryCatalogEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryCatalogEnvelopeV1 envelope;
        try { envelope = JsonSerializer.Deserialize<RecoveryCatalogEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("catalog_envelope_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("catalog_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("catalog_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryCatalog Verify(RecoveryCatalogEnvelopeV1 envelope, RecoverySigningTrust trust)
    {
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParsePayload(bytes);
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.SafeCatalogPayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, RecoveryEvidenceCodec.Hash(bytes), envelope.WorkerId, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}

public sealed record RecoveryCycleEventV1(
    int SchemaVersion,
    Guid EventId,
    Guid? CaptureId,
    Guid? RecoveryPointId,
    string OwnerId,
    string LibraryId,
    DateTimeOffset ObservedAtUtc,
    string Stage,
    string? FailedDataClass,
    string Outcome,
    string SafeCode,
    string RecoveryWorkerBuildDigest)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly string[] Stages = ["capture", "copy", "verification", "publication"];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || EventId == Guid.Empty || CaptureId == Guid.Empty || RecoveryPointId == Guid.Empty)
            throw new InvalidDataException("invalid_recovery_cycle_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Utc(ObservedAtUtc, nameof(ObservedAtUtc));
        if (!Stages.Contains(Stage, StringComparer.Ordinal)) throw new InvalidDataException("invalid_recovery_cycle_stage");
        if (FailedDataClass is not null && !RecoveryEvidenceCodec.ObservationClasses.Contains(FailedDataClass, StringComparer.Ordinal))
            throw new InvalidDataException("invalid_recovery_cycle_class");
        if (!RecoveryEvidenceCodec.ObservationOutcomes.Contains(Outcome, StringComparer.Ordinal) || Outcome == "healthy")
            throw new InvalidDataException("invalid_recovery_cycle_outcome");
        RecoveryEvidenceCodec.ValidateSafeCodes([SafeCode]);
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
    }
}

public sealed record RecoveryCycleEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string SafeCyclePayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_cycle")
            throw new InvalidDataException("invalid_recovery_cycle_envelope");
        RecoveryValidation.Text(WorkerId, nameof(WorkerId), 200);
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(SafeCyclePayloadSha256, nameof(SafeCyclePayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_recovery_cycle_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("recovery_cycle_signature_required");
    }
}

public sealed record VerifiedRecoveryCycle(
    RecoveryCycleEventV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryCycleCodec
{
    public static byte[] SerializePayload(RecoveryCycleEventV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryCycleEventV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryCycleEventV1 payload;
        try { payload = JsonSerializer.Deserialize<RecoveryCycleEventV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("recovery_cycle_payload_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("recovery_cycle_payload_invalid_json", exception); }
        payload.Validate();
        if (!SerializePayload(payload).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("recovery_cycle_payload_noncanonical");
        return payload;
    }

    public static RecoveryCycleEnvelopeV1 CreateEnvelope(
        RecoveryCycleEventV1 payload,
        string workerId,
        RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryCycleEnvelopeV1(1, "recovery_cycle", workerId, signature.KeyId,
            signature.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes),
            Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeEnvelope(RecoveryCycleEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryCycleEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryCycleEnvelopeV1 envelope;
        try { envelope = JsonSerializer.Deserialize<RecoveryCycleEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("recovery_cycle_envelope_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("recovery_cycle_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("recovery_cycle_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryCycle Verify(RecoveryCycleEnvelopeV1 envelope, RecoverySigningTrust trust)
    {
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParsePayload(bytes);
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.SafeCyclePayloadSha256,
            envelope.SignatureBase64, envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, RecoveryEvidenceCodec.Hash(bytes), envelope.WorkerId, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}
