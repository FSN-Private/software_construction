using Platform.Recovery.Worker;

if (args.Length > 0 && args[0] == "verify-qualification")
    return OfflineQualificationVerifier.Run(args[1..]);
if (args.Length > 0 && args[0] == "owner-access")
    return OfflineOwnerAccessAttestation.Run(args[1..]);

if (args.Length == 0 || args[0] is not ("capture" or "copy" or "verify" or "drill" or "restore" or "bootstrap-index" or "purge" or "health" or "list-points"))
{
    Console.Error.WriteLine("usage: recovery-worker <capture|copy|verify|drill|bootstrap-index|health|list-points> | recovery-worker restore <proposal> | recovery-worker purge <proposal> <plan|execute> <output> [approval <receipt>] | verify-qualification <envelope> <trust> <binding> <checkpoint> <offline-anchor>");
    return 2;
}

var mode = args[0];
if (mode is not ("purge" or "restore") && args.Length != 1 || mode == "restore" && args.Length != 2 || mode == "purge" && args.Length is not (4 or 6))
{
    Console.Error.WriteLine("usage: recovery-worker restore <proposal> | recovery-worker purge <proposal> plan <plan-output> | recovery-worker purge <proposal> execute <plan> <approval> <receipt-output>");
    return 2;
}
try
{
    var configuration = RecoveryWorkerConfiguration.Load(mode);
    switch (mode)
    {
        case "capture":
            await CaptureRunner.RunAsync(configuration, CancellationToken.None);
            break;
        case "copy":
            await CopyRunner.RunAsync(configuration, CancellationToken.None);
            break;
        case "verify":
            await RecoveryVerifierRunner.RunAsync(configuration, CancellationToken.None);
            break;
        case "drill":
            await RecoveryDrillRunner.RunAsync(configuration, CancellationToken.None);
            break;
        case "restore":
            await RecoveryRestoreRunner.RunAsync(configuration, args[1], CancellationToken.None);
            break;
        case "bootstrap-index":
            await RecoveryPointIndexBootstrapRunner.RunAsync(configuration, CancellationToken.None);
            break;
        case "purge":
            await RecoveryPurgeRunner.RunAsync(configuration, args[1..], CancellationToken.None);
            break;
        case "health":
        case "list-points":
            await RecoveryControlReader.RunAsync(configuration, CancellationToken.None);
            break;
    }

    RecoveryWorkerMetrics.Emit("RecoveryWorkerHeartbeat", 1, configuration);
    return 0;
}
catch (RecoveryWorkerException exception)
{
    RecoveryWorkerMetrics.EmitFailure(mode, exception.SafeCode);
    Console.Error.WriteLine($"recovery_worker_failed stage={mode} code={exception.SafeCode}");
    return 1;
}
catch (OperationCanceledException)
{
    RecoveryWorkerMetrics.EmitFailure(mode, "worker_cancelled");
    Console.Error.WriteLine($"recovery_worker_failed stage={mode} code=worker_cancelled");
    return 1;
}
catch
{
    RecoveryWorkerMetrics.EmitFailure(mode, "worker_unhandled_error");
    Console.Error.WriteLine($"recovery_worker_failed stage={mode} code=worker_unhandled_error");
    return 1;
}
