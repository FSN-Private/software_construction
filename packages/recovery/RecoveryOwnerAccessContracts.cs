using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

/// <summary>Concrete probes recorded by a protected recovery-only owner-access check.
/// Subject values are one-way hashes; raw tokens and addresses never enter this record.</summary>
public sealed record RecoveryOwnerAccessTranscriptV1(
    int SchemaVersion,
    Guid ProbeId,
    DateTimeOffset OwnerLoginAtUtc,
    DateTimeOffset OwnerRequestAtUtc,
    string OwnerSubjectSha256,
    int OwnerHttpStatus,
    string PreservedOwnerIdSha256,
    string OwnerResponseSha256,
    DateTimeOffset ForeignRequestAtUtc,
    string ForeignSubjectSha256,
    int ForeignHttpStatus,
    string ForeignResponseSha256)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate(string ownerId, DateTimeOffset verifiedAtUtc)
    {
        if (SchemaVersion != CurrentSchemaVersion || ProbeId == Guid.Empty)
            throw new InvalidDataException("invalid_owner_access_probe");
        RecoveryValidation.Utc(OwnerLoginAtUtc, nameof(OwnerLoginAtUtc));
        RecoveryValidation.Utc(OwnerRequestAtUtc, nameof(OwnerRequestAtUtc));
        RecoveryValidation.Utc(ForeignRequestAtUtc, nameof(ForeignRequestAtUtc));
        if (OwnerRequestAtUtc < OwnerLoginAtUtc || ForeignRequestAtUtc < OwnerRequestAtUtc || ForeignRequestAtUtc > verifiedAtUtc)
            throw new InvalidDataException("owner_access_probe_timeline_invalid");
        if (OwnerHttpStatus != 200 || ForeignHttpStatus is not (401 or 403))
            throw new InvalidDataException("owner_access_probe_result_failed");
        RecoveryValidation.Hash(OwnerSubjectSha256, nameof(OwnerSubjectSha256));
        RecoveryValidation.Hash(PreservedOwnerIdSha256, nameof(PreservedOwnerIdSha256));
        RecoveryValidation.Hash(OwnerResponseSha256, nameof(OwnerResponseSha256));
        RecoveryValidation.Hash(ForeignSubjectSha256, nameof(ForeignSubjectSha256));
        RecoveryValidation.Hash(ForeignResponseSha256, nameof(ForeignResponseSha256));
        var expectedOwnerHash = RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes(ownerId));
        if (PreservedOwnerIdSha256 != expectedOwnerHash || OwnerSubjectSha256 == ForeignSubjectSha256)
            throw new InvalidDataException("owner_access_identity_mismatch");
    }
}

/// <summary>Owner-approval-key attestation of a real successful preserved-owner
/// request and a denied foreign-principal request against the isolated recovery API.</summary>
public sealed record RecoveryOwnerAccessEvidenceV1(
    int SchemaVersion,
    string OwnerId,
    string LibraryId,
    string ConfigurationDigest,
    string ApiBuildDigest,
    string SourceRevision,
    string MigrationDigest,
    string RecoveryWorkerBuildDigest,
    string RecoveryTemplateDigest,
    string UserPoolId,
    string UserPoolClientId,
    string Issuer,
    DateTimeOffset VerifiedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string TranscriptSha256,
    RecoveryOwnerAccessTranscriptV1 Transcript)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("invalid_owner_access_evidence_schema");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Hash(ConfigurationDigest, nameof(ConfigurationDigest));
        RecoveryValidation.Hash(ApiBuildDigest, nameof(ApiBuildDigest));
        RecoveryValidation.Text(SourceRevision, nameof(SourceRevision), 200);
        RecoveryValidation.Hash(MigrationDigest, nameof(MigrationDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Text(UserPoolId, nameof(UserPoolId), 256);
        RecoveryValidation.Text(UserPoolClientId, nameof(UserPoolClientId), 256);
        RecoveryValidation.Text(Issuer, nameof(Issuer), 512);
        RecoveryValidation.Utc(VerifiedAtUtc, nameof(VerifiedAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (ExpiresAtUtc <= VerifiedAtUtc || ExpiresAtUtc > VerifiedAtUtc.AddMonths(3))
            throw new InvalidDataException("owner_access_evidence_expiry_invalid");
        RecoveryValidation.Hash(TranscriptSha256, nameof(TranscriptSha256));
        if (Transcript is null) throw new InvalidDataException("owner_access_transcript_required");
        Transcript.Validate(OwnerId, VerifiedAtUtc);
        if (RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(Transcript)) != TranscriptSha256)
            throw new InvalidDataException("owner_access_transcript_digest_mismatch");
    }
}

public sealed record RecoveryOwnerAccessEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    RecoveryOwnerAccessEvidenceV1 Payload,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("invalid_owner_access_envelope");
        Payload.Validate();
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_owner_access_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("owner_access_signature_required");
    }
}

public sealed record RecoveryOwnerAccessExpectationV1(
    string OwnerId,
    string LibraryId,
    string ConfigurationDigest,
    string ApiBuildDigest,
    string SourceRevision,
    string MigrationDigest,
    string RecoveryWorkerBuildDigest,
    string RecoveryTemplateDigest,
    string UserPoolId,
    string UserPoolClientId,
    string Issuer)
{
    public void Validate()
    {
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Hash(ConfigurationDigest, nameof(ConfigurationDigest));
        RecoveryValidation.Hash(ApiBuildDigest, nameof(ApiBuildDigest));
        RecoveryValidation.Text(SourceRevision, nameof(SourceRevision), 200);
        RecoveryValidation.Hash(MigrationDigest, nameof(MigrationDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Text(UserPoolId, nameof(UserPoolId), 256);
        RecoveryValidation.Text(UserPoolClientId, nameof(UserPoolClientId), 256);
        RecoveryValidation.Text(Issuer, nameof(Issuer), 512);
    }
}

public sealed record VerifiedRecoveryOwnerAccessEvidence(
    RecoveryOwnerAccessEvidenceV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryOwnerAccessCodec
{
    public static byte[] SerializePayload(RecoveryOwnerAccessEvidenceV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryOwnerAccessEvidenceV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryOwnerAccessEvidenceV1 payload;
        try { payload = JsonSerializer.Deserialize<RecoveryOwnerAccessEvidenceV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("owner_access_payload_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("owner_access_payload_invalid_json", exception); }
        payload.Validate();
        if (!SerializePayload(payload).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("owner_access_payload_noncanonical");
        return payload;
    }

    public static RecoveryOwnerAccessEnvelopeV1 CreateEnvelope(RecoveryOwnerAccessEvidenceV1 payload, RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryOwnerAccessEnvelopeV1(1, payload, RecoveryEvidenceCodec.Hash(bytes),
            signature.KeyId, signature.PublicKeySha256, Convert.ToBase64String(bytes), Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeEnvelope(RecoveryOwnerAccessEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryOwnerAccessEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryOwnerAccessEnvelopeV1 envelope;
        try { envelope = JsonSerializer.Deserialize<RecoveryOwnerAccessEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("owner_access_envelope_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("owner_access_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("owner_access_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryOwnerAccessEvidence Verify(
        RecoveryOwnerAccessEnvelopeV1 envelope,
        RecoverySigningTrust trust,
        RecoveryOwnerAccessExpectationV1 expected,
        DateTimeOffset nowUtc)
    {
        envelope.Validate();
        expected.Validate();
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParsePayload(bytes);
        if (payload.OwnerId != expected.OwnerId || payload.LibraryId != expected.LibraryId ||
            payload.ConfigurationDigest != expected.ConfigurationDigest || payload.ApiBuildDigest != expected.ApiBuildDigest ||
            payload.SourceRevision != expected.SourceRevision || payload.MigrationDigest != expected.MigrationDigest ||
            payload.RecoveryWorkerBuildDigest != expected.RecoveryWorkerBuildDigest ||
            payload.RecoveryTemplateDigest != expected.RecoveryTemplateDigest || payload.UserPoolId != expected.UserPoolId ||
            payload.UserPoolClientId != expected.UserPoolClientId || payload.Issuer != expected.Issuer)
            throw new InvalidDataException("owner_access_configuration_mismatch");
        if (payload.VerifiedAtUtc > nowUtc || payload.ExpiresAtUtc <= nowUtc)
            throw new InvalidDataException("owner_access_evidence_stale_or_future");
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, RecoveryEvidenceCodec.Hash(bytes), envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}
