using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCandidateStore
{
    internal static async Task RecordValidationAsync(NpgsqlConnection connection, Guid candidateId,
        string sourceManifestSha256, string targetDigest, string validationDigest,
        CancellationToken cancellationToken)
    {
        foreach (var digest in new[] { sourceManifestSha256, targetDigest, validationDigest })
            if (digest is null || digest.Length != 64 || digest.Any(character =>
                    !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
                throw new RecoveryWorkerException("candidate_validation_digest_invalid");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var locked = new NpgsqlCommand("""
            SELECT state FROM recovery_candidate_sessions WHERE candidate_id=$1
              AND source_manifest_sha256=$2 AND expires_at_utc>now() FOR UPDATE
            """, connection, transaction);
        locked.Parameters.AddWithValue(candidateId);
        locked.Parameters.AddWithValue(sourceManifestSha256);
        var state = await locked.ExecuteScalarAsync(cancellationToken) as string;
        if (state is not ("validating" or "owner_access_pending"))
            throw new RecoveryWorkerException("candidate_not_validating");
        await using var insert = new NpgsqlCommand("""
            INSERT INTO recovery_candidate_validation_revisions(candidate_id,revision,source_manifest_sha256,
                target_digest,validation_digest,state,findings,validated_at_utc)
            SELECT $1,1,$2,$3,$4,'verified','[]'::jsonb,now()
            ON CONFLICT(candidate_id,revision) DO NOTHING
            """, connection, transaction);
        foreach (var value in new object[] { candidateId, sourceManifestSha256, targetDigest, validationDigest })
            insert.Parameters.AddWithValue(value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await using var check = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM recovery_candidate_validation_revisions WHERE candidate_id=$1
                AND revision=1 AND source_manifest_sha256=$2 AND target_digest=$3 AND validation_digest=$4
                AND state='verified' AND findings='[]'::jsonb)
            """, connection, transaction);
        foreach (var value in new object[] { candidateId, sourceManifestSha256, targetDigest, validationDigest })
            check.Parameters.AddWithValue(value);
        if (await check.ExecuteScalarAsync(cancellationToken) is not true)
            throw new RecoveryWorkerException("candidate_validation_replay_mismatch");
        await using var update = new NpgsqlCommand("""
            UPDATE recovery_candidate_sessions SET state='owner_access_pending' WHERE candidate_id=$1 AND state='validating'
            """, connection, transaction);
        update.Parameters.AddWithValue(candidateId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task CreateAsync(NpgsqlConnection connection, Guid candidateId, string databaseName,
        RecoveryWorkerConfiguration configuration, VerifiedCopiedPoint point,
        VerifiedRecoveryAuditHeadProof auditProof, CancellationToken cancellationToken)
    {
        auditProof.Payload.Validate();
        var manifest = point.SourceManifest;
        if (candidateId == Guid.Empty || !databaseName.StartsWith("recovery_drill_", StringComparison.Ordinal) ||
            databaseName.Length > 63 || databaseName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            configuration.Required("RecoveryOnly") != "true" ||
            connection.Database != databaseName || manifest.Digest() != auditProof.Payload.SourceManifestSha256 ||
            (point.FinalManifest?.Digest() ?? manifest.Digest()) != auditProof.Payload.FullManifestSha256 ||
            auditProof.Payload.RecoveryPointId != manifest.RecoveryPointId || auditProof.Payload.CaptureId != manifest.CaptureId ||
            auditProof.Payload.OwnerId != manifest.OwnerId || auditProof.Payload.LibraryId != manifest.LibraryId ||
            auditProof.Payload.SnapshotCutoffUtc != manifest.SnapshotCutoffUtc || auditProof.Payload.SnapshotLsn != manifest.SnapshotLsn ||
            auditProof.Payload.RecoveryWorkerBuildDigest != configuration.WorkerBuildDigest)
            throw new RecoveryWorkerException("candidate_binding_invalid");
        var targetBucket = configuration.Required("TargetBucket");
        if (targetBucket == configuration.Required("SourceBucket") || targetBucket == configuration.Required("DestinationBucket") ||
            targetBucket == configuration.Required("EvidenceBucket"))
            throw new RecoveryWorkerException("candidate_target_not_isolated");
        var now = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, object?>
        {
            ["candidate_id"] = candidateId, ["owner_id"] = manifest.OwnerId, ["library_id"] = manifest.LibraryId,
            ["capture_id"] = manifest.CaptureId, ["recovery_point_id"] = manifest.RecoveryPointId,
            ["source_account_id"] = configuration.RequiredAccountId("SourceAccountId"),
            ["source_region"] = configuration.RequiredRegion("SourceRegion"),
            ["recovery_account_id"] = configuration.RequiredAccountId("DestinationAccountId"),
            ["recovery_region"] = configuration.RequiredRegion("DestinationRegion"),
            ["source_manifest_sha256"] = manifest.Digest(), ["full_manifest_sha256"] = point.FinalManifest?.Digest(),
            ["copy_checkpoint_payload_sha256"] = point.CheckpointPayloadSha256,
            ["copy_checkpoint_envelope_sha256"] = point.Pointer.CheckpointSha256,
            ["snapshot_cutoff_utc"] = manifest.SnapshotCutoffUtc, ["snapshot_lsn"] = manifest.SnapshotLsn,
            ["change_sequence"] = manifest.ChangeWatermark.LastSequence,
            ["latest_changed_at_utc"] = manifest.ChangeWatermark.LatestChangedAtUtc,
            ["audit_genesis_sha256"] = auditProof.Payload.GenesisArtifactSha256,
            ["audit_head_sequence"] = auditProof.Payload.VerifiedHead.Sequence,
            ["audit_head_event_sha256"] = auditProof.Payload.VerifiedHead.EventSha256,
            ["audit_proof_payload_sha256"] = auditProof.PayloadSha256,
            ["release_binding_sha256"] = manifest.Components.Single(item => item.Name == "release-bindings.json").Sha256,
            ["configuration_digest"] = configuration.RequiredHash("ConfigurationDigest"),
            ["recovery_worker_build_digest"] = configuration.WorkerBuildDigest,
            ["target_database_name"] = databaseName, ["target_endpoint_identity"] = connection.Host,
            ["target_root_certificate_sha256"] = configuration.RequiredHash("DatabaseRootCertificateSha256"),
            ["target_storage_bucket"] = targetBucket, ["target_application_digest"] = configuration.RequiredHash("ApiBuildDigest")
        };
        var names = values.Keys.ToArray();
        var placeholders = names.Select((name, index) => "$" + (index + 1) + (name == "snapshot_lsn" ? "::pg_lsn" : "")).ToArray();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO recovery_candidate_sessions({string.Join(',', names)},state,created_at_utc,expires_at_utc)
            VALUES({string.Join(',', placeholders)},'restoring',${names.Length + 1},${names.Length + 2})
            ON CONFLICT(candidate_id) DO NOTHING
            """, connection, transaction))
        {
            foreach (var value in values.Values) insert.Parameters.AddWithValue(value ?? DBNull.Value);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(now.AddHours(24));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                var comparisons = names.Select((name, index) =>
                    name + " IS NOT DISTINCT FROM " + placeholders[index]);
                await using var replay = new NpgsqlCommand($"""
                    SELECT ({string.Join(" AND ", comparisons)}) AND expires_at_utc>now()
                      AND state NOT IN ('failed','expired')
                    FROM recovery_candidate_sessions WHERE candidate_id=$1 FOR UPDATE
                    """, connection, transaction);
                foreach (var value in values.Values) replay.Parameters.AddWithValue(value ?? DBNull.Value);
                if (await replay.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new RecoveryWorkerException("candidate_replay_binding_mismatch");
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task RebindAsync(NpgsqlConnection connection, Guid candidateId,
        IReadOnlyList<RecoveryMaterializedObjectV1> receipts, CancellationToken cancellationToken)
    {
        if (candidateId == Guid.Empty || receipts is null || receipts.Count == 0)
            throw new RecoveryWorkerException("candidate_materialization_missing");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string currentState;
        await using (var locked = new NpgsqlCommand("""
            SELECT state FROM recovery_candidate_sessions
            WHERE candidate_id=$1 AND expires_at_utc>now() FOR UPDATE
            """, connection, transaction))
        {
            locked.Parameters.AddWithValue(candidateId);
            currentState = await locked.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new RecoveryWorkerException("candidate_missing_or_expired");
        }
        var ordered = receipts.OrderBy(item => item.SourceBucket, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal).ThenBy(item => item.SourceVersionId, StringComparer.Ordinal).ToArray();
        foreach (var receipt in ordered) receipt.Validate();
        if (ordered.Select(item => (item.SourceBucket, item.SourceKey, item.SourceVersionId)).Distinct().Count() != ordered.Length)
            throw new RecoveryWorkerException("candidate_duplicate_source");
        if (currentState is "validating" or "owner_access_pending" or "owner_access_verified")
        {
            await using var replay = new NpgsqlCommand("""
                SELECT ordinal,receipt_sha256 FROM recovery_candidate_materialization_receipts
                WHERE candidate_id=$1 ORDER BY ordinal
                """, connection, transaction);
            replay.Parameters.AddWithValue(candidateId);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            var ordinal = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (ordinal >= ordered.Length || reader.GetInt32(0) != ordinal || reader.GetString(1).Trim() !=
                    RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(ordered[ordinal])))
                    throw new RecoveryWorkerException("candidate_receipt_replay_mismatch");
                ordinal++;
            }
            if (ordinal != ordered.Length) throw new RecoveryWorkerException("candidate_receipt_replay_mismatch");
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await using (var state = new NpgsqlCommand("""
            UPDATE recovery_candidate_sessions SET state='materializing'
            WHERE candidate_id=$1 AND state='restoring' AND expires_at_utc>now()
            """, connection, transaction))
        {
            state.Parameters.AddWithValue(candidateId);
            if (await state.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new RecoveryWorkerException("candidate_not_restoring");
        }
        for (var ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            var receipt = ordered[ordinal];
            receipt.Validate();
            await using var update = new NpgsqlCommand("""
                UPDATE content_objects SET storage_key=$5,storage_version=$6
                WHERE sha256=$1 AND byte_length=$2 AND storage_key=$3 AND storage_version=$4
                  AND integrity_state='authoritative'
                """, connection, transaction);
            update.Parameters.AddWithValue(receipt.Sha256);
            update.Parameters.AddWithValue(receipt.ByteLength);
            update.Parameters.AddWithValue(receipt.SourceKey);
            update.Parameters.AddWithValue(receipt.SourceVersionId);
            update.Parameters.AddWithValue(receipt.DestinationKey);
            update.Parameters.AddWithValue(receipt.DestinationVersionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new RecoveryWorkerException("candidate_source_locator_mismatch");
            await using var insert = new NpgsqlCommand("""
                INSERT INTO recovery_candidate_materialization_receipts(
                    candidate_id,ordinal,source_bucket,source_key,source_version_id,sha256,byte_length,
                    destination_bucket,destination_key,destination_version_id,receipt_sha256,created_at_utc)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,now())
                """, connection, transaction);
            foreach (var value in new object[] { candidateId, ordinal, receipt.SourceBucket, receipt.SourceKey,
                         receipt.SourceVersionId, receipt.Sha256, receipt.ByteLength, receipt.DestinationBucket,
                         receipt.DestinationKey, receipt.DestinationVersionId,
                         RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(receipt)) })
                insert.Parameters.AddWithValue(value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var validating = new NpgsqlCommand("UPDATE recovery_candidate_sessions SET state='validating' WHERE candidate_id=$1",
            connection, transaction);
        validating.Parameters.AddWithValue(candidateId);
        await validating.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
