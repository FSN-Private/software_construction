using Platform.Recovery.Worker;

internal static class RecoveryCandidateAccessInputChecks
{
    internal static async Task RunAsync()
    {
        var names = new[] { "OwnerIdentityApprovalPath", "OwnerProbeTokenPath", "ForeignProbeTokenPath" };
        var old = names.Concat(["Environment", "RecoveryWorkerBuildDigest"])
            .ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        var path = Path.Combine(Path.GetTempPath(), "recovery-input-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("Recovery__Environment", "candidate-input-fixture");
            Environment.SetEnvironmentVariable("Recovery__RecoveryWorkerBuildDigest", new string('a', 64));
            foreach (var name in names) Environment.SetEnvironmentVariable("Recovery__" + name, null);
            var configuration = new RecoveryWorkerConfiguration("drill");
            if (await RecoveryCandidateAccessRunner.ReadInputsAsync(configuration, default) is not null)
                throw new InvalidOperationException("Missing inputs must leave the candidate unqualified.");
            Environment.SetEnvironmentVariable("Recovery__" + names[0], path);
            await Reject("candidate_probe_inputs_incomplete");
            foreach (var name in names) Environment.SetEnvironmentVariable("Recovery__" + name, path);
            await File.WriteAllTextAsync(path, "fixture-only");
            var inputs = await RecoveryCandidateAccessRunner.ReadInputsAsync(configuration, default);
            if (inputs?.OwnerToken != "fixture-only" || inputs.ForeignToken != "fixture-only")
                throw new InvalidOperationException("Bounded input reader changed the configured input.");
            await File.WriteAllBytesAsync(path, new byte[64 * 1024 + 1]);
            await Reject("candidate_probe_input_size_limit");

            async Task Reject(string code)
            {
                try { await RecoveryCandidateAccessRunner.ReadInputsAsync(configuration, default); }
                catch (RecoveryWorkerException error) when (error.SafeCode == code) { return; }
                throw new InvalidOperationException("Expected input rejection: " + code);
            }
        }
        finally
        {
            foreach (var (name, value) in old) Environment.SetEnvironmentVariable("Recovery__" + name, value);
            File.Delete(path);
        }
    }
}
