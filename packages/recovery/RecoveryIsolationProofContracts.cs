using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

/// <summary>Independent MFA-controlled receipt of provider-backed isolation evidence.
/// It binds the SHA-256 of the exact STS GetCallerIdentity assumed-role session ARN,
/// provider transcript and policy/route snapshots by digest; it never reconstructs
/// an IAM role ARN or accepts an unsigned timeout as denial proof.</summary>
public sealed record RecoveryIsolationEvidenceV1(
    int SchemaVersion,
    Guid EvidenceId,
    Guid RunId,
    string OwnerId,
    string LibraryId,
    string WorkerSessionArnSha256,
    string PrimaryAccountId,
    string PrimaryRegion,
    string RecoveryAccountId,
    string RecoveryRegion,
    string ConfigurationDigest,
    string RecoveryTemplateDigest,
    string RecoveryWorkerBuildDigest,
    string PrimaryIamPolicySnapshotSha256,
    string RecoveryIamPolicySnapshotSha256,
    string PrimaryRouteTableSnapshotSha256,
    string RecoveryRouteTableSnapshotSha256,
    string ProbeTargetSha256,
    string ProbeResult,
    DateTimeOffset ProbeObservedAtUtc,
    string ProviderTranscriptSha256,
    DateTimeOffset VerifiedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || EvidenceId == Guid.Empty || RunId == Guid.Empty)
            throw new InvalidDataException("invalid_recovery_isolation_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Hash(WorkerSessionArnSha256, nameof(WorkerSessionArnSha256));
        ValidateAccountId(PrimaryAccountId, nameof(PrimaryAccountId));
        ValidateAccountId(RecoveryAccountId, nameof(RecoveryAccountId));
        RecoveryValidation.Region(PrimaryRegion);
        RecoveryValidation.Region(RecoveryRegion);
        if (PrimaryAccountId == RecoveryAccountId || PrimaryRegion == RecoveryRegion)
            throw new InvalidDataException("recovery_environment_not_independent");
        RecoveryValidation.Hash(ConfigurationDigest, nameof(ConfigurationDigest));
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Hash(PrimaryIamPolicySnapshotSha256, nameof(PrimaryIamPolicySnapshotSha256));
        RecoveryValidation.Hash(RecoveryIamPolicySnapshotSha256, nameof(RecoveryIamPolicySnapshotSha256));
        RecoveryValidation.Hash(PrimaryRouteTableSnapshotSha256, nameof(PrimaryRouteTableSnapshotSha256));
        RecoveryValidation.Hash(RecoveryRouteTableSnapshotSha256, nameof(RecoveryRouteTableSnapshotSha256));
        RecoveryValidation.Hash(ProbeTargetSha256, nameof(ProbeTargetSha256));
        if (ProbeResult is not ("access_denied" or "no_route"))
            throw new InvalidDataException("recovery_isolation_probe_result_invalid");
        RecoveryValidation.Utc(ProbeObservedAtUtc, nameof(ProbeObservedAtUtc));
        RecoveryValidation.Hash(ProviderTranscriptSha256, nameof(ProviderTranscriptSha256));
        RecoveryValidation.Utc(VerifiedAtUtc, nameof(VerifiedAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (ProbeObservedAtUtc > VerifiedAtUtc || ExpiresAtUtc <= VerifiedAtUtc ||
            ExpiresAtUtc > VerifiedAtUtc.AddMonths(3))
            throw new InvalidDataException("recovery_isolation_timeline_invalid");
    }

    private static void ValidateAccountId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 12 || value.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException($"invalid_{name.ToLowerInvariant()}");
    }
}

public sealed record RecoveryIsolationEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    RecoveryIsolationEvidenceV1 Payload,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || Payload is null)
            throw new InvalidDataException("invalid_recovery_isolation_envelope");
        Payload.Validate();
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        try
        {
            _ = Convert.FromBase64String(CanonicalPayloadBase64);
            _ = Convert.FromBase64String(SignatureBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("invalid_recovery_isolation_signature_encoding", exception);
        }
        if (string.IsNullOrWhiteSpace(SignatureBase64))
            throw new InvalidDataException("recovery_isolation_signature_required");
    }
}

public sealed record VerifiedRecoveryIsolationEvidence(
    RecoveryIsolationEvidenceV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryIsolationCodec
{
    public static byte[] SerializePayload(RecoveryIsolationEvidenceV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryIsolationEvidenceV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryIsolationEvidenceV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryIsolationEvidenceV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("recovery_isolation_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("recovery_isolation_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!SerializePayload(payload).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("recovery_isolation_payload_noncanonical");
        return payload;
    }

    public static RecoveryIsolationEnvelopeV1 CreateEnvelope(
        RecoveryIsolationEvidenceV1 payload,
        RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryIsolationEnvelopeV1(1, payload, RecoveryEvidenceCodec.Hash(bytes),
            signature.KeyId, signature.PublicKeySha256, Convert.ToBase64String(bytes),
            Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeEnvelope(RecoveryIsolationEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryIsolationEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryIsolationEnvelopeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RecoveryIsolationEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions)
                ?? throw new InvalidDataException("recovery_isolation_envelope_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("recovery_isolation_envelope_invalid_json", exception);
        }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("recovery_isolation_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryIsolationEvidence Verify(
        RecoveryIsolationEnvelopeV1 envelope,
        RecoverySigningTrust trust,
        RecoveryIsolationExpectationV1 expected,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(expected);
        envelope.Validate();
        expected.Validate();
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParsePayload(bytes);
        if (payload.RunId != expected.RunId || payload.OwnerId != expected.OwnerId || payload.LibraryId != expected.LibraryId ||
            payload.WorkerSessionArnSha256 != expected.WorkerSessionArnSha256 ||
            payload.PrimaryAccountId != expected.PrimaryAccountId || payload.PrimaryRegion != expected.PrimaryRegion ||
            payload.RecoveryAccountId != expected.RecoveryAccountId || payload.RecoveryRegion != expected.RecoveryRegion ||
            payload.ConfigurationDigest != expected.ConfigurationDigest || payload.RecoveryTemplateDigest != expected.RecoveryTemplateDigest ||
            payload.RecoveryWorkerBuildDigest != expected.RecoveryWorkerBuildDigest)
            throw new InvalidDataException("recovery_isolation_configuration_mismatch");
        if (payload.ProbeObservedAtUtc > nowUtc || payload.VerifiedAtUtc > nowUtc || payload.ExpiresAtUtc <= nowUtc)
            throw new InvalidDataException("recovery_isolation_evidence_stale_or_future");
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, RecoveryEvidenceCodec.Hash(bytes), envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}

public sealed record RecoveryIsolationExpectationV1(
    Guid RunId,
    string OwnerId,
    string LibraryId,
    string WorkerSessionArnSha256,
    string PrimaryAccountId,
    string PrimaryRegion,
    string RecoveryAccountId,
    string RecoveryRegion,
    string ConfigurationDigest,
    string RecoveryTemplateDigest,
    string RecoveryWorkerBuildDigest)
{
    public void Validate()
    {
        if (RunId == Guid.Empty) throw new InvalidDataException("recovery_isolation_run_id_required");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Hash(WorkerSessionArnSha256, nameof(WorkerSessionArnSha256));
        RecoveryValidation.Hash(ConfigurationDigest, nameof(ConfigurationDigest));
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Region(PrimaryRegion);
        RecoveryValidation.Region(RecoveryRegion);
    }
}
