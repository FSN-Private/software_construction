using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryCandidateAccessProbeV1(int SchemaVersion, Guid RunId, Guid PointId,
    string State, string MappingApprovalSha256, RecoveryOwnerAccessTranscriptV1 Transcript,
    RecoveryOwnerAccessExpectationV1 Expectation);

/// <summary>Offline preparation and assembly only. The independent owner must
/// review and sign the exact prepared payload with their separate MFA key.</summary>
internal static class OfflineOwnerAccessAttestation
{
    internal static RecoveryOwnerAccessEvidenceV1 Prepare(byte[] probeBytes, RecoverySigningTrust workerTrust,
        byte[] mappingBytes, RecoverySigningTrust ownerTrust, RecoveryOwnerAccessExpectationV1 expected,
        DateTimeOffset nowUtc)
    {
        expected.Validate();
        _ = workerTrust.ValidateAndGetPublicKey();
        _ = ownerTrust.ValidateAndGetPublicKey();
        if (nowUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("owner_access_time_invalid");
        if (workerTrust.KeyId == ownerTrust.KeyId || workerTrust.PublicKeySha256 == ownerTrust.PublicKeySha256)
            throw new InvalidDataException("owner_access_independent_key_required");
        var envelope = ParseCanonical<PrivateSignedCheckpointV1>(probeBytes);
        if (envelope.SchemaVersion != 1 || envelope.MessageType != "recovery_candidate_access_probe" ||
            string.IsNullOrWhiteSpace(envelope.WorkerId))
            throw new InvalidDataException("owner_access_probe_envelope_invalid");
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        _ = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, workerTrust);
        var probe = ParseCanonical<RecoveryCandidateAccessProbeV1>(bytes);
        if (probe.SchemaVersion != 1 || probe.RunId == Guid.Empty || probe.PointId == Guid.Empty ||
            probe.State != "independent_attestation_pending" || probe.Expectation != expected ||
            probe.MappingApprovalSha256 != RecoveryEvidenceCodec.Hash(mappingBytes) || probe.Transcript is null)
            throw new InvalidDataException("owner_access_probe_binding_mismatch");
        var mapping = RecoveryCandidateIdentity.VerifyApproval(mappingBytes, ownerTrust, expected.OwnerId,
            expected.Issuer, expected.UserPoolClientId, nowUtc);
        var transcript = probe.Transcript;
        transcript.Validate(expected.OwnerId, nowUtc);
        if (transcript.OwnerRequestAtUtc < mapping.Payload.ApprovedAtUtc || transcript.ForeignHttpStatus != 403 ||
            transcript.ForeignRequestAtUtc < nowUtc.AddHours(-24) ||
            transcript.OwnerSubjectSha256 != RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(mapping.Payload.Subject)))
            throw new InvalidDataException("owner_access_probe_identity_or_time_mismatch");
        // Retain the original observation time; preparation/signing cannot renew it.
        var verifiedAt = transcript.ForeignRequestAtUtc;
        var payload = new RecoveryOwnerAccessEvidenceV1(1, expected.OwnerId, expected.LibraryId,
            expected.ConfigurationDigest, expected.ApiBuildDigest, expected.SourceRevision, expected.MigrationDigest,
            expected.RecoveryWorkerBuildDigest, expected.RecoveryTemplateDigest, expected.UserPoolId,
            expected.UserPoolClientId, expected.Issuer, verifiedAt, verifiedAt.AddMonths(3),
            RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(transcript)), transcript);
        payload.Validate();
        return payload;
    }

    internal static byte[] Assemble(byte[] payloadBytes, byte[] signature, RecoverySigningTrust ownerTrust,
        RecoveryOwnerAccessExpectationV1 expected, DateTimeOffset nowUtc)
    {
        var payload = RecoveryOwnerAccessCodec.ParsePayload(payloadBytes);
        var envelope = RecoveryOwnerAccessCodec.CreateEnvelope(payload,
            new RecoveryEvidenceSignature(ownerTrust.KeyId, ownerTrust.PublicKeySha256, signature));
        _ = RecoveryOwnerAccessCodec.Verify(envelope, ownerTrust, expected, nowUtc);
        return RecoveryOwnerAccessCodec.SerializeEnvelope(envelope);
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] == "prepare" && args.Length != 7 ||
            args[0] == "assemble" && args.Length != 6 || args[0] is not ("prepare" or "assemble"))
        {
            Console.Error.WriteLine("usage: owner-access prepare <probe> <worker-trust> <mapping> <owner-trust> <expectation> <payload-output> | owner-access assemble <payload> <raw-signature> <owner-trust> <expectation> <envelope-output>");
            return 2;
        }
        try
        {
            byte[] output;
            if (args[0] == "prepare")
                output = RecoveryOwnerAccessCodec.SerializePayload(Prepare(Read(args[1]),
                    ParseCanonical<RecoverySigningTrust>(Read(args[2])), Read(args[3]),
                    ParseCanonical<RecoverySigningTrust>(Read(args[4])),
                    ParseCanonical<RecoveryOwnerAccessExpectationV1>(Read(args[5])), DateTimeOffset.UtcNow));
            else
                output = Assemble(Read(args[1]), Read(args[2]),
                    ParseCanonical<RecoverySigningTrust>(Read(args[3])),
                    ParseCanonical<RecoveryOwnerAccessExpectationV1>(Read(args[4])), DateTimeOffset.UtcNow);
            // Never overwrite a reviewed payload or an existing signed proof.
            using var file = new FileStream(args[^1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(output);
            file.Flush(flushToDisk: true);
            Console.WriteLine($"owner_access_{(args[0] == "prepare" ? "prepared" : "assembled")} sha256={RecoveryEvidenceCodec.Hash(output)}");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or
            UnauthorizedAccessException or ArgumentException or CryptographicException or FormatException or RecoveryWorkerException)
        {
            Console.Error.WriteLine("owner_access_rejected code=offline_attestation_invalid");
            return 1;
        }
    }

    private static byte[] Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 64 * 1024) throw new InvalidDataException("offline_attestation_size_invalid");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("offline_attestation_size_invalid");
        return bytes;
    }

    private static T ParseCanonical<T>(byte[] bytes) where T : class
    {
        var result = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("offline_attestation_empty");
        if (!RecoveryEvidenceCodec.SerializeCanonical(result).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("offline_attestation_noncanonical");
        return result;
    }
}
