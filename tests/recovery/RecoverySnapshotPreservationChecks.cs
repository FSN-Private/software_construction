using System.Security.Cryptography;
using Platform.Recovery;

internal static class RecoverySnapshotPreservationChecks
{
    internal static void Run()
    {
        using var rsa = RSA.Create(2048);
        using var foreign = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var trust = new RecoverySigningTrust("verifier", Convert.ToBase64String(publicKey), RecoveryEvidenceCodec.Hash(publicKey));
        var hash = new string('a', 64);
        var now = DateTimeOffset.UtcNow;
        var proof = new RecoverySnapshotPreservationProofV1(1, Guid.NewGuid(), Guid.NewGuid(), "owner", "library",
            hash, hash, "recovery-bucket", "snapshots/point/snapshot.dump", "exact-version", hash, 2048,
            hash, hash, hash, now.AddMinutes(-1), "verifier", hash);
        RecoverySnapshotPreservationEnvelopeV1 Sign(RecoverySnapshotPreservationProofV1 value)
        {
            var bytes = RecoveryEvidenceCodec.SerializeCanonical(value);
            return new(1, "recovery_snapshot_media_free", trust.KeyId, trust.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes),
                Convert.ToBase64String(bytes), Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        }
        var envelope = Sign(proof);
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
        var decoded = RecoverySnapshotPreservationCodec.ParseEnvelope(bytes);
        if (RecoverySnapshotPreservationCodec.Verify(decoded, trust, hash, now) != proof)
            throw new InvalidOperationException("Private preservation handoff changed exact snapshot identity");
        foreach (var changed in new[] { proof with { PointId = Guid.Empty }, proof with { CaptureId = Guid.Empty },
                     proof with { OwnerId = "" }, proof with { CheckpointSha256 = "invalid" },
                     proof with { SchemaInventorySha256 = "invalid" }, proof with { ValidationSha256 = "invalid" },
                     proof with { VersionId = "null" }, proof with { ByteLength = 0 },
                     proof with { ByteLength = 95L * 1024 * 1024 * 1024 + 1 },
                     proof with { VerifiedAtUtc = now.AddSeconds(1) }, proof with { WorkerBuildDigest = new string('b', 64) } })
            Reject(() => RecoverySnapshotPreservationCodec.Verify(Sign(changed), trust, hash, now));
        Reject(() => RecoverySnapshotPreservationCodec.Verify(envelope with { MessageType = "recovery_hold_release" }, trust, hash, now));
        Reject(() => RecoverySnapshotPreservationCodec.Verify(envelope with { PayloadSha256 = new string('b', 64) }, trust, hash, now));
        Reject(() => RecoverySnapshotPreservationCodec.Verify(envelope with
        {
            SignatureBase64 = Convert.ToBase64String(
            foreign.SignData(Convert.FromBase64String(envelope.CanonicalPayloadBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        }, trust, hash, now));
        Reject(() => RecoverySnapshotPreservationCodec.ParseEnvelope([.. bytes, (byte)' ']));
        Reject(() => RecoverySnapshotPreservationCodec.ParseEnvelope(new byte[64 * 1024 + 1]));
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { return; }
        throw new InvalidOperationException("Invalid preservation proof was accepted");
    }
}
