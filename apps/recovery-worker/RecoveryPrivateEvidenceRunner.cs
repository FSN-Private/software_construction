using Amazon.DynamoDBv2;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryPrivateEvidenceRunner
{
    internal static async Task RunAsync(RecoveryWorkerConfiguration configuration, string? approvalPath,
        string outputPath, CancellationToken ct)
    {
        var approval = approvalPath is null ? null : RecoveryHoldReleaseCodec.ParseEnvelope(
            await RecoveryPurgeRunner.ReadBoundedArtifactAsync(approvalPath, 64 * 1024, ct));
        var ownerTrust = approval is null ? null : RecoveryPurgeRunner.ReadTrust(configuration, "OwnerApprovalTrust");
        var requested = approval is null ? null : RecoveryHoldReleaseCodec.VerifyApprovalSignature(approval, ownerTrust!);
        if (requested is not null && (requested.OwnerId != configuration.Required("OwnerId") ||
            requested.LibraryId != configuration.Required("LibraryId")))
            throw new RecoveryWorkerException("hold_release_scope_invalid");
        var historyBindings = requested?.Kind == "lifecycle"
            ? await RecoveryLifecycleHistoryBindings.ReadAsync(configuration, ct) : null;
        var region = Amazon.RegionEndpoint.GetBySystemName(configuration.RequiredRegion("DestinationRegion"));
        using var sts = new AmazonSecurityTokenServiceClient(region);
        if ((await sts.GetCallerIdentityAsync(new Amazon.SecurityToken.Model.GetCallerIdentityRequest(), ct)).Account !=
            configuration.RequiredAccountId("DestinationAccountId")) throw new RecoveryWorkerException("recovery_task_account_mismatch");
        using var s3 = new AmazonS3Client(region);
        using var dynamo = new AmazonDynamoDBClient(region);
        using var kms = new AmazonKeyManagementServiceClient(region);
        using var secrets = new AmazonSecretsManagerClient(region);
        var signer = await AwsKmsEvidenceSigner.CreateAsync(kms, configuration.Required("SigningKeyId"),
            configuration.RequiredHash("SigningPublicKeySha256"), ct);
        var pointId = RecoveryLatestPointDiscovery.SelectedPoint(configuration)
            ?? throw new RecoveryWorkerException("private_evidence_point_required");
        var selected = await RecoveryLatestPointDiscovery.ReadIndexedPointAsync(dynamo, s3,
            configuration.Required("ControlTableName"), configuration.Required("OwnerId"), configuration.Required("LibraryId"),
            pointId, signer.Trust, ct);
        var pointConfiguration = historyBindings is null ? configuration : new RecoveryWorkerConfiguration(configuration.Mode,
            historyBindings.SingleOrDefault(item => item.PointId == pointId)?.Pins
                ?? throw new RecoveryWorkerException("hold_history_selected_binding_missing"));
        var point = await RecoveryVerifierRunner.VerifyPointAsync(pointConfiguration, s3, selected.Pointer, signer.Trust, ct,
            verifyDatabaseSnapshotBytes: false, forceFullMediaRead: true, historicalOwnerAccess: true);
        await RecoveryVerifierRunner.VerifyReleaseBindingsAsync(pointConfiguration, s3, point, ct);
        var genesis = await RecoveryVerifierRunner.ReadAuditGenesisAsync(configuration, s3, signer.Trust, ct);
        var auditProof = await RecoveryAuditHeadProofStore.ReadAsync(dynamo, configuration.Required("VerificationTableName"),
            pointId, signer.Trust, ct) ?? throw new RecoveryWorkerException("audit_head_proof_missing");
        var requirement = RecoveryAuditHeadProofStore.ValidateForDrill(auditProof, pointConfiguration, point, genesis);
        var directory = Path.Combine(configuration.Optional("ScratchDirectory") ?? Path.GetTempPath(),
            "recovery-private-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = await RecoveryDrillRunner.ReadRecoveryDatabaseAsync(configuration, secrets, directory, ct);
            var dump = point.Receipts.Single(item => item.DataClass == nameof(RecoveryDataClass.DatabaseSnapshot));
            var dumpPath = Path.Combine(directory, "snapshot.dump");
            await RecoveryDrillRunner.DownloadExactFileAsync(s3, dump, dumpPath, ct);
            var pgRestore = configuration.Optional("PgRestorePath") ?? "pg_restore";
            await CaptureRunner.RequirePostgres18Async(pgRestore, ct);
            var name = "recovery_verify_" + Guid.NewGuid().ToString("N");
            await RecoveryDrillRunner.CreateDatabaseAsync(database, name, ct);
            try
            {
                var inventorySha256 = await RecoveryTrustedDumpRestore.RestoreAsync(database, name, dumpPath,
                    pointConfiguration.Optional("MigrationDirectory") ?? "/app/migrations", pointConfiguration.RequiredHash("MigrationDigest"),
                    configuration.Optional("PgDumpPath") ?? "pg_dump", pgRestore, ct);
                var auditHead = await RecoveryDrillRunner.ValidateRestoredPointWithAuditHeadAsync(database, name, point.SourceManifest,
                    signer.Trust, genesis, pointConfiguration.RequiredHash("MigrationDigest"),
                    pointConfiguration.Optional("MigrationDirectory") ?? "/app/migrations", configuration.RequiredAccountId("DestinationAccountId"),
                    requirement, configuration, ct);
                if (approval is null)
                {
                    await using var clone = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString)
                    { Database = name }.ConnectionString);
                    await clone.OpenAsync(ct);
                    await using var transaction = await clone.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                    await RecoverySnapshotPreservation.RequireMediaFreeAsync(clone, transaction,
                        configuration.Optional("MigrationDirectory") ?? "/app/migrations", inventorySha256, ct);
                    await transaction.CommitAsync(ct);
                    var envelope = await RecoverySnapshotPreservation.SignAsync(configuration, signer, signer.Trust, point,
                        inventorySha256, auditHead, DateTimeOffset.UtcNow, ct);
                    await RecoveryPurgeRunner.WritePrivateArtifactAsync(outputPath, RecoveryEvidenceCodec.SerializeCanonical(envelope), ct);
                    return;
                }
                var rowsPath = Path.Combine(directory, "holds.sql");
                await RecoveryPurgeRunner.RunDumpExtractAsync(pgRestore, requested!.Kind + "_holds", dumpPath, rowsPath, ct);
                var candidates = RecoveryPurgeRunner.ReadCopyRows(rowsPath);
                RecoveryHoldReleaseEnvelopeV1? proof = null;
                foreach (var row in candidates)
                {
                    var hold = RecoveryPurgeRunner.ReadHoldObservation(row, requested.Kind, point.SourceManifest.SnapshotCutoffUtc);
                    if (requested!.HoldId != hold.HoldId || requested.DefinitionSha256 != hold.ScopeSha256) continue;
                    (Guid PointId, DateTimeOffset CreatedAtUtc)? lifecycleCreation = null;
                    string? assetOwner = null;
                    if (requested.Kind == "lifecycle")
                    {
                        lifecycleCreation = await ReadLifecycleCreationAsync(configuration, s3, dynamo, signer.Trust,
                            requested, point, historyBindings!, directory, pgRestore, ct);
                        var assetsPath = Path.Combine(directory, "assets.sql");
                        await RecoveryPurgeRunner.RunDumpExtractAsync(pgRestore, "assets", dumpPath, assetsPath, ct);
                        assetOwner = RecoveryPurgeRunner.ReadCopyRows(assetsPath).SingleOrDefault(asset =>
                            asset.GetValueOrDefault("id") == row.GetValueOrDefault("asset_id"))?.GetValueOrDefault("owner_id");
                    }
                    proof = await RecoveryHoldReleaseStore.PersistAsync(dynamo, configuration, signer, signer.Trust,
                        ownerTrust!, point, row, approval, ct, lifecycleCreation, assetOwner);
                    break;
                }
                if (proof is null) throw new RecoveryWorkerException("hold_release_captured_definition_missing");
                await RecoveryPurgeRunner.WritePrivateArtifactAsync(outputPath, RecoveryEvidenceCodec.SerializeCanonical(proof), ct);
            }
            finally { await RecoveryDrillRunner.DropDatabaseAsync(database, name, CancellationToken.None); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(Guid PointId, DateTimeOffset CreatedAtUtc)> ReadLifecycleCreationAsync(
        RecoveryWorkerConfiguration configuration, Amazon.S3.IAmazonS3 s3, Amazon.DynamoDBv2.IAmazonDynamoDB dynamo,
        RecoverySigningTrust trust, RecoveryHoldReleaseApprovalV1 requested, VerifiedCopiedPoint selected,
        IReadOnlyList<RecoveryLifecycleHistoryBinding> bindings, string directory, string pgRestore, CancellationToken ct)
    {
        var inventory = await RecoveryLifecycleHistoryBindings.ReadCompleteInventoryAsync(configuration, dynamo, s3, trust, bindings, ct);
        var history = new List<RecoveryRetentionPointObservationV1>();
        foreach (var pointer in inventory)
        {
            var historical = new RecoveryWorkerConfiguration(configuration.Mode, bindings.Single(item => item.PointId == pointer.PointId).Pins);
            var point = await RecoveryPurgePointVerifier.VerifyAsync(historical, s3, pointer, trust, null, ct);
            var copied = new VerifiedCopiedPoint(pointer, point.SourceManifest, point.FinalManifest, point.Checkpoint,
                point.Receipts, point.CheckpointPayloadSha256, point.CheckpointEnvelopeSha256, null);
            await RecoveryVerifierRunner.VerifyReleaseBindingsAsync(historical, s3, copied, ct);
            var dump = point.Receipts.Single(item => item.DataClass == nameof(RecoveryDataClass.DatabaseSnapshot));
            var path = Path.Combine(directory, "history-" + pointer.PointId.ToString("N") + ".dump");
            await RecoveryDrillRunner.DownloadExactFileAsync(s3, dump, path, ct);
            var holdsPath = Path.Combine(directory, "history-holds.sql");
            await RecoveryPurgeRunner.RunDumpExtractAsync(pgRestore, "lifecycle_holds", path, holdsPath, ct);
            var holds = RecoveryPurgeRunner.ReadCopyRows(holdsPath).Select(row =>
                RecoveryPurgeRunner.ReadHoldObservation(row, "lifecycle", point.SourceManifest.SnapshotCutoffUtc))
                .Where(hold => hold.HoldId == requested.HoldId).ToArray();
            history.Add(new(pointer.PointId, point.SourceManifest.SnapshotCutoffUtc, point.Checkpoint.VerifiedAtUtc,
                point.CheckpointPayloadSha256, point.CheckpointEnvelopeSha256, trust.KeyId,
                null, 0, 0, null, null, holds));
            File.Delete(path);
            File.Delete(holdsPath);
        }
        var rechecked = await RecoveryLifecycleHistoryBindings.ReadCompleteInventoryAsync(configuration, dynamo, s3, trust, bindings, ct);
        if (!RecoveryEvidenceCodec.SerializeCanonical(inventory).AsSpan().SequenceEqual(
                RecoveryEvidenceCodec.SerializeCanonical(rechecked)))
            throw new RecoveryWorkerException("hold_release_history_changed");
        var origin = RetentionService.ResolveLifecycleHoldCreation(requested.HoldId, requested.DefinitionSha256, history);
        if (origin.PointId != selected.Pointer.PointId)
            throw new RecoveryWorkerException("hold_release_earliest_point_required");
        return origin;
    }
}
