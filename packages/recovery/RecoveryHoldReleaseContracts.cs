using System.Text.Json;

namespace Platform.Recovery;

public sealed record RecoveryHoldReleaseApprovalV1(int SchemaVersion, Guid ApprovalId, string OwnerId,
    string LibraryId, Guid HoldId, string Kind, string DefinitionSha256, DateTimeOffset CreatedAtUtc,
    string IndependentMfaId, DateTimeOffset ApprovedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || ApprovalId == Guid.Empty || HoldId == Guid.Empty || Kind is not ("lifecycle" or "recovery"))
            throw new InvalidDataException("hold_release_approval_invalid");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(IndependentMfaId, nameof(IndependentMfaId), 200);
        RecoveryValidation.Hash(DefinitionSha256, nameof(DefinitionSha256));
        RecoveryValidation.Utc(CreatedAtUtc, nameof(CreatedAtUtc));
        RecoveryValidation.Utc(ApprovedAtUtc, nameof(ApprovedAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (CreatedAtUtc > ApprovedAtUtc || ExpiresAtUtc <= ApprovedAtUtc || ExpiresAtUtc > ApprovedAtUtc.AddMinutes(15))
            throw new InvalidDataException("hold_release_approval_timeline_invalid");
    }
}

public sealed record RecoveryHoldReleaseEnvelopeV1(int SchemaVersion, string MessageType, string SigningKeyId,
    string SigningPublicKeySha256, string PayloadSha256, string CanonicalPayloadBase64, string SignatureBase64);

public sealed record RecoveryHoldReleaseProofV1(int SchemaVersion, Guid PointId, Guid CaptureId,
    string OwnerId, string LibraryId, Guid HoldId, string Kind, string DefinitionSha256,
    DateTimeOffset CreatedAtUtc, string CheckpointSha256, DateTimeOffset ObservedAtUtc,
    string VerifierId, string WorkerBuildDigest, RecoveryHoldReleaseEnvelopeV1 OwnerApproval);

/// <summary>Only the signature codec can construct the independently authenticated release.</summary>
public sealed class VerifiedRecoveryHoldRelease
{
    internal VerifiedRecoveryHoldRelease(RecoveryHoldReleaseProofV1 payload, string payloadSha256)
    { Payload = payload; PayloadSha256 = payloadSha256; }
    public RecoveryHoldReleaseProofV1 Payload { get; }
    public string PayloadSha256 { get; }
}

public static class RecoveryHoldReleaseCodec
{
    public static RecoveryHoldReleaseEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 64 * 1024) throw new InvalidDataException("hold_release_envelope_size_invalid");
        return ParseCanonical<RecoveryHoldReleaseEnvelopeV1>(bytes);
    }

    public static RecoveryHoldReleaseApprovalV1 VerifyApproval(RecoveryHoldReleaseEnvelopeV1 envelope,
        RecoverySigningTrust ownerTrust, DateTimeOffset observedAtUtc)
    {
        RecoveryValidation.Utc(observedAtUtc, nameof(observedAtUtc));
        var payload = VerifyApprovalSignature(envelope, ownerTrust);
        if (payload.ApprovedAtUtc > observedAtUtc || payload.ExpiresAtUtc <= observedAtUtc)
            throw new InvalidDataException("hold_release_approval_expired");
        return payload;
    }

    /// <summary>Authenticates immutable lookup identity only, without granting fresh release authority.</summary>
    public static RecoveryHoldReleaseApprovalV1 VerifyApprovalSignature(RecoveryHoldReleaseEnvelopeV1 envelope,
        RecoverySigningTrust ownerTrust)
    {
        var payload = VerifyPayload<RecoveryHoldReleaseApprovalV1>(envelope, "recovery_hold_release_approval", ownerTrust);
        payload.Validate();
        return payload;
    }

    public static VerifiedRecoveryHoldRelease VerifyProof(RecoveryHoldReleaseEnvelopeV1 envelope,
        RecoverySigningTrust recoveryTrust, RecoverySigningTrust ownerTrust, string workerBuildDigest,
        DateTimeOffset nowUtc)
    {
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        RecoveryValidation.Hash(workerBuildDigest, nameof(workerBuildDigest));
        if (recoveryTrust.PublicKeySha256 == ownerTrust.PublicKeySha256)
            throw new InvalidDataException("hold_release_independent_key_required");
        var proof = VerifyPayload<RecoveryHoldReleaseProofV1>(envelope, "recovery_hold_release", recoveryTrust);
        if (proof.SchemaVersion != 1 || proof.PointId == Guid.Empty || proof.CaptureId == Guid.Empty || proof.HoldId == Guid.Empty ||
            proof.Kind is not ("lifecycle" or "recovery") || proof.WorkerBuildDigest != workerBuildDigest)
            throw new InvalidDataException("hold_release_proof_invalid");
        RecoveryValidation.Text(proof.OwnerId, nameof(proof.OwnerId), 200);
        RecoveryValidation.Text(proof.LibraryId, nameof(proof.LibraryId), 200);
        RecoveryValidation.Text(proof.VerifierId, nameof(proof.VerifierId), 200);
        RecoveryValidation.Hash(proof.DefinitionSha256, nameof(proof.DefinitionSha256));
        RecoveryValidation.Hash(proof.CheckpointSha256, nameof(proof.CheckpointSha256));
        RecoveryValidation.Utc(proof.CreatedAtUtc, nameof(proof.CreatedAtUtc));
        RecoveryValidation.Utc(proof.ObservedAtUtc, nameof(proof.ObservedAtUtc));
        if (proof.CreatedAtUtc > proof.ObservedAtUtc || proof.ObservedAtUtc > nowUtc)
            throw new InvalidDataException("hold_release_proof_timeline_invalid");
        var approval = VerifyApproval(proof.OwnerApproval, ownerTrust, proof.ObservedAtUtc);
        if (approval.OwnerId != proof.OwnerId || approval.LibraryId != proof.LibraryId || approval.HoldId != proof.HoldId ||
            approval.Kind != proof.Kind || approval.DefinitionSha256 != proof.DefinitionSha256 || approval.CreatedAtUtc != proof.CreatedAtUtc)
            throw new InvalidDataException("hold_release_proof_approval_mismatch");
        return new(proof, envelope.PayloadSha256);
    }

    private static T VerifyPayload<T>(RecoveryHoldReleaseEnvelopeV1 envelope, string messageType,
        RecoverySigningTrust trust) where T : class
    {
        if (envelope.SchemaVersion != 1 || envelope.MessageType != messageType ||
            RecoveryEvidenceCodec.SerializeCanonical(envelope).Length > 64 * 1024)
            throw new InvalidDataException("hold_release_envelope_invalid");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64); }
        catch (Exception error) when (error is FormatException or ArgumentNullException)
        { throw new InvalidDataException("hold_release_payload_encoding_invalid", error); }
        _ = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return ParseCanonical<T>(bytes);
    }

    private static T ParseCanonical<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("hold_release_payload_empty");
            if (!RecoveryEvidenceCodec.SerializeCanonical(value).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("hold_release_payload_noncanonical");
            return value;
        }
        catch (JsonException error) { throw new InvalidDataException("hold_release_json_invalid", error); }
    }
}
