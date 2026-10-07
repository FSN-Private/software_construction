using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.ECS;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

/// <summary>Performs a recovery-account-only restore and validates the recovered point.
/// Qualification publication is intentionally a separate step requiring provider-backed
/// access, isolation, alert, cold-storage and purge evidence.</summary>
internal static class RecoveryDrillRunner
{
    private const long MaximumDumpBytes = 95L * 1024 * 1024 * 1024;

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!string.Equals(configuration.Required("RecoveryOnly"), "true", StringComparison.Ordinal))
            throw new RecoveryWorkerException("drill_recovery_only_required");
        if (!Guid.TryParse(configuration.Required("DrillRunId"), out var runId) || runId == Guid.Empty)
            throw new RecoveryWorkerException("drill_run_id_invalid");
        var accessInputs = await RecoveryCandidateAccessRunner.ReadInputsAsync(configuration, cancellationToken);

        var region = configuration.RequiredRegion("DestinationRegion");
        var expectedAccount = configuration.RequiredAccountId("DestinationAccountId");
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
        if (identity.Account != expectedAccount) throw new RecoveryWorkerException("recovery_task_account_mismatch");

        using var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        using var dynamo = new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(region));
        using var kms = new AmazonKeyManagementServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var signer = await AwsKmsEvidenceSigner.CreateAsync(kms, configuration.Required("SigningKeyId"),
            configuration.RequiredHash("SigningPublicKeySha256"), cancellationToken);
        var latest = await RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(dynamo, s3,
            configuration.Required("ControlTableName"), configuration.Required("OwnerId"),
            configuration.Required("LibraryId"), signer.Trust, cancellationToken);
        var auditGenesis = await RecoveryVerifierRunner.ReadAuditGenesisAsync(configuration, s3, signer.Trust,
            cancellationToken);

        var scratchDirectory = configuration.Optional("ScratchDirectory") ?? Path.Combine(Path.GetTempPath(), "recovery-drill");
        Directory.CreateDirectory(scratchDirectory);
        var runDirectory = Path.Combine(scratchDirectory, runId.ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var completedScopes = 0;
        PublishedS3Object? candidateEvidence = null;
        try
        {
            using var secrets = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(region));
            using var ecs = new AmazonECSClient(Amazon.RegionEndpoint.GetBySystemName(region));
            var database = await ReadRecoveryDatabaseAsync(configuration, secrets, runDirectory, cancellationToken);
            var pgDump = configuration.Optional("PgDumpPath") ?? "pg_dump";
            var pgRestore = configuration.Optional("PgRestorePath") ?? "pg_restore";
            await CaptureRunner.RequirePostgres18Async(pgDump, cancellationToken);
            await CaptureRunner.RequirePostgres18Async(pgRestore, cancellationToken);

            foreach (var candidate in new[] { latest })
            {
                var point = await RecoveryVerifierRunner.VerifyPointAsync(configuration, s3,
                    candidate.Pointer, signer.Trust, cancellationToken);
                await RecoveryVerifierRunner.VerifyReleaseBindingsAsync(configuration, s3, point, cancellationToken);
                var auditProof = await RecoveryAuditHeadProofStore.ReadAsync(dynamo,
                    configuration.Required("VerificationTableName"), point.Pointer.PointId, signer.Trust,
                    cancellationToken) ?? throw new RecoveryWorkerException("audit_head_proof_missing");
                var expectedAuditHead = RecoveryAuditHeadProofStore.ValidateForDrill(auditProof,
                    configuration, point, auditGenesis);
                var dumpReceipt = point.Receipts.SingleOrDefault(receipt =>
                    receipt.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString())
                    ?? throw new RecoveryWorkerException("database_snapshot_receipt_missing");
                var dumpPath = Path.Combine(runDirectory, $"{point.Pointer.PointId:N}.dump");
                await DownloadExactFileAsync(s3, dumpReceipt, dumpPath, cancellationToken);
                await RunPostgresToolAsync(pgRestore, ["--list", dumpPath], database, cancellationToken);

                var scratchDatabase = "recovery_drill_" + runId.ToString("N") + "_" + point.Pointer.PointId.ToString("N")[..8];
                await CreateDatabaseAsync(database, scratchDatabase, cancellationToken);
                RecoveryApiTask? apiTask = null;
                var apiLaunchAttempted = false;
                try
                {
                    var restoreArgs = new[]
                    {
                        "--exit-on-error", "--no-owner", "--no-acl", "--dbname", scratchDatabase,
                        "--host", database.Host, "--port", database.Port.ToString(CultureInfo.InvariantCulture),
                        "--username", database.Username, dumpPath
                    };
                    await RunPostgresToolAsync(pgRestore, restoreArgs, database, cancellationToken);
                    await ValidateRestoredPointWithAuditHeadAsync(database, scratchDatabase, point.SourceManifest, signer.Trust,
                        auditGenesis, configuration.RequiredHash("MigrationDigest"),
                        configuration.Optional("MigrationDirectory") ?? "/app/migrations", expectedAccount,
                        expectedAuditHead, configuration, cancellationToken);
                    var candidateConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
                        { Database = scratchDatabase }.ConnectionString;
                    await using var candidateConnection = new NpgsqlConnection(candidateConnectionString);
                    await candidateConnection.OpenAsync(cancellationToken);
                    await RecoveryCandidateStore.CreateAsync(candidateConnection, runId, scratchDatabase,
                        configuration, point, auditProof, cancellationToken);
                    var materialized = await RecoveryMaterializer.MaterializeAsync(s3, configuration, point,
                        new DynamoFenceIntentStore(dynamo, configuration.Required("FenceTableName"),
                            configuration.Required("IntentTableName")), runId, cancellationToken);
                    await RecoveryCandidateStore.RebindAsync(candidateConnection, runId, materialized, cancellationToken);
                    var validationDigest = await ValidateMaterializedCandidateAsync(candidateConnection,
                        point.SourceManifest, materialized, configuration.Required("SourceBucket"),
                        configuration.Required("TargetBucket"), signer.Trust, auditGenesis,
                        configuration.Optional("MigrationDirectory") ?? "/app/migrations",
                        configuration.RequiredHash("MigrationDigest"), expectedAccount, expectedAuditHead,
                        configuration, cancellationToken);
                    var targetDigest = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
                    {
                        database = scratchDatabase, endpoint = database.Host,
                        certificateSha256 = configuration.RequiredHash("DatabaseRootCertificateSha256"),
                        bucket = configuration.Required("TargetBucket"),
                        applicationDigest = configuration.RequiredHash("ApiBuildDigest"), validationDigest
                    }));
                    await RecoveryCandidateStore.RecordValidationAsync(candidateConnection, runId,
                        point.SourceManifest.Digest(), targetDigest, validationDigest, cancellationToken);
                    candidateEvidence = await RecoveryCandidateEvidence.PersistAsync(s3, candidateConnection,
                        configuration, runId, point, materialized, signer, signer.Trust, cancellationToken);
                    if (accessInputs is not null)
                    {
                        var mapping = await RecoveryCandidateAccessRunner.PrepareAsync(secrets, candidateConnection,
                            configuration, runId, accessInputs, cancellationToken);
                        apiLaunchAttempted = true;
                        apiTask = await RecoveryApiTaskRunner.StartAsync(ecs, configuration, runId,
                            scratchDatabase, cancellationToken);
                        var transcript = await RecoveryOwnerAccessProbe.RunAsync(apiTask, Guid.NewGuid(), mapping.Payload,
                            accessInputs.OwnerToken, accessInputs.ForeignToken, cancellationToken);
                        await RecoveryCandidateAccessRunner.PersistTranscriptAsync(s3, configuration, runId,
                            point.Pointer.PointId, mapping, transcript, signer, cancellationToken);
                    }
                    completedScopes++;
                    // A prior source-side access proof cannot qualify this new clone. Fresh
                    // candidate mapping, real probes and independent attestation remain required.
                    const string access = "owner_access_pending";
                    Console.WriteLine($"recovery_drill_restore_validated run={runId:D} point={point.Pointer.PointId:D} owner={RecoveryVerifierRunner.ScopeHash(point.Pointer.OwnerId, point.Pointer.LibraryId)} access={access}");
                }
                finally
                {
                    if (apiTask is not null)
                        await RecoveryApiTaskRunner.StopAsync(ecs, configuration, apiTask.TaskArn, CancellationToken.None);
                    if (apiLaunchAttempted && apiTask is null)
                        throw new RecoveryWorkerException("api_candidate_launch_cleanup_pending");
                    // An unknown launch/stop outcome retains the clone for explicit
                    // reconciliation. Never drop its database under a running API.
                    await DropDatabaseAsync(database, scratchDatabase, CancellationToken.None);
                }
            }

            // This marker records completed restore work, never a healthy qualification.
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = configuration.Required("ControlTableName"),
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = S("RUN#" + runId.ToString("D")),
                    ["sk"] = S("DRILL"),
                    ["runId"] = S(runId.ToString("D")),
                    ["state"] = S("restore_validated_unqualified"),
                    ["pointCount"] = N(completedScopes),
                    ["completedAtUtc"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                    ["workerBuildDigest"] = S(configuration.WorkerBuildDigest),
                    ["reason"] = S("release_evidence_pending"),
                    ["candidateEvidenceKey"] = S(candidateEvidence?.Key ?? throw new RecoveryWorkerException("candidate_evidence_missing")),
                    ["candidateEvidenceVersionId"] = S(candidateEvidence.VersionId),
                    ["candidateEvidenceSha256"] = S(candidateEvidence.Sha256)
                },
                ConditionExpression = "attribute_not_exists(pk) OR #state <> :qualified",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":qualified"] = S("qualified") }
            }, cancellationToken);
            RecoveryWorkerMetrics.Emit("DrillRestoreChecks", completedScopes, configuration);
        }
        finally
        {
            try { Directory.Delete(runDirectory, recursive: true); }
            catch { }
        }
    }

    internal static async Task<RecoveryDatabaseConnection> ReadRecoveryDatabaseAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonSecretsManager secrets,
        string directory,
        CancellationToken cancellationToken)
    {
        var secret = await secrets.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = configuration.Required("DatabaseSecretArn")
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret.SecretString)) throw new RecoveryWorkerException("database_secret_empty");
        using var document = JsonDocument.Parse(secret.SecretString);
        var username = document.RootElement.TryGetProperty("username", out var user) ? user.GetString() : null;
        var password = document.RootElement.TryGetProperty("password", out var pass) ? pass.GetString() : null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new RecoveryWorkerException("database_secret_invalid");
        var rootCertificate = CaptureRunner.WriteDatabaseRootCertificate(configuration, directory);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Required("Database__Host"),
            Port = int.TryParse(configuration.Optional("Database__Port"), out var port) ? port : 5432,
            Database = configuration.Required("Database__Name"),
            Username = username,
            Password = password,
            SslMode = SslMode.VerifyFull,
            RootCertificate = rootCertificate,
            Timeout = 30,
            CommandTimeout = 0,
            Pooling = false,
            ApplicationName = "platform-recovery-drill"
        };
        return new(builder.Host, builder.Port, username, password, rootCertificate, builder.ConnectionString);
    }

    internal static async Task DownloadExactFileAsync(
        IAmazonS3 s3,
        PrivateCopyReceiptV1 receipt,
        string path,
        CancellationToken cancellationToken)
    {
        if (receipt.ByteLength < 0 || receipt.ByteLength > MaximumDumpBytes)
            throw new RecoveryWorkerException("database_dump_size_limit");
        using var response = await s3.GetObjectAsync(new Amazon.S3.Model.GetObjectRequest
        {
            BucketName = receipt.DestinationBucket,
            Key = receipt.DestinationKey,
            VersionId = receipt.DestinationVersionId
        }, cancellationToken);
        if (response.VersionId != receipt.DestinationVersionId || response.ContentLength != receipt.ByteLength)
            throw new RecoveryWorkerException("database_dump_identity_mismatch");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long length = 0;
        int read;
        while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length = checked(length + read);
            if (length > receipt.ByteLength) throw new RecoveryWorkerException("database_dump_length_mismatch");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != receipt.ByteLength || actual != receipt.Sha256)
            throw new RecoveryWorkerException("database_dump_checksum_mismatch");
    }

    internal static async Task CreateDatabaseAsync(RecoveryDatabaseConnection database, string name,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" WITH TEMPLATE template0 ENCODING 'UTF8'", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task DropDatabaseAsync(RecoveryDatabaseConnection database, string name,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task ValidateRestoredPointAsync(
        RecoveryDatabaseConnection credentials,
        string databaseName,
        RecoveryPointManifestV1 manifest,
        RecoverySigningTrust auditTrust,
        VerifiedRecoveryAuditGenesis auditGenesis,
        string expectedMigrationDigest,
        string migrationDirectory,
        string recoveryAccountId,
        CancellationToken cancellationToken)
    {
        _ = await ValidateRestoredPointWithAuditHeadAsync(credentials, databaseName, manifest, auditTrust,
            auditGenesis, expectedMigrationDigest, migrationDirectory, recoveryAccountId,
            expectedAuditHead: null, configuration: null, cancellationToken);
    }

    internal static async Task<RecoveryAuditHeadV1> ValidateRestoredPointWithAuditHeadAsync(
        RecoveryDatabaseConnection credentials,
        string databaseName,
        RecoveryPointManifestV1 manifest,
        RecoverySigningTrust auditTrust,
        VerifiedRecoveryAuditGenesis auditGenesis,
        string expectedMigrationDigest,
        string migrationDirectory,
        string recoveryAccountId,
        RecoveryAuditHeadRequirementV1? expectedAuditHead,
        RecoveryWorkerConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(credentials.ConnectionString) { Database = databaseName };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);

        var auditHead = await RecoveryDatabaseValidator.ValidateSchemaAndGraphWithAuditHeadAsync(connection, transaction,
            manifest.OwnerId, manifest.LibraryId, migrationDirectory, expectedMigrationDigest,
            recoveryAccountId, auditTrust, auditGenesis, expectedAuditHead, cancellationToken);
        var purgeOmissions = configuration is null
            ? new HashSet<RecoveryPurgedReference>()
            : await RecoveryPurgeCapture.ReadAuthorizedOmissionsAsync(connection, transaction, configuration,
                manifest.OwnerId, manifest.LibraryId, DateTimeOffset.UtcNow, cancellationToken);
        var actualObjects = await CaptureRunner.ReadObjectClosureAsync(connection, transaction,
            configuration?.Optional("SourceBucket") ?? string.Empty, manifest.OwnerId, manifest.LibraryId,
            purgeOmissions, cancellationToken);
        RecoveryObjectClosureValidator.RequireExactMatch(manifest.Objects, actualObjects);
        var counts = await CaptureRunner.ReadClassCountsAsync(connection, transaction,
            manifest.OwnerId, manifest.LibraryId, manifest.Objects, cancellationToken);
        foreach (var dataClass in new[]
                 {
                     RecoveryDataClass.VersionGraph, RecoveryDataClass.LibraryMetadata,
                     RecoveryDataClass.AuditRecord, RecoveryDataClass.RecoveryEvidence
                 })
        {
            var expected = manifest.Coverage.Single(item => item.DataClass == dataClass);
            var actual = counts[dataClass];
            if (actual.ItemCount != expected.ItemCount || actual.ByteLength != expected.ByteLength)
                throw new RecoveryWorkerException("restored_database_coverage_mismatch");
        }

        await transaction.CommitAsync(cancellationToken);
        return auditHead;
    }

    internal static async Task<string> ValidateMaterializedCandidateAsync(
        NpgsqlConnection connection, RecoveryPointManifestV1 manifest,
        IReadOnlyList<RecoveryMaterializedObjectV1> receipts, string sourceBucket, string targetBucket,
        RecoverySigningTrust auditTrust, VerifiedRecoveryAuditGenesis auditGenesis,
        string migrationDirectory, string migrationDigest, string recoveryAccountId,
        RecoveryAuditHeadRequirementV1 expectedAuditHead, RecoveryWorkerConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,
            cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        var head = await RecoveryDatabaseValidator.ValidateSchemaAndGraphWithAuditHeadAsync(connection, transaction,
            manifest.OwnerId, manifest.LibraryId, migrationDirectory, migrationDigest, recoveryAccountId,
            auditTrust, auditGenesis, expectedAuditHead, cancellationToken);
        var omissions = configuration is null ? new HashSet<RecoveryPurgedReference>() :
            await RecoveryPurgeCapture.ReadAuthorizedOmissionsAsync(connection, transaction, configuration,
                manifest.OwnerId, manifest.LibraryId, DateTimeOffset.UtcNow, cancellationToken);
        var actualObjects = await CaptureRunner.ReadObjectClosureAsync(connection, transaction, sourceBucket,
            manifest.OwnerId, manifest.LibraryId, omissions, cancellationToken);
        RecoveryMaterializedClosureValidator.RequireExactMappedMatch(manifest.Objects, actualObjects, receipts,
            sourceBucket, targetBucket);
        var counts = await CaptureRunner.ReadClassCountsAsync(connection, transaction,
            manifest.OwnerId, manifest.LibraryId, manifest.Objects, cancellationToken);
        foreach (var dataClass in new[] { RecoveryDataClass.VersionGraph, RecoveryDataClass.LibraryMetadata,
                     RecoveryDataClass.AuditRecord })
        {
            var expected = manifest.Coverage.Single(item => item.DataClass == dataClass);
            var actual = counts[dataClass];
            if (actual.ItemCount != expected.ItemCount || actual.ByteLength != expected.ByteLength)
                throw new RecoveryWorkerException("candidate_database_coverage_mismatch");
        }
        // Candidate sessions/receipts are new recovery evidence. The exact source evidence
        // count was checked before insertion; target receipts are checked separately above.
        var digest = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
        {
            schemaVersion = 1, manifest.RecoveryPointId, manifest.OwnerId, manifest.LibraryId,
            targetBucket, verifiedAuditHead = head,
            receipts = receipts.OrderBy(item => item.SourceBucket, StringComparer.Ordinal)
                .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.SourceVersionId, StringComparer.Ordinal).ToArray(),
            immutableClasses = counts.Where(pair => pair.Key is RecoveryDataClass.VersionGraph or
                RecoveryDataClass.LibraryMetadata or RecoveryDataClass.AuditRecord)
                .OrderBy(pair => pair.Key).ToArray()
        }));
        await transaction.CommitAsync(cancellationToken);
        return digest;
    }

    internal static async Task RunPostgresToolAsync(string executable, IReadOnlyList<string> arguments,
        RecoveryDatabaseConnection database, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = database.Password;
        start.Environment["PGSSLMODE"] = "verify-full";
        start.Environment["PGSSLROOTCERT"] = database.RootCertificatePath;
        start.Environment["PGHOST"] = database.Host;
        start.Environment["PGPORT"] = database.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = database.Username;
        using var process = Process.Start(start) ?? throw new RecoveryWorkerException("postgres_tool_start_failed");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stderr;
        _ = await stdout;
        if (process.ExitCode != 0) throw new RecoveryWorkerException("postgres_tool_failed");
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    internal sealed record RecoveryDatabaseConnection(string Host, int Port, string Username, string Password,
        string RootCertificatePath, string ConnectionString);
}
