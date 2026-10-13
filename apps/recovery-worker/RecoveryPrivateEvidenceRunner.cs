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
            requested.LibraryId != configuration.Required("LibraryId") || requested.Kind != "recovery"))
            throw new RecoveryWorkerException("hold_release_scope_invalid");
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
        var point = await RecoveryVerifierRunner.VerifyPointAsync(configuration, s3, selected.Pointer, signer.Trust, ct,
            verifyDatabaseSnapshotBytes: false, forceFullMediaRead: true, historicalOwnerAccess: true);
        await RecoveryVerifierRunner.VerifyReleaseBindingsAsync(configuration, s3, point, ct);
        var genesis = await RecoveryVerifierRunner.ReadAuditGenesisAsync(configuration, s3, signer.Trust, ct);
        var auditProof = await RecoveryAuditHeadProofStore.ReadAsync(dynamo, configuration.Required("VerificationTableName"),
            pointId, signer.Trust, ct) ?? throw new RecoveryWorkerException("audit_head_proof_missing");
        var requirement = RecoveryAuditHeadProofStore.ValidateForDrill(auditProof, configuration, point, genesis);
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
                    configuration.Optional("MigrationDirectory") ?? "/app/migrations", configuration.RequiredHash("MigrationDigest"),
                    configuration.Optional("PgDumpPath") ?? "pg_dump", pgRestore, ct);
                var auditHead = await RecoveryDrillRunner.ValidateRestoredPointWithAuditHeadAsync(database, name, point.SourceManifest,
                    signer.Trust, genesis, configuration.RequiredHash("MigrationDigest"),
                    configuration.Optional("MigrationDirectory") ?? "/app/migrations", configuration.RequiredAccountId("DestinationAccountId"),
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
                await RecoveryPurgeRunner.RunDumpExtractAsync(pgRestore, "recovery_holds", dumpPath, rowsPath, ct);
                var candidates = RecoveryPurgeRunner.ReadCopyRows(rowsPath).Where(row =>
                    row.GetValueOrDefault("owner_id") == point.Pointer.OwnerId && row.GetValueOrDefault("library_id") == point.Pointer.LibraryId);
                RecoveryHoldReleaseEnvelopeV1? proof = null;
                foreach (var row in candidates)
                {
                    var hold = RecoveryPurgeRunner.ReadHoldObservation(row, "recovery", point.SourceManifest.SnapshotCutoffUtc);
                    if (requested!.HoldId != hold.HoldId || requested.DefinitionSha256 != hold.ScopeSha256) continue;
                    proof = await RecoveryHoldReleaseStore.PersistAsync(dynamo, configuration, signer, signer.Trust,
                        ownerTrust!, point, row, approval, ct);
                    break;
                }
                if (proof is null) throw new RecoveryWorkerException("hold_release_captured_definition_missing");
                await RecoveryPurgeRunner.WritePrivateArtifactAsync(outputPath, RecoveryEvidenceCodec.SerializeCanonical(proof), ct);
            }
            finally { await RecoveryDrillRunner.DropDatabaseAsync(database, name, CancellationToken.None); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
