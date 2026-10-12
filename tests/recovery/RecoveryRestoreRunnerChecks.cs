using System.Text;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryRestoreRunnerChecks
{
    internal static async Task RunAsync()
    {
        var names = new[] { "Environment", "RecoveryWorkerBuildDigest", "OwnerId", "LibraryId", "ScratchDirectory" };
        var prior = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        try
        {
            var values = new[] { "fixture", new string('a', 64), "restore-owner", "main", Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "recovery-scratch-checks", Guid.NewGuid().ToString("N")) };
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
            await ScratchConcurrencyAsync(config, proposal.SessionId, values[^1]);
        }
        finally { foreach (var pair in prior) Environment.SetEnvironmentVariable("Recovery__" + pair.Key, pair.Value); }
    }

    private static async Task ScratchConcurrencyAsync(RecoveryWorkerConfiguration configuration, Guid session, string root)
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scratch check is outside the workspace");
        var winner = RecoveryRestoreRunner.CreateScratchDirectory(configuration, session);
        var certificate = Path.Combine(winner, "database-ca.pem");
        await File.WriteAllTextAsync(certificate, "winner certificate");
        var loserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = Task.Run(async () =>
        {
            var attempt = RecoveryRestoreRunner.CreateScratchDirectory(configuration, session);
            if (attempt == winner || !Path.GetFullPath(attempt).StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Concurrent restore scratch namespace overlaps");
            loserReady.SetResult();
            await releaseLoser.Task;
            Directory.Delete(attempt, recursive: true);
        });
        try
        {
            await loserReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!File.Exists(certificate)) throw new InvalidOperationException("Winner lost its certificate before cleanup");
            releaseLoser.SetResult();
            await loser;
            if (await File.ReadAllTextAsync(certificate) != "winner certificate")
                throw new InvalidOperationException("Losing retry removed the active restore's certificate");
        }
        finally
        {
            releaseLoser.TrySetResult();
            await loser;
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Reject(byte[] bytes, RecoveryWorkerConfiguration configuration)
    {
        try { _ = RecoveryRestoreRunner.ParseProposal(bytes, configuration); }
        catch (RecoveryWorkerException) { return; }
        throw new InvalidOperationException("Foreign, stale or altered restore request was accepted");
    }
}
