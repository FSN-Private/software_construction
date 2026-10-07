using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class CaptureRunner
{
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var region = configuration.RequiredRegion("SourceRegion");
        var bucket = configuration.Required("PrimaryArtifactsBucket");
        var prefix = NormalizePrefix(configuration.Optional("PrimaryArtifactsPrefix") ?? "captures/");
        var sourceBucket = configuration.Required("SourceBucket");
        var scratch = configuration.Optional("ScratchDirectory") ?? Path.Combine(Path.GetTempPath(), "recovery-capture");
        Directory.CreateDirectory(scratch);
        var runDirectory = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);

        var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        using var secrets = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(region));
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        try
        {
            var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            var sourceAccountId = RequireAccountId(identity.Account);
            var captureId = Guid.NewGuid();
            var capturePrefix = $"{prefix}{captureId:D}/";
            await WriteMarkerAsync(s3, bucket, capturePrefix + "capture-start.json",
                new CaptureMarkerV1(1, captureId, DateTimeOffset.UtcNow, sourceAccountId, region, configuration.WorkerBuildDigest), cancellationToken);

            try
            {
                var rootCertificatePath = WriteDatabaseRootCertificate(configuration, runDirectory);
                var connection = await ReadConnectionAsync(configuration, secrets, rootCertificatePath, cancellationToken);
                await using var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
                await using var database = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                await using (var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", database, transaction))
                    await setReadOnly.ExecuteNonQueryAsync(cancellationToken);

                var (snapshotId, snapshotCutoffUtc, snapshotLsn) = await ExportSnapshotAsync(database, transaction, cancellationToken);
                var scopes = await ReadScopesAsync(database, transaction,
                    configuration.Optional("OwnerId"), configuration.Optional("LibraryId"), cancellationToken);
                if (scopes.Count == 0) throw new RecoveryWorkerException("recovery_scope_missing");
                var scopeData = new List<CapturedScope>(scopes.Count);
                foreach (var scope in scopes)
                {
                    var purgeOmissions = await RecoveryPurgeCapture.ReadAuthorizedOmissionsAsync(
                        database, transaction, configuration, scope.OwnerId, scope.LibraryId,
                        DateTimeOffset.UtcNow, cancellationToken);
                    var watermark = await ReadAndVerifyWatermarkAsync(database, transaction, scope.OwnerId, scope.LibraryId, snapshotCutoffUtc, cancellationToken);
                    var objects = await ReadObjectClosureAsync(database, transaction, sourceBucket, scope.OwnerId,
                        scope.LibraryId, purgeOmissions, cancellationToken);
                    var counts = await ReadClassCountsAsync(database, transaction, scope.OwnerId, scope.LibraryId, objects, cancellationToken);
                    scopeData.Add(new(scope, watermark, objects, counts));
                }

                var migrationDigest = await RecoveryWorkerConfiguration.ComputeMigrationDigestAsync(
                    configuration.Optional("MigrationDirectory") ?? "/app/migrations", cancellationToken);
                var buildBinding = ReadBuildBinding(configuration, migrationDigest, sourceAccountId, region);
                var dumpPath = Path.Combine(runDirectory, "database.dump");
                await CreateDatabaseDumpAsync(configuration, connection, snapshotId, dumpPath, cancellationToken);
                var pgRestore = configuration.Optional("PgRestorePath") ?? "pg_restore";
                await RequirePostgres18Async(pgRestore, cancellationToken);
                await RunProcessAsync(pgRestore, ["--list", dumpPath], connection.Password, connection, null, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                var dumpObject = await S3ArtifactWriter.UploadFileAsync(s3, bucket, capturePrefix + "database.dump", dumpPath,
                    configuration.Optional("PrimaryArtifactsKmsKeyId"), cancellationToken);

                var manifestObjects = new List<CaptureManifestPointerV1>(scopeData.Count);
                foreach (var data in scopeData)
                {
                    var scopeHash = ScopeHash(data.Scope.OwnerId, data.Scope.LibraryId);
                    var scopePrefix = capturePrefix + "scopes/" + scopeHash + "/";
                    var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new CaptureInventoryV1(
                        1, captureId, data.Scope.OwnerId, data.Scope.LibraryId, data.Objects), ArtifactJson);
                    var inventory = await S3ArtifactWriter.PutBytesAsync(s3, bucket, scopePrefix + "object-inventory.json",
                        inventoryBytes, configuration.Optional("PrimaryArtifactsKmsKeyId"), cancellationToken);

                    var configBytes = JsonSerializer.SerializeToUtf8Bytes(new CaptureConfigurationV1(
                        1, buildBinding.Environment, buildBinding.SourceRevision, buildBinding.SourceAccountId,
                        buildBinding.SourceRegion, buildBinding.MigrationDigest, buildBinding.ConfigurationDigest,
                        configuration.RequiredHash("DatabaseRootCertificateSha256")), ArtifactJson);
                    var configObject = await S3ArtifactWriter.PutBytesAsync(s3, bucket, scopePrefix + "configuration.json",
                        configBytes, configuration.Optional("PrimaryArtifactsKmsKeyId"), cancellationToken);
                    var releaseBytes = JsonSerializer.SerializeToUtf8Bytes(new CaptureReleaseBindingV1(
                        1, buildBinding.ApiBuildDigest, buildBinding.IngestWorkerBuildDigest,
                        buildBinding.RecoveryWorkerBuildDigest, buildBinding.RendererBuildDigest,
                        buildBinding.SourceRevision), ArtifactJson);
                    var releaseObject = await S3ArtifactWriter.PutBytesAsync(s3, bucket, scopePrefix + "release-bindings.json",
                        releaseBytes, configuration.Optional("PrimaryArtifactsKmsKeyId"), cancellationToken);

                    var coverage = BuildManifestCoverage(data, dumpObject, inventory, configObject, releaseObject);
                    var capturedAtUtc = DateTimeOffset.UtcNow;
                    var manifest = new RecoveryPointManifestV1(
                        RecoveryPointManifestV1.CurrentSchemaVersion,
                        Guid.NewGuid(), captureId, data.Scope.OwnerId, data.Scope.LibraryId, region,
                        capturedAtUtc, snapshotCutoffUtc, snapshotId, snapshotLsn, migrationDigest,
                        data.Watermark,
                        [
                            Component("database.dump", RecoveryDataClass.DatabaseSnapshot, dumpObject, bucket),
                            Component("object-inventory.json", RecoveryDataClass.ObjectInventory, inventory, bucket),
                            Component("configuration.json", RecoveryDataClass.Configuration, configObject, bucket),
                            Component("release-bindings.json", RecoveryDataClass.ReleaseArtifact, releaseObject, bucket)
                        ],
                        data.Objects,
                        coverage);
                    var manifestBytes = manifest.SerializeCanonical();
                    var manifestKey = scopePrefix + "manifest.json";
                    var manifestObject = await S3ArtifactWriter.PutBytesAsync(s3, bucket, manifestKey, manifestBytes,
                        configuration.Optional("PrimaryArtifactsKmsKeyId"), cancellationToken);
                    manifestObjects.Add(new(manifestKey, manifestObject.VersionId, manifestObject.Sha256, manifestObject.ByteLength));
                }

                var completed = await WriteMarkerAsync(s3, bucket, capturePrefix + "capture-complete.json",
                    new CaptureCompleteV1(1, captureId, DateTimeOffset.UtcNow, manifestObjects.Count, manifestObjects), cancellationToken);
                _ = completed;
                RecoveryWorkerMetrics.Emit("CaptureFailures", 0, configuration);
                Console.WriteLine($"recovery_capture_completed capture={captureId:D} scopes={manifestObjects.Count}");
            }
            catch (Exception exception)
            {
                var safeCode = exception is RecoveryWorkerException workerException ? workerException.SafeCode : "capture_failed";
                try
                {
                    await WriteMarkerAsync(s3, bucket, capturePrefix + "capture-failure.json",
                        new CaptureFailureV1(1, captureId, DateTimeOffset.UtcNow, safeCode, configuration.WorkerBuildDigest), cancellationToken);
                }
                catch { }
                if (exception is RecoveryWorkerException) throw;
                throw new RecoveryWorkerException(safeCode, exception);
            }
        }
        finally
        {
            s3.Dispose();
            try { Directory.Delete(runDirectory, recursive: true); }
            catch { }
        }
    }

    private static async Task<DatabaseConnection> ReadConnectionAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonSecretsManager secrets,
        string rootCertificatePath,
        CancellationToken cancellationToken)
    {
        var secret = await secrets.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = configuration.Required("SourceDatabaseSecretArn")
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret.SecretString)) throw new RecoveryWorkerException("database_secret_empty");
        using var document = JsonDocument.Parse(secret.SecretString);
        var root = document.RootElement;
        var username = root.TryGetProperty("username", out var user) ? user.GetString() : null;
        var password = root.TryGetProperty("password", out var pass) ? pass.GetString() : null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new RecoveryWorkerException("database_secret_invalid");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Required("Database__Host"),
            Port = int.TryParse(configuration.Optional("Database__Port"), out var port) ? port : 5432,
            Database = configuration.Required("Database__Name"),
            Username = username,
            Password = password,
            SslMode = SslMode.VerifyFull,
            RootCertificate = rootCertificatePath,
            Timeout = 30,
            CommandTimeout = 0,
            Pooling = false,
            ApplicationName = "platform-recovery-capture"
        };
        return new(builder.ConnectionString, password, builder.Host, builder.Port, builder.Database, builder.Username, rootCertificatePath);
    }

    internal static string WriteDatabaseRootCertificate(RecoveryWorkerConfiguration configuration, string directory)
    {
        var pem = configuration.Required("DatabaseRootCertificate");
        var bytes = Encoding.UTF8.GetBytes(pem);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (digest != configuration.RequiredHash("DatabaseRootCertificateSha256"))
            throw new RecoveryWorkerException("database_root_certificate_digest_mismatch");
        try
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(pem);
            if (certificates.Count == 0) throw new CryptographicException("empty_pem_bundle");
        }
        catch (CryptographicException exception)
        {
            throw new RecoveryWorkerException("database_root_certificate_invalid", exception);
        }
        var path = Path.Combine(directory, "rds-root-bundle.pem");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal static async Task<(string SnapshotId, DateTimeOffset CutoffUtc, string Lsn)> ExportSnapshotAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_export_snapshot(), statement_timestamp(), pg_current_wal_lsn()::text", database, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RecoveryWorkerException("snapshot_export_failed");
        var snapshot = reader.GetString(0);
        var cutoff = reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime();
        var lsn = reader.GetString(2);
        if (string.IsNullOrWhiteSpace(snapshot) || !IsLsn(lsn)) throw new RecoveryWorkerException("snapshot_export_invalid");
        return (snapshot, cutoff, lsn);
    }

    private static async Task<List<RecoveryScope>> ReadScopesAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string? configuredOwnerId,
        string? configuredLibraryId,
        CancellationToken cancellationToken)
    {
        if (configuredOwnerId is not null && configuredLibraryId is null)
            throw new RecoveryWorkerException("configured_scope_incomplete");
        var scopes = new HashSet<RecoveryScope>();
        await using var command = new NpgsqlCommand(
            "SELECT owner_id, library_id FROM recovery_change_watermarks ORDER BY owner_id, library_id", database, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            scopes.Add(new(reader.GetString(0), reader.GetString(1)));
        await reader.CloseAsync();
        if (configuredLibraryId is not null &&
            (string.IsNullOrWhiteSpace(configuredLibraryId) || configuredLibraryId.Length > 200))
            throw new RecoveryWorkerException("configured_scope_invalid");
        if (configuredOwnerId is not null && configuredLibraryId is not null)
        {
            if (string.IsNullOrWhiteSpace(configuredOwnerId) || configuredOwnerId.Length > 200)
                throw new RecoveryWorkerException("configured_scope_invalid");
            scopes.Add(new(configuredOwnerId, configuredLibraryId));
        }
        return scopes.OrderBy(scope => scope.OwnerId, StringComparer.Ordinal)
            .ThenBy(scope => scope.LibraryId, StringComparer.Ordinal).ToList();
    }

    internal static async Task<RecoveryChangeWatermark> ReadAndVerifyWatermarkAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string ownerId,
        string libraryId,
        DateTimeOffset snapshotCutoffUtc,
        CancellationToken cancellationToken)
    {
        await using var watermarkCommand = new NpgsqlCommand(
            "SELECT last_sequence, latest_changed_at_utc FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id=$2",
            database, transaction);
        watermarkCommand.Parameters.AddWithValue(ownerId);
        watermarkCommand.Parameters.AddWithValue(libraryId);
        await using var watermarkReader = await watermarkCommand.ExecuteReaderAsync(cancellationToken);
        if (!await watermarkReader.ReadAsync(cancellationToken)) throw new RecoveryWorkerException("change_watermark_missing");
        var sequence = watermarkReader.GetInt64(0);
        var latest = watermarkReader.IsDBNull(1) ? (DateTimeOffset?)null : watermarkReader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime();
        await watermarkReader.CloseAsync();

        await using var ledgerCommand = new NpgsqlCommand(
            "SELECT count(*), coalesce(max(sequence),0), max(changed_at_utc) FROM recovery_change_ledger WHERE owner_id=$1 AND library_id=$2",
            database, transaction);
        ledgerCommand.Parameters.AddWithValue(ownerId);
        ledgerCommand.Parameters.AddWithValue(libraryId);
        await using var ledgerReader = await ledgerCommand.ExecuteReaderAsync(cancellationToken);
        if (!await ledgerReader.ReadAsync(cancellationToken)) throw new RecoveryWorkerException("change_ledger_missing");
        var count = ledgerReader.GetInt64(0);
        var maxSequence = ledgerReader.GetInt64(1);
        var ledgerLatest = ledgerReader.IsDBNull(2) ? (DateTimeOffset?)null : ledgerReader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime();
        if (count != sequence || maxSequence != sequence || ledgerLatest != latest)
            throw new RecoveryWorkerException("change_ledger_incomplete");
        var result = new RecoveryChangeWatermark(sequence, latest);
        result.Validate(snapshotCutoffUtc);
        return result;
    }

    internal static async Task<IReadOnlyList<RecoveryObjectReference>> ReadObjectClosureAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string sourceBucket,
        string ownerId,
        string libraryId,
        IReadOnlySet<RecoveryPurgedReference> authorizedOmissions,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH refs AS (
                SELECT a.owner_id, $2::text AS library_id, a.id AS asset_id, NULL::uuid AS edit_id,
                       NULL::text AS commit_id, a.original_sha256 AS sha256, 'original'::text AS object_class
                  FROM assets a WHERE a.owner_id=$1 AND a.original_sha256 IS NOT NULL
                UNION ALL
                SELECT a.owner_id, $2::text, a.id, ei.id, NULL::text, ei.xmp_sha256, 'xmp'::text
                  FROM edit_identities ei JOIN assets a ON a.id=ei.asset_id
                 WHERE a.owner_id=$1 AND ei.xmp_sha256 IS NOT NULL
                UNION ALL
                SELECT c.owner_id, c.library_id, e.asset_id, e.edit_id, e.commit_id::text,
                       e.content_sha256, CASE WHEN e.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
                 WHERE c.owner_id=$1 AND c.library_id=$2 AND e.content_sha256 IS NOT NULL
                UNION ALL
                SELECT c.owner_id, c.library_id, e.asset_id, e.edit_id, e.commit_id::text,
                       e.base_content_sha256, CASE WHEN e.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
                 WHERE c.owner_id=$1 AND c.library_id=$2 AND e.base_content_sha256 IS NOT NULL
                UNION ALL
                SELECT w.owner_id, w.library_id, w.asset_id, w.edit_id, w.base_commit_id::text,
                       w.current_content_sha256, CASE WHEN w.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_working_changes w
                 WHERE w.owner_id=$1 AND w.library_id=$2 AND w.current_content_sha256 IS NOT NULL
                UNION ALL
                SELECT w.owner_id, w.library_id, w.asset_id, w.edit_id, w.base_commit_id::text,
                       w.base_content_sha256, CASE WHEN w.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_working_changes w
                 WHERE w.owner_id=$1 AND w.library_id=$2 AND w.base_content_sha256 IS NOT NULL
                UNION ALL
                SELECT l.owner_id, l.library_id, l.asset_id, l.edit_id, l.target_commit_id::text,
                       l.sha256, 'xmp'::text
                  FROM lineage_merge_content l
                 WHERE l.owner_id=$1 AND l.library_id=$2
                UNION ALL
                SELECT l.owner_id, l.library_id, l.asset_id, l.edit_id, l.target_commit_id::text,
                       provenance.sha256, 'xmp'::text
                  FROM lineage_merge_content l
                  CROSS JOIN LATERAL (VALUES (l.base_sha256), (l.target_sha256), (l.source_sha256)) AS provenance(sha256)
                 WHERE l.owner_id=$1 AND l.library_id=$2 AND provenance.sha256 IS NOT NULL
                UNION ALL
                SELECT s.owner_id, s.library_id, w.asset_id, w.edit_id, w.base_commit_id::text,
                       w.current_content_sha256, CASE WHEN w.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_stashes s
                  CROSS JOIN LATERAL jsonb_to_recordset(s.working_snapshot->'working') AS w(
                      asset_id uuid, edit_id uuid, base_commit_id char(64), current_content_sha256 char(64))
                 WHERE s.owner_id=$1 AND s.library_id=$2 AND w.current_content_sha256 IS NOT NULL
                UNION ALL
                SELECT s.owner_id, s.library_id, w.asset_id, w.edit_id, w.base_commit_id::text,
                       w.base_content_sha256, CASE WHEN w.edit_id IS NULL THEN 'original' ELSE 'xmp' END
                  FROM vc_stashes s
                  CROSS JOIN LATERAL jsonb_to_recordset(s.working_snapshot->'working') AS w(
                      asset_id uuid, edit_id uuid, base_commit_id char(64), base_content_sha256 char(64))
                 WHERE s.owner_id=$1 AND s.library_id=$2 AND w.base_content_sha256 IS NOT NULL
                UNION ALL
                SELECT a.owner_id, $2::text, a.id, NULL::uuid, NULL::text, pii.content_sha256,
                       CASE WHEN co.category='xmp' THEN 'xmp' ELSE 'original' END
                  FROM publication_intent_items pii
                  JOIN publication_intents pi ON pi.id=pii.intent_id
                  JOIN assets a ON a.id=pii.asset_id
                  JOIN content_objects co ON co.sha256=pii.content_sha256
                 WHERE a.owner_id=$1 AND pi.state IN ('pending-publication','publishing')
            )
            SELECT r.owner_id, r.library_id, r.asset_id, r.edit_id, r.commit_id, r.object_class,
                   r.sha256, co.byte_length, co.storage_key, co.storage_version, co.category, co.integrity_state
              FROM refs r LEFT JOIN content_objects co ON co.sha256=r.sha256
             ORDER BY co.sha256, co.storage_key, co.storage_version, r.asset_id, r.edit_id, r.commit_id
            """;
        await using var command = new NpgsqlCommand(sql, database, transaction);
        command.Parameters.AddWithValue(ownerId);
        command.Parameters.AddWithValue(libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ObjectRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectClass = reader.GetString(5) switch
            {
                "original" => RecoveryDataClass.OriginalImage,
                "xmp" => RecoveryDataClass.XmpSidecar,
                _ => throw new RecoveryWorkerException("referenced_object_class_invalid")
            };
            var rowOwner = reader.GetString(0);
            var rowLibrary = reader.GetString(1);
            var assetId = reader.GetGuid(2);
            Guid? editId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            var digest = reader.GetString(6).Trim();
            var isAuthorizedOmission = authorizedOmissions.Contains(new RecoveryPurgedReference(rowOwner, rowLibrary,
                assetId, editId, objectClass == RecoveryDataClass.XmpSidecar ? "xmp" : "original", digest));
            var hasObject = !reader.IsDBNull(7) || !reader.IsDBNull(8) || !reader.IsDBNull(9) ||
                !reader.IsDBNull(10) || !reader.IsDBNull(11);
            if (!hasObject && isAuthorizedOmission) continue;
            if (reader.IsDBNull(10) || reader.IsDBNull(11))
                throw new RecoveryWorkerException("referenced_object_incomplete");
            var expectedCategory = objectClass == RecoveryDataClass.XmpSidecar ? "xmp" : "original";
            if (!string.Equals(reader.GetString(10), expectedCategory, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(11), "authoritative", StringComparison.Ordinal))
                throw new RecoveryWorkerException("referenced_object_unavailable");
            if (reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9))
                throw new RecoveryWorkerException("referenced_object_incomplete");
            rows.Add(new ObjectRow(
                objectClass,
                rowOwner, rowLibrary, assetId,
                editId, reader.IsDBNull(4) ? null : reader.GetString(4),
                digest, reader.GetInt64(7), reader.GetString(8), reader.GetString(9)));
        }

        var objects = new List<RecoveryObjectReference>();
        foreach (var group in rows.GroupBy(row => (row.DataClass, row.Sha256, row.ByteLength, row.SourceKey, row.SourceVersionId)))
        {
            var logicalReferences = group.Select(row => new RecoveryLogicalReference(
                    row.OwnerId, row.LibraryId, row.AssetId, row.EditId, row.CommitId))
                .Distinct()
                .ToArray();
            var first = group.First();
            var item = new RecoveryObjectReference(first.DataClass, first.OwnerId, first.LibraryId,
                first.SourceKey, first.SourceVersionId, first.Sha256, first.ByteLength, logicalReferences);
            item.Validate();
            objects.Add(item);
        }
        if (objects.Select(item => (item.SourceKey, item.SourceVersionId)).Distinct().Count() != objects.Count)
            throw new RecoveryWorkerException("storage_class_collision");
        _ = sourceBucket;
        return objects;
    }

    internal static async Task<Dictionary<RecoveryDataClass, CountBytes>> ReadClassCountsAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string ownerId,
        string libraryId,
        IReadOnlyList<RecoveryObjectReference> objects,
        CancellationToken cancellationToken)
    {
        await using (var orphanCheck = new NpgsqlCommand("""
            SELECT 1 FROM recovery_audit_checkpoints c
            LEFT JOIN recovery_audit_events e ON e.sequence=c.sequence AND e.event_sha256=c.event_sha256
            WHERE e.sequence IS NULL LIMIT 1
            """, database, transaction))
        {
            if (await orphanCheck.ExecuteScalarAsync(cancellationToken) is not null)
                throw new RecoveryWorkerException("recovery_audit_checkpoint_orphan");
        }

        var counts = Enum.GetValues<RecoveryDataClass>()
            .Where(value => value != RecoveryDataClass.PreviewDerivative)
            .ToDictionary(value => value, _ => new CountBytes(0, 0));
        counts[RecoveryDataClass.OriginalImage] = new(
            objects.LongCount(item => item.DataClass == RecoveryDataClass.OriginalImage),
            objects.Where(item => item.DataClass == RecoveryDataClass.OriginalImage).Sum(item => item.ByteLength));
        counts[RecoveryDataClass.XmpSidecar] = new(
            objects.LongCount(item => item.DataClass == RecoveryDataClass.XmpSidecar),
            objects.Where(item => item.DataClass == RecoveryDataClass.XmpSidecar).Sum(item => item.ByteLength));

        counts[RecoveryDataClass.VersionGraph] = await SumTablesAsync(database, transaction, ownerId, libraryId,
            VersionGraphTables, cancellationToken);
        counts[RecoveryDataClass.LibraryMetadata] = await SumTablesAsync(database, transaction, ownerId, libraryId,
            LibraryMetadataTables, cancellationToken);
        counts[RecoveryDataClass.AuditRecord] = await CountTableAsync(database, transaction,
            "SELECT count(*), coalesce(sum(pg_column_size(a)),0) FROM audit_events a WHERE a.owner_id=$1", ownerId, null, cancellationToken);
        counts[RecoveryDataClass.RecoveryEvidence] = await SumTablesAsync(database, transaction, ownerId, libraryId,
            RecoveryEvidenceTables, cancellationToken);
        return counts;
    }

    internal static IReadOnlyList<string> VersionGraphTables =>
    [
        "vc_commits", "vc_commit_parents", "vc_commit_entries", "vc_refs", "vc_reflog", "vc_visibility",
        "vc_materialized_state", "vc_working_changes", "vc_staging_sets", "vc_staged_entries", "vc_divergence",
        "vc_idempotency", "vc_ref_quarantine", "vc_checkouts", "vc_stashes", "vc_recovery_expirations",
        "vc_lineage_reachable", "lineage_merge_content"
    ];

    internal static IReadOnlyList<string> LibraryMetadataTables =>
    [
        "assets", "edit_identities", "asset_paths", "lifecycle_holds", "import_batches", "idempotency_results",
        "upload_sessions", "import_batch_items", "publication_intents", "publication_intent_items",
        "ingest_publications", "asset_mutations", "reconstruction_manifests", "rendering_compatibility",
        "rendering_compatibility_candidates", "render_requests", "render_attempts", "renditions",
        "render_provenance", "render_events", "render_intake_control", "windows_sync_devices",
        "windows_sync_content_ids", "windows_sync_device_content", "windows_sync_owner_revisions",
        "gallery_generations", "gallery_projection", "gallery_facet_values", "gallery_refresh",
        "gallery_preferences", "gallery_web_overlay_revisions", "gallery_web_content", "gallery_edit_idempotency",
        "gallery_preference_idempotency", "export_baskets", "export_basket_items", "export_requests",
        "export_results", "export_purge_tombstones", "export_write_intents", "export_download_sessions",
        "export_download_grants", "export_download_admissions", "export_operations"
    ];

    internal static IReadOnlyList<string> RecoveryEvidenceTables =>
    [
        "platform_schema_migrations",
        "recovery_points", "recovery_point_attestations", "recovery_cycle_statuses", "recovery_purge_freezes",
        "recovery_point_components",
        "recovery_point_objects", "recovery_observations", "recovery_observation_rejections", "recovery_write_intents",
        "recovery_change_watermarks", "recovery_change_ledger", "recovery_sessions", "recovery_validation_revisions",
        "recovery_materialization_receipts", "recovery_holds", "media_purge_tombstones", "recovery_purge_proposals",
        "recovery_purge_journals", "recovery_purge_completions", "recovery_purge_objects", "recovery_identity_mappings",
        "recovery_audit_state", "recovery_audit_events", "recovery_audit_checkpoints"
    ];

    internal static IReadOnlyList<IReadOnlyList<string>> ProtectedRelationalTableClasses =>
    [
        VersionGraphTables,
        LibraryMetadataTables,
        ["audit_events"],
        RecoveryEvidenceTables
    ];

    private static async Task<CountBytes> SumTablesAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string ownerId,
        string libraryId,
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken)
    {
        long itemCount = 0, byteLength = 0;
        foreach (var table in tableNames)
        {
            var query = table switch
            {
                "assets" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM assets t WHERE t.owner_id=$1",
                "edit_identities" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM edit_identities t JOIN assets a ON a.id=t.asset_id WHERE a.owner_id=$1",
                "asset_paths" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM asset_paths t WHERE t.owner_id=$1",
                "lifecycle_holds" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM lifecycle_holds t JOIN assets a ON a.id=t.asset_id WHERE a.owner_id=$1",
                "import_batches" or "idempotency_results" or "upload_sessions" or "asset_mutations" => $"SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM {table} t WHERE t.owner_id=$1",
                "import_batch_items" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM import_batch_items t JOIN import_batches b ON b.id=t.batch_id WHERE b.owner_id=$1",
                "ingest_publications" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM ingest_publications t JOIN import_batches b ON b.id=t.batch_id WHERE b.owner_id=$1",
                "publication_intents" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM publication_intents t WHERE EXISTS (SELECT 1 FROM publication_intent_items i JOIN assets a ON a.id=i.asset_id WHERE i.intent_id=t.id AND a.owner_id=$1)",
                "publication_intent_items" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM publication_intent_items t JOIN assets a ON a.id=t.asset_id WHERE a.owner_id=$1",
                "reconstruction_manifests" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM reconstruction_manifests t WHERE t.owner_id=$1 OR t.owner_id IS NULL",
                "rendering_compatibility" or "rendering_compatibility_candidates" or "render_intake_control" or "export_operations" or "recovery_audit_state" or "recovery_observation_rejections" or "platform_schema_migrations" =>
                    $"SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM {table} t WHERE $1::text IS NOT NULL",
                "render_attempts" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM render_attempts t JOIN render_requests r ON r.id=t.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "renditions" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM renditions t JOIN render_requests r ON r.id=t.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "render_provenance" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM render_provenance t JOIN renditions d ON d.id=t.rendition_id JOIN render_requests r ON r.id=d.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "windows_sync_content_ids" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM windows_sync_content_ids t WHERE EXISTS (SELECT 1 FROM windows_sync_device_content d WHERE d.content_id=t.content_id AND d.owner_id=$1 AND d.library_id=$2)",
                "windows_sync_owner_revisions" or "gallery_preferences" or "export_download_sessions" or "recovery_identity_mappings" =>
                    $"SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM {table} t WHERE t.owner_id=$1",
                "export_basket_items" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM export_basket_items t JOIN export_baskets b ON b.id=t.basket_id WHERE b.owner_id=$1 AND b.library_id=$2",
                "export_results" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM export_results t JOIN export_requests r ON r.id=t.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "export_write_intents" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM export_write_intents t JOIN export_requests r ON r.id=t.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "export_download_grants" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM export_download_grants t JOIN export_requests r ON r.id=t.request_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "recovery_point_attestations" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_point_attestations t JOIN recovery_points p ON p.point_id=t.point_id WHERE p.owner_id=$1 AND p.library_id=$2",
                "recovery_point_components" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_point_components t JOIN recovery_points p ON p.point_id=t.point_id WHERE p.owner_id=$1 AND p.library_id=$2",
                "recovery_validation_revisions" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_validation_revisions t JOIN recovery_sessions s ON s.session_id=t.session_id WHERE s.owner_id=$1 AND s.library_id=$2",
                "recovery_materialization_receipts" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_materialization_receipts t JOIN recovery_sessions s ON s.session_id=t.session_id WHERE s.owner_id=$1 AND s.library_id=$2",
                "recovery_purge_objects" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_purge_objects t JOIN recovery_purge_journals j ON j.operation_id=t.operation_id WHERE j.owner_id=$1 AND j.library_id=$2",
                "vc_commit_parents" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM vc_commit_parents t JOIN vc_commits c ON c.id=t.commit_id WHERE c.owner_id=$1 AND c.library_id=$2",
                "vc_commit_entries" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM vc_commit_entries t JOIN vc_commits c ON c.id=t.commit_id WHERE c.owner_id=$1 AND c.library_id=$2",
                "vc_staged_entries" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM vc_staged_entries t JOIN vc_staging_sets s ON s.id=t.staging_set_id WHERE s.owner_id=$1 AND s.library_id=$2",
                "vc_recovery_expirations" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM vc_recovery_expirations t JOIN vc_reflog r ON r.id=t.reflog_id WHERE r.owner_id=$1 AND r.library_id=$2",
                "recovery_audit_checkpoints" => "SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM recovery_audit_checkpoints t JOIN recovery_audit_events e ON e.sequence=t.sequence AND e.event_sha256=t.event_sha256 WHERE e.owner_id=$1 AND e.library_id=$2",
                _ => $"SELECT count(*),coalesce(sum(pg_column_size(t)),0) FROM {table} t WHERE t.owner_id=$1 AND t.library_id=$2"
            };
            var value = await CountTableAsync(database, transaction, query, ownerId, libraryId, cancellationToken);
            itemCount = checked(itemCount + value.ItemCount);
            byteLength = checked(byteLength + value.ByteLength);
        }
        return new(itemCount, byteLength);
    }

    private static async Task<CountBytes> CountTableAsync(
        NpgsqlConnection database,
        NpgsqlTransaction transaction,
        string sql,
        string ownerId,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, database, transaction);
        command.Parameters.AddWithValue(ownerId);
        if (sql.Contains("$2", StringComparison.Ordinal)) command.Parameters.AddWithValue(libraryId ?? "");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RecoveryWorkerException("snapshot_inventory_query_failed");
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static IReadOnlyList<RecoveryClassCoverage> BuildManifestCoverage(
        CapturedScope data,
        PublishedS3Object dump,
        PublishedS3Object inventory,
        PublishedS3Object configuration,
        PublishedS3Object release)
    {
        var values = data.Counts.ToDictionary(pair => pair.Key, pair => pair.Value);
        values[RecoveryDataClass.Configuration] = new(1, configuration.ByteLength);
        values[RecoveryDataClass.ReleaseArtifact] = new(1, release.ByteLength);
        values[RecoveryDataClass.DatabaseSnapshot] = new(1, dump.ByteLength);
        values[RecoveryDataClass.ObjectInventory] = new(1, inventory.ByteLength);
        values[RecoveryDataClass.AccessEvidence] = new(0, 0);
        return values.Select(pair => new RecoveryClassCoverage(pair.Key, pair.Value.ItemCount, pair.Value.ByteLength)).ToArray();
    }

    private static RecoveryComponentReference Component(string name, RecoveryDataClass dataClass, PublishedS3Object item, string bucket) =>
        new(name, dataClass, $"s3://{bucket}/{item.Key}", item.VersionId, item.Sha256, item.ByteLength);

    internal static async Task CreateDatabaseDumpAsync(
        RecoveryWorkerConfiguration configuration,
        DatabaseConnection connection,
        string snapshotId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var pgDump = configuration.Optional("PgDumpPath") ?? "pg_dump";
        await RequirePostgres18Async(pgDump, cancellationToken);
        await RunProcessAsync(pgDump,
            ["--format=custom", "--no-owner", "--no-acl", $"--snapshot={snapshotId}", $"--file={outputPath}",
                $"--host={connection.Host}", $"--port={connection.Port}", $"--username={connection.Username}",
                $"--dbname={connection.Database}"], connection.Password, connection, null, cancellationToken);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new RecoveryWorkerException("database_dump_empty");
    }

    private static async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string password,
        DatabaseConnection connection,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = outputPath is null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = password;
        start.Environment["PGSSLMODE"] = "verify-full";
        start.Environment["PGSSLROOTCERT"] = connection.RootCertificatePath;
        start.Environment["PGHOST"] = connection.Host;
        start.Environment["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGDATABASE"] = connection.Database;
        start.Environment["PGUSER"] = connection.Username;
        using var process = Process.Start(start) ?? throw new RecoveryWorkerException("postgres_tool_start_failed");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string>? stdoutTask = outputPath is null ? process.StandardOutput.ReadToEndAsync(cancellationToken) : null;
        await process.WaitForExitAsync(cancellationToken);
        _ = await stderrTask;
        if (stdoutTask is not null) _ = await stdoutTask;
        if (process.ExitCode != 0) throw new RecoveryWorkerException("postgres_tool_failed");
    }

    internal static async Task RequirePostgres18Async(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new RecoveryWorkerException("postgres_tool_start_failed");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(
                stdout + " " + stderr, @"\bPostgreSQL\)?\s+18(?:\.|\b)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new RecoveryWorkerException("postgres_tool_major_version_mismatch");
    }

    private static async Task<PublishedS3Object> WriteMarkerAsync<T>(
        IAmazonS3 s3,
        string bucket,
        string key,
        T value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ArtifactJson);
        return await S3ArtifactWriter.PutBytesAsync(s3, bucket, key, bytes, null, cancellationToken);
    }

    private static CaptureBuildBinding ReadBuildBinding(
        RecoveryWorkerConfiguration configuration,
        string migrationDigest,
        string sourceAccountId,
        string sourceRegion) => new(
            configuration.EnvironmentName,
            configuration.Optional("SourceRevision") ?? throw new RecoveryWorkerException("source_revision_missing"),
            sourceAccountId,
            sourceRegion,
            configuration.RequiredHash("ApiBuildDigest"),
            configuration.RequiredHash("IngestWorkerBuildDigest"),
            configuration.WorkerBuildDigest,
            configuration.RequiredHash("RendererBuildDigest"),
            migrationDigest,
            configuration.RequiredHash("ConfigurationDigest"));

    private static string ScopeHash(string ownerId, string libraryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId + "\0" + libraryId))).ToLowerInvariant();

    private static string RequireAccountId(string? value)
    {
        if (value is null || value.Length != 12 || value.Any(character => !char.IsAsciiDigit(character)))
            throw new RecoveryWorkerException("source_account_identity_invalid");
        return value;
    }

    private static string NormalizePrefix(string prefix) => prefix.Trim('/') + "/";

    private static bool IsLsn(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length is > 0 and <= 8 && part.All(Uri.IsHexDigit));
    }

    internal sealed record RecoveryScope(string OwnerId, string LibraryId);
    internal sealed record CapturedScope(RecoveryScope Scope, RecoveryChangeWatermark Watermark,
        IReadOnlyList<RecoveryObjectReference> Objects, Dictionary<RecoveryDataClass, CountBytes> Counts);
    internal sealed record CountBytes(long ItemCount, long ByteLength);
    private sealed record ObjectRow(RecoveryDataClass DataClass, string OwnerId, string LibraryId, Guid AssetId,
        Guid? EditId, string? CommitId, string Sha256, long ByteLength, string SourceKey, string SourceVersionId);
    internal sealed record DatabaseConnection(string ConnectionString, string Password, string Host, int Port, string Database, string Username, string RootCertificatePath);
    private sealed record CaptureBuildBinding(string Environment, string SourceRevision, string SourceAccountId, string SourceRegion,
        string ApiBuildDigest, string IngestWorkerBuildDigest, string RecoveryWorkerBuildDigest, string RendererBuildDigest,
        string MigrationDigest, string ConfigurationDigest);

    private sealed record CaptureMarkerV1(int SchemaVersion, Guid CaptureId, DateTimeOffset StartedAtUtc,
        string SourceAccountId, string SourceRegion, string RecoveryWorkerBuildDigest);
    private sealed record CaptureManifestPointerV1(string Key, string VersionId, string Sha256, long ByteLength);
    private sealed record CaptureCompleteV1(int SchemaVersion, Guid CaptureId, DateTimeOffset CompletedAtUtc,
        int ScopeCount, IReadOnlyList<CaptureManifestPointerV1> ManifestObjects);
    private sealed record CaptureFailureV1(int SchemaVersion, Guid CaptureId, DateTimeOffset FailedAtUtc,
        string SafeCode, string RecoveryWorkerBuildDigest);
    private sealed record CaptureInventoryV1(int SchemaVersion, Guid CaptureId, string OwnerId, string LibraryId,
        IReadOnlyList<RecoveryObjectReference> Objects);
    private sealed record CaptureConfigurationV1(int SchemaVersion, string Environment, string SourceRevision,
        string SourceAccountId, string SourceRegion, string MigrationDigest, string ConfigurationDigest,
        string DatabaseRootCertificateSha256);
    private sealed record CaptureReleaseBindingV1(int SchemaVersion, string ApiBuildDigest,
        string IngestWorkerBuildDigest, string RecoveryWorkerBuildDigest, string RendererBuildDigest, string SourceRevision);
}
