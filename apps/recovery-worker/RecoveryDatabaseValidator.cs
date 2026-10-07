using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Contracts.VersionControl;

namespace Platform.Recovery.Worker;

internal static class RecoveryDatabaseValidator
{
    internal static async Task ValidateSchemaAndGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ownerId,
        string libraryId,
        string migrationDirectory,
        string expectedMigrationDigest,
        string recoveryAccountId,
        RecoverySigningTrust auditTrust,
        VerifiedRecoveryAuditGenesis auditGenesis,
        CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(connection, transaction, cancellationToken);
        await ValidateMigrationLedgerAsync(connection, transaction, migrationDirectory, expectedMigrationDigest,
            cancellationToken);
        await ValidateAuditChainAsync(connection, transaction, recoveryAccountId, auditTrust, auditGenesis,
            cancellationToken);
        await ValidateCanonicalCommitsAsync(connection, transaction, ownerId, libraryId, cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1
            WHERE EXISTS (
                SELECT 1 FROM vc_commit_parents p
                JOIN vc_commits child ON child.id=p.commit_id
                LEFT JOIN vc_commits parent ON parent.id=p.parent_id
                    AND parent.owner_id=child.owner_id AND parent.library_id=child.library_id
                WHERE child.owner_id=$1 AND child.library_id=$2 AND parent.id IS NULL
            ) OR EXISTS (
                SELECT 1 FROM vc_commit_entries e
                JOIN vc_commits c ON c.id=e.commit_id
                LEFT JOIN assets a ON a.id=e.asset_id AND a.owner_id=c.owner_id
                LEFT JOIN edit_identities i ON i.id=e.edit_id AND i.asset_id=e.asset_id
                WHERE c.owner_id=$1 AND c.library_id=$2
                  AND (a.id IS NULL OR (e.edit_id IS NOT NULL AND i.id IS NULL))
            ) OR EXISTS (
                SELECT 1 FROM vc_refs r
                LEFT JOIN vc_commits c ON c.id=r.head_commit_id
                    AND c.owner_id=r.owner_id AND c.library_id=r.library_id
                WHERE r.owner_id=$1 AND r.library_id=$2 AND r.head_commit_id IS NOT NULL AND c.id IS NULL
            ) OR EXISTS (
                SELECT 1 FROM vc_reflog r
                CROSS JOIN LATERAL (VALUES (r.old_head), (r.new_head)) h(commit_id)
                LEFT JOIN vc_commits c ON c.id=h.commit_id
                    AND c.owner_id=r.owner_id AND c.library_id=r.library_id
                WHERE r.owner_id=$1 AND r.library_id=$2 AND h.commit_id IS NOT NULL AND c.id IS NULL
            ) OR EXISTS (
                SELECT 1 FROM vc_working_changes w
                LEFT JOIN vc_commits c ON c.id=w.base_commit_id
                    AND c.owner_id=w.owner_id AND c.library_id=w.library_id
                LEFT JOIN assets a ON a.id=w.asset_id AND a.owner_id=w.owner_id
                LEFT JOIN edit_identities i ON i.id=w.edit_id AND i.asset_id=w.asset_id
                WHERE w.owner_id=$1 AND w.library_id=$2
                  AND ((w.base_commit_id IS NOT NULL AND c.id IS NULL) OR a.id IS NULL OR
                       (w.edit_id IS NOT NULL AND i.id IS NULL))
            ) OR EXISTS (
                SELECT 1 FROM lineage_merge_content m
                LEFT JOIN assets a ON a.id=m.asset_id AND a.owner_id=m.owner_id
                LEFT JOIN edit_identities i ON i.id=m.edit_id AND i.asset_id=m.asset_id
                LEFT JOIN vc_commits target ON target.id=m.target_commit_id
                    AND target.owner_id=m.owner_id AND target.library_id=m.library_id
                LEFT JOIN vc_commits source ON source.id=m.source_commit_id
                    AND source.owner_id=m.owner_id AND source.library_id=m.library_id
                WHERE m.owner_id=$1 AND m.library_id=$2
                  AND (a.id IS NULL OR i.id IS NULL OR target.id IS NULL OR source.id IS NULL)
            )
            """, ownerId, libraryId, "restored_version_graph_invalid", cancellationToken);

        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM assets a
            LEFT JOIN content_objects o ON o.sha256=a.original_sha256
            WHERE a.owner_id=$1 AND a.original_sha256 IS NOT NULL
              AND (o.sha256 IS NULL OR o.category<>'original' OR o.integrity_state<>'authoritative')
            LIMIT 1
            """, ownerId, libraryId, "restored_media_metadata_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM edit_identities i
            JOIN assets a ON a.id=i.asset_id
            LEFT JOIN content_objects o ON o.sha256=i.xmp_sha256
            WHERE a.owner_id=$1 AND i.xmp_sha256 IS NOT NULL
              AND (o.sha256 IS NULL OR o.category<>'xmp' OR o.integrity_state<>'authoritative')
            LIMIT 1
            """, ownerId, libraryId, "restored_xmp_metadata_invalid", cancellationToken);
        await RequireNoRowsAsync(connection, transaction, """
            SELECT 1 FROM lineage_merge_content m
            CROSS JOIN LATERAL (VALUES (m.sha256),(m.base_sha256),(m.target_sha256),(m.source_sha256)) d(sha256)
            LEFT JOIN content_objects o ON o.sha256=d.sha256
            WHERE m.owner_id=$1 AND m.library_id=$2 AND d.sha256 IS NOT NULL
              AND (o.sha256 IS NULL OR o.category<>'xmp' OR o.integrity_state<>'authoritative')
            LIMIT 1
            """, ownerId, libraryId, "restored_version_graph_invalid", cancellationToken);
    }

    private static async Task ValidateSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = CaptureRunner.ProtectedRelationalTableClasses.SelectMany(tables => tables)
            .Append("content_objects").ToHashSet(StringComparer.Ordinal);
        if (expected.Count != CaptureRunner.ProtectedRelationalTableClasses.Sum(tables => tables.Count) + 1)
            throw new RecoveryWorkerException("recovery_schema_classification_invalid");

        await using var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public'", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken)) actual.Add(reader.GetString(0));
        if (!actual.SetEquals(expected)) throw new RecoveryWorkerException("restored_database_schema_incomplete");
    }

    private static async Task ValidateMigrationLedgerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string migrationDirectory, string expectedDigest, CancellationToken cancellationToken)
    {
        if (await RecoveryWorkerConfiguration.ComputeMigrationDigestAsync(migrationDirectory, cancellationToken) != expectedDigest)
            throw new RecoveryWorkerException("restored_migration_artifacts_mismatch");

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            expected.Add(Path.GetFileName(path), RecoveryEvidenceCodec.Hash(bytes));
        }
        if (expected.Count == 0) throw new RecoveryWorkerException("restored_migration_artifacts_missing");

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT id,sha256 FROM platform_schema_migrations ORDER BY id", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var digest = reader.GetString(1).Trim();
            if (!actual.TryAdd(id, digest)) throw new RecoveryWorkerException("restored_migration_ledger_invalid");
        }
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var digest) || digest != pair.Value))
            throw new RecoveryWorkerException("restored_migration_ledger_mismatch");
    }

    private static async Task ValidateAuditChainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string recoveryAccountId, RecoverySigningTrust trust, VerifiedRecoveryAuditGenesis genesis,
        CancellationToken cancellationToken)
    {
        if (genesis.Payload.Sequence != 0 || genesis.Payload.HeadEventSha256 != new string('0', 64) ||
            genesis.SigningKeyId != trust.KeyId || genesis.SigningPublicKeySha256 != trust.PublicKeySha256)
            throw new RecoveryWorkerException("recovery_audit_genesis_invalid");

        long stateSequence;
        string stateHash;
        await using (var stateCommand = new NpgsqlCommand(
                         "SELECT last_sequence,last_event_sha256 FROM recovery_audit_state WHERE singleton=true", connection, transaction))
        await using (var stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await stateReader.ReadAsync(cancellationToken))
                throw new RecoveryWorkerException("recovery_audit_state_invalid");
            stateSequence = stateReader.GetInt64(0);
            stateHash = stateReader.GetString(1).Trim();
            if (await stateReader.ReadAsync(cancellationToken))
                throw new RecoveryWorkerException("recovery_audit_state_invalid");
        }

        long expectedSequence = 1;
        var previousHash = new string('0', 64);
        await using (var eventsCommand = new NpgsqlCommand("""
            SELECT sequence,event_id,owner_id,library_id,event_type,outcome,safe_resource_id,
                   occurred_at_utc,previous_sha256,canonical_event,event_sha256,signing_key_id,signature
            FROM recovery_audit_events ORDER BY sequence
            """, connection, transaction))
        await using (var eventsReader = await eventsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await eventsReader.ReadAsync(cancellationToken))
            {
                var sequence = eventsReader.GetInt64(0);
                var eventId = eventsReader.GetGuid(1);
                var ownerId = eventsReader.GetString(2);
                var libraryId = eventsReader.GetString(3);
                var eventType = eventsReader.GetString(4);
                var outcome = eventsReader.GetString(5);
                var resourceId = eventsReader.IsDBNull(6) ? null : eventsReader.GetString(6);
                var occurredAt = eventsReader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime();
                var rowPreviousHash = eventsReader.GetString(8).Trim();
                var canonical = (byte[])eventsReader[9];
                var rowEventHash = eventsReader.GetString(10).Trim();
                var signingKeyId = eventsReader.GetString(11);
                var signature = Convert.ToBase64String((byte[])eventsReader[12]);
                if (sequence != expectedSequence || rowPreviousHash != previousHash || signingKeyId != trust.KeyId)
                    throw new RecoveryWorkerException("recovery_audit_chain_discontinuous");

                RecoveryAuditEventPayloadV1 payload;
                try { payload = RecoveryAuditLedgerCodec.ParseEvent(canonical); }
                catch (InvalidDataException exception) { throw new RecoveryWorkerException("recovery_audit_event_invalid", exception); }
                if (payload.Sequence != sequence || payload.EventId != eventId || payload.OwnerId != ownerId ||
                    payload.LibraryId != libraryId || payload.EventType != eventType || payload.Outcome != outcome ||
                    payload.SafeResourceId != resourceId || !SamePostgresTimestamp(payload.OccurredAtUtc, occurredAt) ||
                    payload.PreviousSha256 != rowPreviousHash || RecoveryEvidenceCodec.Hash(canonical) != rowEventHash)
                    throw new RecoveryWorkerException("recovery_audit_event_binding_mismatch");
                try
                {
                    _ = RecoveryEvidenceCodec.VerifyDetached(canonical, rowEventHash, signature,
                        signingKeyId, trust.PublicKeySha256, trust);
                }
                catch (InvalidDataException exception) { throw new RecoveryWorkerException("recovery_audit_signature_invalid", exception); }

                previousHash = rowEventHash;
                expectedSequence = checked(expectedSequence + 1);
            }
        }
        var finalSequence = expectedSequence - 1;
        if (stateSequence != finalSequence || stateHash != previousHash)
            throw new RecoveryWorkerException("recovery_audit_state_head_mismatch");

        await using var checkpoints = new NpgsqlCommand("""
            SELECT c.checkpoint_id,c.sequence,c.event_sha256,c.signer_key_id,c.signature,
                   c.recovery_account,c.created_at_utc,c.offline_pin_id
            FROM recovery_audit_checkpoints c
            JOIN recovery_audit_events e ON e.sequence=c.sequence AND e.event_sha256=c.event_sha256
            ORDER BY c.sequence,c.checkpoint_id
            """, connection, transaction);
        await using (var orphanCommand = new NpgsqlCommand("""
                         SELECT 1 FROM recovery_audit_checkpoints c
                         LEFT JOIN recovery_audit_events e ON e.sequence=c.sequence AND e.event_sha256=c.event_sha256
                         WHERE e.sequence IS NULL LIMIT 1
                         """, connection, transaction))
        {
            if (await orphanCommand.ExecuteScalarAsync(cancellationToken) is not null)
                throw new RecoveryWorkerException("recovery_audit_checkpoint_binding_invalid");
        }
        await using var checkpointReader = await checkpoints.ExecuteReaderAsync(cancellationToken);
        while (await checkpointReader.ReadAsync(cancellationToken))
        {
            var checkpointId = checkpointReader.GetGuid(0);
            var sequence = checkpointReader.GetInt64(1);
            var eventHash = checkpointReader.GetString(2).Trim();
            var signerKeyId = checkpointReader.GetString(3);
            var signature = Convert.ToBase64String((byte[])checkpointReader[4]);
            var accountId = checkpointReader.GetString(5);
            var createdAt = checkpointReader.GetFieldValue<DateTimeOffset>(6).ToUniversalTime();
            var offlinePinId = checkpointReader.GetString(7);
            if (sequence <= 0 || sequence > stateSequence || signerKeyId != trust.KeyId ||
                accountId != recoveryAccountId || offlinePinId != genesis.ArtifactSha256)
                throw new RecoveryWorkerException("recovery_audit_checkpoint_binding_invalid");
            var payload = new RecoveryAuditLedgerCheckpointPayloadV1(1, checkpointId, sequence, eventHash,
                signerKeyId, accountId, createdAt, offlinePinId);
            var bytes = RecoveryAuditLedgerCodec.SerializeLedgerCheckpoint(payload);
            var checkpointDigest = RecoveryEvidenceCodec.Hash(bytes);
            try
            {
                _ = RecoveryEvidenceCodec.VerifyDetached(bytes, checkpointDigest, signature,
                    signerKeyId, trust.PublicKeySha256, trust);
            }
            catch (InvalidDataException exception)
            {
                throw new RecoveryWorkerException("recovery_audit_checkpoint_signature_invalid", exception);
            }
        }
    }

    private static async Task ValidateCanonicalCommitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string ownerId, string libraryId, CancellationToken cancellationToken)
    {
        var commits = new Dictionary<string, StoredCommit>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT c.id,c.library_id,c.manifest_bytes,c.author_id,c.authored_at,c.received_at,c.message,
                   c.contract_version,c.policy_version,c.integrity_state,
                   COALESCE(array_agg(p.parent_id ORDER BY p.ordinal) FILTER (WHERE p.parent_id IS NOT NULL),ARRAY[]::bpchar[])
            FROM vc_commits c LEFT JOIN vc_commit_parents p ON p.commit_id=c.id
            WHERE c.owner_id=$1 AND c.library_id=$2
            GROUP BY c.id,c.library_id,c.manifest_bytes,c.author_id,c.authored_at,c.received_at,c.message,
                     c.contract_version,c.policy_version,c.integrity_state
            ORDER BY c.id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(ownerId);
            command.Parameters.AddWithValue(libraryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0).Trim();
                var raw = (byte[])reader[2];
                var parents = reader.GetFieldValue<string[]>(10).Select(parent => parent.Trim()).ToArray();
                var commit = new StoredCommit(id, reader.GetString(1), raw, reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9), parents, []);
                if (!commits.TryAdd(id, commit)) throw new RecoveryWorkerException("restored_version_graph_invalid");
            }
        }

        var entries = commits.Keys.ToDictionary(id => id, _ => new List<VersionControlEntry>(), StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT e.commit_id,e.asset_id,e.edit_id,e.change_kind,e.base_content_sha256,e.content_sha256,
                   e.display_path,e.lifecycle_state
            FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
            WHERE c.owner_id=$1 AND c.library_id=$2 ORDER BY e.commit_id,e.ordinal
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(ownerId);
            command.Parameters.AddWithValue(libraryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var commitId = reader.GetString(0).Trim();
                if (!entries.TryGetValue(commitId, out var list))
                    throw new RecoveryWorkerException("restored_version_graph_invalid");
                list.Add(new VersionControlEntry(reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetString(3), OptionalTrimmed(reader, 4), OptionalTrimmed(reader, 5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        foreach (var commit in commits.Values)
        {
            if (commit.IntegrityState != "verified" ||
                !string.Equals(VersionControlManifestCodec.Digest(commit.ManifestBytes), commit.Id, StringComparison.Ordinal))
                throw new RecoveryWorkerException("restored_version_graph_invalid");

            VersionControlManifest manifest;
            try
            {
                manifest = VersionControlManifestCodec.Read(commit.ManifestBytes);
                if (!VersionControlManifestCodec.Write(manifest).AsSpan().SequenceEqual(commit.ManifestBytes))
                    throw new InvalidDataException("noncanonical_commit_manifest");
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
            {
                throw new RecoveryWorkerException("restored_version_graph_invalid", exception);
            }

            if (manifest.LibraryId != commit.LibraryId || manifest.AuthorId != commit.AuthorId ||
                manifest.Message != commit.Message ||
                !SamePostgresTimestamp(manifest.AuthoredAt, commit.AuthoredAt) ||
                !SamePostgresTimestamp(manifest.ReceivedAt, commit.ReceivedAt) ||
                manifest.ContractVersion != commit.ContractVersion || manifest.PolicyVersion != commit.PolicyVersion ||
                !manifest.Parents.SequenceEqual(commit.Parents, StringComparer.Ordinal) ||
                !manifest.Entries.SequenceEqual(entries[commit.Id]))
                throw new RecoveryWorkerException("restored_version_graph_invalid");
        }
    }

    private static async Task RequireNoRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, string ownerId, string libraryId, string failureCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(ownerId);
        command.Parameters.AddWithValue(libraryId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            throw new RecoveryWorkerException(failureCode);
    }

    private static string? OptionalTrimmed(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal).Trim();

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime().Ticks / 10 == right.ToUniversalTime().Ticks / 10;

    private sealed record StoredCommit(string Id, string LibraryId, byte[] ManifestBytes, string AuthorId,
        DateTimeOffset AuthoredAt, DateTimeOffset ReceivedAt, string Message, int ContractVersion,
        int PolicyVersion, string IntegrityState, IReadOnlyList<string> Parents, IReadOnlyList<VersionControlEntry> Entries);
}
