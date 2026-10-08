using System.Security.Cryptography;
using System.Text;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class OfflineOwnerAccessAttestationChecks
{
    internal static void Run()
    {
        using var worker = RSA.Create(2048);
        using var owner = RSA.Create(2048);
        RecoverySigningTrust Trust(RSA key, string id) => new(id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            RecoveryEvidenceCodec.Hash(key.ExportSubjectPublicKeyInfo()));
        var workerTrust = Trust(worker, "worker-key");
        var ownerTrust = Trust(owner, "independent-owner-key");
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var hash = new string('a', 64);
        var expected = new RecoveryOwnerAccessExpectationV1("owner", "library", hash, hash, "revision", hash,
            hash, hash, "pool", "client", "https://independent.example/issuer");
        var mapping = new RecoveryIdentityApprovalV1(1, Guid.NewGuid(), expected.Issuer, expected.UserPoolClientId,
            "mapped-subject", expected.OwnerId, "mfa-operator", ownerTrust.KeyId, now.AddMinutes(-5));
        byte[] Mapping(RecoveryIdentityApprovalV1 value) => RecoveryEvidenceCodec.SerializeCanonical(
            new RecoveryIdentityApprovalArtifactV1(value, Convert.ToBase64String(owner.SignData(
                RecoveryEvidenceCodec.SerializeCanonical(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
        var mappingBytes = Mapping(mapping);
        var transcript = new RecoveryOwnerAccessTranscriptV1(1, Guid.NewGuid(), now.AddMinutes(-3),
            now.AddMinutes(-2), RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(mapping.Subject)), 200,
            RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(expected.OwnerId)), hash,
            now.AddMinutes(-1), new string('b', 64), 403, hash);
        var probe = new RecoveryCandidateAccessProbeV1(1, Guid.NewGuid(), Guid.NewGuid(),
            "independent_attestation_pending", RecoveryEvidenceCodec.Hash(mappingBytes), transcript, expected);
        byte[] Probe(RecoveryCandidateAccessProbeV1 value, RSA? key = null)
        {
            var bytes = RecoveryEvidenceCodec.SerializeCanonical(value);
            return RecoveryEvidenceCodec.SerializeCanonical(new PrivateSignedCheckpointV1(1,
                "recovery_candidate_access_probe", "drill", workerTrust.KeyId, workerTrust.PublicKeySha256,
                RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes), Convert.ToBase64String((key ?? worker)
                    .SignHash(SHA256.HashData(bytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
        }
        RecoveryOwnerAccessEvidenceV1 Prepare(byte[] bytes, byte[]? approval = null,
            RecoveryOwnerAccessExpectationV1? pins = null, DateTimeOffset? clock = null) =>
            OfflineOwnerAccessAttestation.Prepare(bytes, workerTrust, approval ?? mappingBytes, ownerTrust,
                pins ?? expected, clock ?? now);
        var payload = Prepare(Probe(probe));
        if (payload.VerifiedAtUtc != transcript.ForeignRequestAtUtc ||
            payload.ExpiresAtUtc != transcript.ForeignRequestAtUtc.AddMonths(3))
            throw new InvalidOperationException("Owner attestation refreshed original probe time");
        var payloadBytes = RecoveryOwnerAccessCodec.SerializePayload(payload);
        var signature = owner.SignHash(SHA256.HashData(payloadBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelopeBytes = OfflineOwnerAccessAttestation.Assemble(payloadBytes, signature, ownerTrust, expected, now);
        var verified = RecoveryOwnerAccessCodec.Verify(RecoveryOwnerAccessCodec.ParseEnvelope(envelopeBytes), ownerTrust, expected, now);
        if (verified.Payload != payload) throw new InvalidOperationException("Offline assembled evidence changed");
        foreach (var altered in new[]
        {
            probe with { PointId = Guid.Empty }, probe with { RunId = Guid.Empty }, probe with { State = "healthy" },
            probe with { MappingApprovalSha256 = new string('c', 64) },
            probe with { Expectation = expected with { ConfigurationDigest = new string('c', 64) } },
            probe with { Transcript = transcript with { OwnerSubjectSha256 = new string('c', 64) } },
            probe with { Transcript = transcript with { ForeignHttpStatus = 401 } },
            probe with { Transcript = transcript with { ForeignRequestAtUtc = now.AddSeconds(1) } },
            probe with { Transcript = transcript with { OwnerRequestAtUtc = now.AddMinutes(-6), OwnerLoginAtUtc = now.AddMinutes(-7) } }
        }) Reject(() => Prepare(Probe(altered)));
        Reject(() => Prepare(Probe(probe, owner)));
        Reject(() => Prepare([.. Probe(probe), (byte)' ']));
        Reject(() => Prepare(Probe(probe), Mapping(mapping with { Subject = "other-subject" })));
        Reject(() => Prepare(Probe(probe), pins: expected with { OwnerId = "foreign" }));
        Reject(() => Prepare(Probe(probe), clock: now.AddDays(2)));
        Reject(() => OfflineOwnerAccessAttestation.Prepare(Probe(probe), workerTrust, mappingBytes,
            workerTrust, expected, now));
        Reject(() => OfflineOwnerAccessAttestation.Assemble(payloadBytes,
            worker.SignHash(SHA256.HashData(payloadBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss), ownerTrust, expected, now));
        Reject(() => OfflineOwnerAccessAttestation.Assemble([.. payloadBytes, (byte)' '], signature, ownerTrust, expected, now));
        Reject(() => OfflineOwnerAccessAttestation.Assemble(payloadBytes, signature, ownerTrust, expected, payload.ExpiresAtUtc));
        // Exercise the actual offline CLI without cloud configuration or credentials.
        var directory = Path.Combine(Path.GetTempPath(), "owner-attestation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var current = DateTimeOffset.UtcNow;
            var recentMapping = Mapping(mapping with { ApprovedAtUtc = current.AddMinutes(-5).AddTicks(-(current.Ticks % 10)) });
            var recent = probe with { MappingApprovalSha256 = RecoveryEvidenceCodec.Hash(recentMapping),
                Transcript = transcript with { OwnerLoginAtUtc = current.AddMinutes(-3), OwnerRequestAtUtc = current.AddMinutes(-2),
                    ForeignRequestAtUtc = current.AddMinutes(-1) } };
            string Write(string name, byte[] bytes) { var path = Path.Combine(directory, name); File.WriteAllBytes(path, bytes); return path; }
            var probePath = Write("probe.json", Probe(recent));
            var workerPath = Write("worker.json", RecoveryEvidenceCodec.SerializeCanonical(workerTrust));
            var ownerPath = Write("owner.json", RecoveryEvidenceCodec.SerializeCanonical(ownerTrust));
            var mappingPath = Write("mapping.json", recentMapping);
            var pinsPath = Write("pins.json", RecoveryEvidenceCodec.SerializeCanonical(expected));
            var output = Path.Combine(directory, "payload.json");
            string[] prepare = ["prepare", probePath, workerPath, mappingPath, ownerPath, pinsPath, output];
            if (OfflineOwnerAccessAttestation.Run(prepare) != 0 || OfflineOwnerAccessAttestation.Run(prepare) != 1)
                throw new InvalidOperationException("CLI failed preparation or overwrote reviewed payload");
            var prepared = File.ReadAllBytes(output);
            var sigPath = Write("signature.bin", owner.SignHash(SHA256.HashData(prepared), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            var assembled = Path.Combine(directory, "proof.json");
            if (OfflineOwnerAccessAttestation.Run(["assemble", output, sigPath, ownerPath, pinsPath, assembled]) != 0)
                throw new InvalidOperationException("CLI failed signed assembly");
            _ = RecoveryOwnerAccessCodec.Verify(RecoveryOwnerAccessCodec.ParseEnvelope(File.ReadAllBytes(assembled)), ownerTrust, expected, current);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or RecoveryWorkerException) { return; }
        throw new InvalidOperationException("Untrusted owner attestation accepted");
    }
}
