using System.Text;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryRestoreRunnerChecks
{
    internal static void Run()
    {
        var names = new[] { "Environment", "RecoveryWorkerBuildDigest", "OwnerId", "LibraryId" };
        var prior = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        try
        {
            var values = new[] { "fixture", new string('a', 64), "restore-owner", "main" };
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable("Recovery__" + names[i], values[i]);
            var config = RecoveryWorkerConfiguration.Load("restore");
            var proposal = RecoveryRestoreProposalV1.Create(Guid.NewGuid(), "restore-owner", "main", Guid.NewGuid(),
                new("full_library", null, null), DateTimeOffset.UtcNow.AddHours(1));
            var bytes = RecoveryEvidenceCodec.SerializeCanonical(proposal);
            if (RecoveryRestoreRunner.ParseProposal(bytes, config) != proposal) throw new InvalidOperationException("Restore proposal changed");
            var name = RecoveryRestoreRunner.DatabaseName(proposal);
            if (name.Length > 63 || name != "recovery_drill_" + proposal.SessionId.ToString("N") + "_" + proposal.RecoveryPointId.ToString("N")[..8])
                throw new InvalidOperationException("Restore namespace is not deterministic and isolated");
            foreach (var changed in new[]
            {
                proposal with { RequestDigest = new string('f', 64) },
                RecoveryRestoreProposalV1.Create(proposal.SessionId, "foreign-owner", proposal.LibraryId, proposal.RecoveryPointId, proposal.RequestedScope, proposal.ExpiresAtUtc),
                RecoveryRestoreProposalV1.Create(proposal.SessionId, proposal.OwnerId, "foreign-library", proposal.RecoveryPointId, proposal.RequestedScope, proposal.ExpiresAtUtc),
                RecoveryRestoreProposalV1.Create(proposal.SessionId, proposal.OwnerId, proposal.LibraryId, proposal.RecoveryPointId, proposal.RequestedScope, DateTimeOffset.UtcNow.AddSeconds(-1))
            }) Reject(RecoveryEvidenceCodec.SerializeCanonical(changed), config);
            Reject(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes)), config);
            Reject(new byte[64 * 1024 + 1], config);
            Reject("null"u8.ToArray(), config);
        }
        finally { foreach (var pair in prior) Environment.SetEnvironmentVariable("Recovery__" + pair.Key, pair.Value); }
    }

    private static void Reject(byte[] bytes, RecoveryWorkerConfiguration configuration)
    {
        try { _ = RecoveryRestoreRunner.ParseProposal(bytes, configuration); }
        catch (RecoveryWorkerException) { return; }
        throw new InvalidOperationException("Foreign, stale or altered restore request was accepted");
    }
}
