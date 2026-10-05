using System.Text.Json;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class OfflineQualificationVerifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Run(string[] paths)
    {
        try
        {
            if (paths.Length != 5) throw new InvalidDataException("usage");
            var qualificationBytes = ReadBounded(paths[0], 1024 * 1024);
            var trustBytes = ReadBounded(paths[1], 16 * 1024);
            var bindingBytes = ReadBounded(paths[2], 16 * 1024);
            var checkpointBytes = ReadBounded(paths[3], 64 * 1024 * 1024);
            var anchorBytes = ReadBounded(paths[4], 1024 * 1024);

            var trust = Deserialize<RecoverySigningTrust>(trustBytes, "qualification_trust_invalid");
            var binding = Deserialize<RecoveryQualificationBindingV1>(bindingBytes, "release_binding_invalid");
            if (!RecoveryEvidenceCodec.SerializeCanonical(trust).AsSpan().SequenceEqual(trustBytes) ||
                !RecoveryEvidenceCodec.SerializeCanonical(binding).AsSpan().SequenceEqual(bindingBytes))
                throw new InvalidDataException("offline_input_noncanonical");
            _ = trust.ValidateAndGetPublicKey();

            var envelope = RecoveryQualificationCodec.ParseEnvelope(qualificationBytes);
            var verified = RecoveryQualificationCodec.Verify(envelope, trust);
            RecoveryQualificationReleaseGate.RequireReady(verified, trust, binding, DateTimeOffset.UtcNow);

            var artifact = RecoveryAuditCheckpointCodec.ParseArtifact(checkpointBytes);
            var anchor = RecoveryAuditCheckpointCodec.ParseAnchor(anchorBytes);
            var auditTrust = anchor.SigningTrust();
            var checkpoint = RecoveryAuditCheckpointCodec.Verify(artifact.Checkpoint, auditTrust);
            RecoveryAuditCheckpointCodec.RequireQualificationBinding(checkpoint, anchor, verified,
                RecoveryEvidenceCodec.Hash(qualificationBytes), DateTimeOffset.UtcNow);
            Console.WriteLine($"recovery_qualification_verified qualification={verified.Payload.QualificationId:D} run={verified.Payload.RunId:D} point={verified.Payload.Point.PointId:D}");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or
                                           UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            var code = exception is InvalidDataException ? exception.Message : "offline_proof_input_invalid";
            Console.Error.WriteLine($"recovery_qualification_rejected code={SafeCode(code)}");
            return 1;
        }
    }

    private static T Deserialize<T>(byte[] bytes, string safeCode) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException(safeCode);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(safeCode, exception);
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException("offline_proof_input_size_invalid");
        return File.ReadAllBytes(path);
    }

    private static string SafeCode(string value) => value.Length <= 100 &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-')
        ? value : "offline_proof_invalid";
}
