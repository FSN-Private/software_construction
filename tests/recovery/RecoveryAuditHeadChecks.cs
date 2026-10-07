using Platform.Recovery;
using System.Security.Cryptography;

internal static class RecoveryAuditHeadChecks
{
    public static void Run()
    {
        var minimum = new RecoveryAuditHeadV1(1, new string('a', 64));
        var previous = new RecoveryAuditHeadV1(2, new string('b', 64));
        var requirement = new RecoveryAuditHeadRequirementV1(minimum, previous);
        var chain = new Dictionary<long, string>
        {
            [0] = new string('0', 64),
            [1] = minimum.EventSha256,
            [2] = previous.EventSha256,
            [3] = new string('c', 64)
        };
        requirement.RequireHeadsInChain(chain);

        Rejects("recovery_audit_previous_head_not_in_chain", () =>
            requirement.RequireHeadsInChain(chain.Where(pair => pair.Key != previous.Sequence)
                .ToDictionary(pair => pair.Key, pair => pair.Value)));
        Rejects("recovery_audit_minimum_not_in_chain", () =>
            requirement.RequireHeadsInChain(chain.Where(pair => pair.Key != minimum.Sequence)
                .ToDictionary(pair => pair.Key, pair => pair.Value)));
        var alteredPrefix = chain.ToDictionary(pair => pair.Key, pair => pair.Value);
        alteredPrefix[previous.Sequence] = new string('d', 64);
        Rejects("recovery_audit_previous_head_not_in_chain", () =>
            requirement.RequireHeadsInChain(alteredPrefix));
        Rejects("recovery_audit_head_regressed", () =>
            new RecoveryAuditHeadRequirementV1(minimum, new RecoveryAuditHeadV1(0, new string('0', 64))).Validate());
        Rejects("invalid_recovery_audit_head", () => new RecoveryAuditHeadV1(0, new string('a', 64)).Validate());

        var historical = new RecoveryAuditHeadV1(3, new string('c', 64));
        var historicalRequirement = new RecoveryAuditHeadRequirementV1(minimum, previous, historical);
        historicalRequirement.RequireHeadsInChain(chain);
        Rejects("recovery_audit_historical_head_not_in_chain", () =>
            historicalRequirement.RequireHeadsInChain(chain.Where(pair => pair.Key != historical.Sequence)
                .ToDictionary(pair => pair.Key, pair => pair.Value)));

        SignedProofRoundTripAndTamperCheck();
    }

    private static void Rejects(string expectedMessage, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception) when (exception.Message == expectedMessage)
        {
            return;
        }
        throw new InvalidOperationException($"Expected audit head rejection: {expectedMessage}.");
    }

    private static void SignedProofRoundTripAndTamperCheck()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var fingerprint = RecoveryEvidenceCodec.Hash(spki);
        var trust = new RecoverySigningTrust("fixture-audit-key", Convert.ToBase64String(spki), fingerprint);
        var cutoff = DateTimeOffset.Parse("2026-10-07T01:00:00Z");
        var payload = new RecoveryAuditHeadProofPayloadV1(1, Guid.NewGuid(), Guid.NewGuid(), "owner-a", "main",
            new string('1', 64), new string('2', 64), cutoff, "0/10",
            new RecoveryAuditHeadV1(3, new string('c', 64)),
            new RecoveryAuditHeadV1(1, new string('a', 64)),
            new RecoveryAuditHeadV1(2, new string('b', 64)), new string('3', 64), cutoff.AddMinutes(1),
            new string('4', 64));
        var bytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofPayload(payload);
        var signature = rsa.SignHash(SHA256.HashData(bytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = RecoveryAuditLedgerCodec.CreateAuditHeadProofEnvelope(payload, "unit-fixture",
            new RecoveryEvidenceSignature(trust.KeyId, trust.PublicKeySha256, signature));
        var encoded = RecoveryAuditLedgerCodec.SerializeAuditHeadProofEnvelope(envelope);
        var verified = RecoveryAuditLedgerCodec.VerifyAuditHeadProof(
            RecoveryAuditLedgerCodec.ParseAuditHeadProofEnvelope(encoded), trust);
        Check(verified.Payload == payload, "Audit head proof round trip changed its signed payload.");
        RecoveryAuditLedgerCodec.RequireAuditHeadProofBinding(payload, payload.RecoveryPointId,
            payload.CaptureId, payload.OwnerId, payload.LibraryId, payload.SourceManifestSha256,
            payload.FullManifestSha256, payload.SnapshotCutoffUtc, payload.SnapshotLsn,
            payload.GenesisArtifactSha256, payload.RecoveryWorkerBuildDigest);

        var altered = envelope with { SignatureBase64 = Convert.ToBase64String(new byte[signature.Length]) };
        RejectsAny(() => RecoveryAuditLedgerCodec.VerifyAuditHeadProof(altered, trust));
        Rejects("audit_head_proof_point_binding_invalid", () =>
            RecoveryAuditLedgerCodec.RequireAuditHeadProofBinding(payload, Guid.NewGuid(),
                payload.CaptureId, payload.OwnerId, payload.LibraryId, payload.SourceManifestSha256,
                payload.FullManifestSha256, payload.SnapshotCutoffUtc, payload.SnapshotLsn,
                payload.GenesisArtifactSha256, payload.RecoveryWorkerBuildDigest));
    }

    private static void RejectsAny(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (CryptographicException) { return; }
        throw new InvalidOperationException("Expected signed audit proof tamper rejection.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
