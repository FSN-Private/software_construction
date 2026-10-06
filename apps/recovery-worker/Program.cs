using Platform.Recovery.Worker;

if (args.Length > 0 && args[0] == "verify-qualification")
    return OfflineQualificationVerifier.Run(args[1..]);

if (args.Length != 1 || args[0] is not ("capture" or "copy" or "verify" or "drill" or "bootstrap-index"))
{
    Console.Error.WriteLine("usage: recovery-worker <capture|copy|verify|drill|bootstrap-index> | verify-qualification <envelope> <trust> <binding> <checkpoint> <offline-anchor>");
    return 2;
}

var mode = args[0];
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
        case "bootstrap-index":
            await RecoveryPointIndexBootstrapRunner.RunAsync(configuration, CancellationToken.None);
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
