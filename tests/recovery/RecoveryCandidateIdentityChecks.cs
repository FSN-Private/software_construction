using System.Security.Cryptography;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryCandidateIdentityChecks
{
    internal static void Run()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var trust = new RecoverySigningTrust("owner-mfa-key", Convert.ToBase64String(spki), RecoveryEvidenceCodec.Hash(spki));
        var now = new DateTimeOffset(2026,10,7,0,0,0,TimeSpan.Zero);
        var approval = new RecoveryIdentityApprovalV1(1,Guid.NewGuid(),"https://recovery.example/issuer","recovery-client",
            "new-recovery-subject","preserved-owner","independent-mfa-role",trust.KeyId,now.AddMinutes(-1));
        byte[] Sign(RecoveryIdentityApprovalV1 payload) => RecoveryEvidenceCodec.SerializeCanonical(
            new RecoveryIdentityApprovalArtifactV1(payload,Convert.ToBase64String(rsa.SignData(
                RecoveryEvidenceCodec.SerializeCanonical(payload),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))));
        RecoveryIdentityApprovalArtifactV1 Verify(byte[] bytes) => RecoveryCandidateIdentity.VerifyApproval(bytes,trust,
            "preserved-owner",approval.Issuer,approval.Audience,now);
        var valid = Sign(approval);
        if (Verify(valid).Payload != approval) throw new InvalidOperationException("candidate_identity_binding_changed");
        foreach (var altered in new[]
        {
            approval with { PreservedOwnerId="foreign-owner" },approval with { Issuer="https://foreign/issuer" },
            approval with { Audience="foreign-client" },approval with { ApprovalKeyId="recovery-worker-key" },
            approval with { ApprovedAtUtc=now.AddSeconds(1) },approval with { ApprovedAtUtc=now.AddDays(-2) },
            approval with { ApprovedAtUtc=now.AddTicks(-1) },approval with { Subject="" },approval with { MappingId=Guid.Empty }
        }) Reject(()=>Verify(Sign(altered)));
        var artifact = new RecoveryIdentityApprovalArtifactV1(approval,Convert.ToBase64String(new byte[256]));
        Reject(()=>Verify(RecoveryEvidenceCodec.SerializeCanonical(artifact)));
        Reject(()=>Verify([..valid,(byte)' ']));
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (RecoveryWorkerException exception) when(exception.SafeCode=="candidate_identity_approval_invalid") { return; }
        throw new InvalidOperationException("Untrusted recovery identity approval accepted");
    }
}
