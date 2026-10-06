using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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
        var points = await RecoveryVerifierRunner.ReadCopiedPointsAsync(dynamo,
            configuration.Required("ControlTableName"), cancellationToken);
        if (points.Count == 0) throw new RecoveryWorkerException("recovery_copy_missing");

        var candidates = new List<(CopyPointer Pointer, RecoveryPointManifestV1 Manifest)>();
        foreach (var point in points)
            candidates.Add((point, await RecoveryVerifierRunner.ReadSourceManifestAsync(
                s3, point, signer.Trust, cancellationToken)));
        var latest = candidates.GroupBy(item => RecoveryVerifierRunner.ScopeHash(item.Pointer.OwnerId, item.Pointer.LibraryId), StringComparer.Ordinal)
            .Select(RecoveryVerifierRunner.SelectLatest).ToArray();

        var scratchDirectory = configuration.Optional("ScratchDirectory") ?? Path.Combine(Path.GetTempPath(), "recovery-drill");
        Directory.CreateDirectory(scratchDirectory);
        var runDirectory = Path.Combine(scratchDirectory, runId.ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var completedScopes = 0;
        try
        {
            using var secrets = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(region));
            var database = await ReadRecoveryDatabaseAsync(configuration, secrets, runDirectory, cancellationToken);
            var pgDump = configuration.Optional("PgDumpPath") ?? "pg_dump";
            var pgRestore = configuration.Optional("PgRestorePath") ?? "pg_restore";
            await CaptureRunner.RequirePostgres18Async(pgDump, cancellationToken);
            await CaptureRunner.RequirePostgres18Async(pgRestore, cancellationToken);

            foreach (var candidate in latest)
            {
                var point = await RecoveryVerifierRunner.VerifyPointAsync(configuration, s3,
                    candidate.Pointer, signer.Trust, cancellationToken);
                var dumpReceipt = point.Receipts.SingleOrDefault(receipt =>
                    receipt.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString())
                    ?? throw new RecoveryWorkerException("database_snapshot_receipt_missing");
                var dumpPath = Path.Combine(runDirectory, $"{point.Pointer.PointId:N}.dump");
                await DownloadExactFileAsync(s3, dumpReceipt, dumpPath, cancellationToken);
                await RunPostgresToolAsync(pgRestore, ["--list", dumpPath], database, cancellationToken);

                var scratchDatabase = "recovery_drill_" + runId.ToString("N") + "_" + point.Pointer.PointId.ToString("N")[..8];
                await CreateDatabaseAsync(database, scratchDatabase, cancellationToken);
                try
                {
                    var restoreArgs = new[]
                    {
                        "--exit-on-error", "--no-owner", "--no-acl", "--dbname", scratchDatabase,
                        "--host", database.Host, "--port", database.Port.ToString(CultureInfo.InvariantCulture),
                        "--username", database.Username, dumpPath
                    };
                    await RunPostgresToolAsync(pgRestore, restoreArgs, database, cancellationToken);
                    await ValidateRestoredPointAsync(database, scratchDatabase, point.SourceManifest, cancellationToken);
                    completedScopes++;
                    var access = point.OwnerAccess is null ? "owner_access_pending" : "provider_release_evidence_pending";
                    Console.WriteLine($"recovery_drill_restore_validated run={runId:D} point={point.Pointer.PointId:D} owner={RecoveryVerifierRunner.ScopeHash(point.Pointer.OwnerId, point.Pointer.LibraryId)} access={access}");
                }
                finally
                {
                    await DropDatabaseAsync(database, scratchDatabase, cancellationToken);
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
                    ["reason"] = S("release_evidence_pending")
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
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(credentials.ConnectionString) { Database = databaseName };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);

        var requiredTables = new[]
        {
            "assets", "content_objects", "edit_identities", "asset_paths", "audit_events", "vc_commits",
            "vc_commit_parents", "vc_commit_entries", "vc_refs", "vc_reflog", "vc_working_changes",
            "vc_stashes", "recovery_points", "recovery_change_ledger", "recovery_change_watermarks"
        };
        await using (var tables = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name=ANY($1)", connection, transaction))
        {
            tables.Parameters.AddWithValue(requiredTables);
            var found = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await tables.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) found.Add(reader.GetString(0));
            if (requiredTables.Any(table => !found.Contains(table)))
                throw new RecoveryWorkerException("restored_database_schema_incomplete");
        }

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

        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM vc_commit_parents p
            JOIN vc_commits child ON child.id=p.commit_id
            LEFT JOIN vc_commits parent ON parent.id=p.parent_id AND parent.owner_id=child.owner_id AND parent.library_id=child.library_id
            WHERE child.owner_id=$1 AND child.library_id=$2 AND parent.id IS NULL LIMIT 1
            """, manifest.OwnerId, manifest.LibraryId, "restored_version_graph_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM vc_refs r LEFT JOIN vc_commits c
              ON c.id=r.head_commit_id AND c.owner_id=r.owner_id AND c.library_id=r.library_id
            WHERE r.owner_id=$1 AND r.library_id=$2 AND r.head_commit_id IS NOT NULL AND c.id IS NULL LIMIT 1
            """, manifest.OwnerId, manifest.LibraryId, "restored_version_graph_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM vc_commit_entries e
            JOIN vc_commits c ON c.id=e.commit_id
            LEFT JOIN assets a ON a.id=e.asset_id AND a.owner_id=c.owner_id
            LEFT JOIN edit_identities i ON i.id=e.edit_id AND i.asset_id=e.asset_id
            WHERE c.owner_id=$1 AND c.library_id=$2 AND (a.id IS NULL OR (e.edit_id IS NOT NULL AND i.id IS NULL)) LIMIT 1
            """, manifest.OwnerId, manifest.LibraryId, "restored_version_graph_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM assets a LEFT JOIN content_objects o ON o.sha256=a.original_sha256
            WHERE a.owner_id=$1 AND a.original_sha256 IS NOT NULL
              AND (o.sha256 IS NULL OR o.integrity_state<>'authoritative') LIMIT 1
            """, manifest.OwnerId, manifest.LibraryId, "restored_media_metadata_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM edit_identities i JOIN assets a ON a.id=i.asset_id
            LEFT JOIN content_objects o ON o.sha256=i.xmp_sha256
            WHERE a.owner_id=$1 AND i.xmp_sha256 IS NOT NULL
              AND (o.sha256 IS NULL OR o.integrity_state<>'authoritative') LIMIT 1
            """, manifest.OwnerId, manifest.LibraryId, "restored_xmp_metadata_invalid", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RequireNoRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, string owner, string library, string failureCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(library);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            throw new RecoveryWorkerException(failureCode);
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
