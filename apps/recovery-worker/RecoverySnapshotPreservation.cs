using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoverySnapshotPreservation
{
    // ADR-0018 classifies this exact v1 migration set as metadata/evidence only.
    // New migrations need explicit classification before preservation can be signed.
    internal const string ClassifiedMigrationSha256 = "56bf31d09759b12ce2de2a7a15293603e7761df2cbcf28494dbede3ceb259bca";
    internal const string LegacyClassifiedMigrationSha256 = "11679a386b634417931b0ec127899368fda5ea1bd79962be5122c17cb7a144c4";

    internal static async Task<string> RequireMediaFreeAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string migrationDirectory, string expectedInventorySha256, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long migrationBytes = 0;
        foreach (var path in Directory.GetFiles(migrationDirectory, "*.sql").Order(StringComparer.Ordinal))
        {
            migrationBytes = checked(migrationBytes + new FileInfo(path).Length);
            if (migrationBytes > 16 * 1024 * 1024) throw new RecoveryWorkerException("snapshot_classification_size_limit");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var text = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }
        var classified = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (classified != ClassifiedMigrationSha256 && classified != LegacyClassifiedMigrationSha256)
            throw new RecoveryWorkerException("snapshot_media_classification_required");
        var actual = await ReadInventorySha256Async(connection, transaction, ct);
        if (actual != expectedInventorySha256)
            throw new RecoveryWorkerException("snapshot_schema_inventory_changed");
        await using var largeObjects = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_largeobject_metadata)", connection, transaction);
        if ((bool)(await largeObjects.ExecuteScalarAsync(ct))!)
            throw new RecoveryWorkerException("snapshot_large_objects_not_classified");
        await RequireClassifiedBinaryValuesAsync(connection, transaction, ct);
        return actual;
    }

    private static async Task RequireClassifiedBinaryValuesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        // A known schema is not enough: bytes in its existing evidence columns
        // remain unclassified until a typed, authenticated value validator exists.
        var columns = new List<(string Table, string Column)>();
        await using (var query = new NpgsqlCommand("""
            SELECT table_name,column_name FROM information_schema.columns
             WHERE table_schema='public' AND (udt_name='bytea' OR udt_name='_bytea')
             ORDER BY table_name,ordinal_position
            """, connection, transaction))
        await using (var rows = await query.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct)) columns.Add((rows.GetString(0), rows.GetString(1)));
        using var identifiers = new NpgsqlCommandBuilder();
        foreach (var (table, column) in columns)
        {
            // Global audit bytes/signatures have already been authenticated by
            // the native validator. Commit manifests are checked for every scope below.
            if (table is "recovery_audit_events" or "recovery_audit_checkpoints" ||
                table == "vc_commits" && column == "manifest_bytes") continue;
            await using var present = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM public.{identifiers.QuoteIdentifier(table)} " +
                $"WHERE {identifiers.QuoteIdentifier(column)} IS NOT NULL)", connection, transaction);
            if ((bool)(await present.ExecuteScalarAsync(ct))!)
                throw new RecoveryWorkerException("snapshot_binary_evidence_not_classified");
        }
        var scopes = new List<(string Owner, string Library)>();
        await using (var query = new NpgsqlCommand("SELECT DISTINCT owner_id,library_id FROM vc_commits", connection, transaction))
        await using (var rows = await query.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
            {
                if (scopes.Count >= 10_000) throw new RecoveryWorkerException("snapshot_commit_scope_limit");
                scopes.Add((rows.GetString(0), rows.GetString(1)));
            }
        foreach (var (owner, library) in scopes)
            await RecoveryDatabaseValidator.ValidateCanonicalCommitsAsync(connection, transaction, owner, library, ct);
    }

    internal static async Task<string> ReadInventorySha256Async(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT table_schema,table_name,column_name,ordinal_position,data_type,udt_schema,udt_name,is_nullable
              FROM information_schema.columns
             WHERE table_schema NOT IN ('pg_catalog','information_schema')
             ORDER BY table_schema,table_name,ordinal_position
            """, connection, transaction);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var inventory = new List<object[]>();
        while (await rows.ReadAsync(ct))
        {
            if (inventory.Count >= 10_000) throw new RecoveryWorkerException("snapshot_schema_inventory_limit");
            var row = new object[rows.FieldCount];
            rows.GetValues(row);
            inventory.Add(row);
        }
        return RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(inventory));
    }

    internal static async Task<RecoverySnapshotPreservationEnvelopeV1> SignAsync(RecoveryWorkerConfiguration configuration,
        IRecoveryEvidenceSigner signer, RecoverySigningTrust trust, VerifiedCopiedPoint point,
        string inventorySha256, RecoveryAuditHeadV1 auditHead, DateTimeOffset verifiedAtUtc, CancellationToken ct)
    {
        var receipt = point.Receipts.Single(item => item.DataClass == nameof(RecoveryDataClass.DatabaseSnapshot));
        var validationSha256 = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
        {
            schemaVersion = 1,
            manifestSha256 = point.Pointer.SourceManifestSha256,
            schemaInventorySha256 = inventorySha256,
            auditHead,
            coverage = point.SourceManifest.Coverage,
            objectClosureSha256 = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(point.SourceManifest.Objects))
        }));
        var proof = new RecoverySnapshotPreservationProofV1(1, point.Pointer.PointId, point.Pointer.CaptureId,
            point.Pointer.OwnerId, point.Pointer.LibraryId, point.CheckpointEnvelopeSha256, point.Pointer.SourceManifestSha256,
            receipt.DestinationBucket, receipt.DestinationKey, receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength,
            point.SourceManifest.MigrationDigest, inventorySha256, validationSha256, verifiedAtUtc,
            configuration.WorkerId, configuration.WorkerBuildDigest);
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(proof);
        var signature = await signer.SignDigestAsync(SHA256.HashData(bytes), ct);
        var envelope = new RecoverySnapshotPreservationEnvelopeV1(1, "recovery_snapshot_media_free", signer.KeyId,
            signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes), Convert.ToBase64String(signature));
        _ = RecoverySnapshotPreservationCodec.Verify(envelope, trust, configuration.WorkerBuildDigest, DateTimeOffset.UtcNow);
        return envelope;
    }
}
