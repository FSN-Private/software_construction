using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Platform.Recovery;

public sealed record RecoveryRestoreScopeV1(string Kind, Guid? AssetId, Guid? EditId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    public void Validate()
    {
        if (Reason is not null && (Reason.Length is < 1 or > 1000 || Reason.Any(char.IsControl)))
            throw new InvalidDataException("restore_reason_invalid");
        if (Kind == "full_library" && AssetId is null && EditId is null) return;
        if (Kind == "asset" && AssetId is { } asset && asset != Guid.Empty && EditId is null) return;
        if (Kind == "edit" && AssetId is { } editAsset && editAsset != Guid.Empty && EditId is { } edit && edit != Guid.Empty) return;
        throw new InvalidDataException("restore_scope_invalid");
    }
}

public sealed record RecoveryRestoreProposalV1(
    int SchemaVersion,
    Guid SessionId,
    string OwnerId,
    string LibraryId,
    Guid RecoveryPointId,
    RecoveryRestoreScopeV1 RequestedScope,
    string RequestDigest,
    DateTimeOffset ExpiresAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate(DateTimeOffset? nowUtc = null)
    {
        if (SchemaVersion != CurrentSchemaVersion || SessionId == Guid.Empty || RecoveryPointId == Guid.Empty)
            throw new InvalidDataException("invalid_restore_proposal_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RequestedScope.Validate();
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        RecoveryValidation.Hash(RequestDigest, nameof(RequestDigest));
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        RecoveryValidation.Utc(now, nameof(nowUtc));
        if (ExpiresAtUtc <= now || ExpiresAtUtc > now.AddDays(1))
            throw new InvalidDataException("restore_proposal_expired_or_too_long");
        if (!string.Equals(RequestDigest, ComputeRequestDigest(this), StringComparison.Ordinal))
            throw new InvalidDataException("restore_proposal_digest_mismatch");
    }

    public static RecoveryRestoreProposalV1 Create(
        Guid sessionId,
        string ownerId,
        string libraryId,
        Guid recoveryPointId,
        RecoveryRestoreScopeV1 scope,
        DateTimeOffset expiresAtUtc)
    {
        var proposal = new RecoveryRestoreProposalV1(CurrentSchemaVersion, sessionId, ownerId, libraryId,
            recoveryPointId, scope, new string('0', 64), expiresAtUtc);
        return proposal with { RequestDigest = ComputeRequestDigest(proposal) };
    }

    public static string ComputeRequestDigest(RecoveryRestoreProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var request = new RequestDigestPayload(proposal.SchemaVersion, proposal.SessionId, proposal.OwnerId,
            proposal.LibraryId, proposal.RecoveryPointId, proposal.RequestedScope, proposal.ExpiresAtUtc);
        return RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(request));
    }

    private sealed record RequestDigestPayload(
        int SchemaVersion,
        Guid SessionId,
        string OwnerId,
        string LibraryId,
        Guid RecoveryPointId,
        RecoveryRestoreScopeV1 RequestedScope,
        DateTimeOffset ExpiresAtUtc);
}

/// <summary>Exact byte-verified source and target object versions in an isolated restored namespace.</summary>
public sealed record RecoveryMaterializedObjectV1(
    string SourceBucket,
    string SourceKey,
    string SourceVersionId,
    string Sha256,
    long ByteLength,
    string DestinationBucket,
    string DestinationKey,
    string DestinationVersionId)
{
    public void Validate()
    {
        RecoveryValidation.Text(SourceBucket, nameof(SourceBucket), 255);
        RecoveryValidation.Text(SourceKey, nameof(SourceKey), 1024);
        RecoveryValidation.Text(SourceVersionId, nameof(SourceVersionId), 1024);
        RecoveryValidation.Hash(Sha256, nameof(Sha256));
        if (ByteLength < 0) throw new InvalidDataException("materialized_object_length_invalid");
        RecoveryValidation.Text(DestinationBucket, nameof(DestinationBucket), 255);
        RecoveryValidation.Text(DestinationKey, nameof(DestinationKey), 1024);
        RecoveryValidation.Text(DestinationVersionId, nameof(DestinationVersionId), 1024);
        if (SourceVersionId == "null" || DestinationVersionId == "null" || DestinationBucket == SourceBucket ||
            DestinationKey != "sha256/" + Sha256)
            throw new InvalidDataException("materialized_object_identity_invalid");
    }
}

/// <summary>
/// Persists only the compact signed point into the restored database and owns the
/// recovery-local session/revision and physical-locator rebinding seam. Callers must
/// supply one transaction so point hydration and session creation remain atomic.
/// </summary>
public static class RestoreSessionStore
{
    private static readonly HashSet<string> SessionStates = new(StringComparer.Ordinal)
    {
        "requested", "restoring", "validating", "ready", "promoting", "promoted", "aborted", "failed", "expired"
    };

    public static async Task<VerifiedRecoveryCatalog> ImportVerifiedPointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCatalogEnvelopeV1 envelope,
        RecoverySigningTrust trust,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(connection, transaction);
        var verified = RecoveryCatalogCodec.Verify(envelope, trust);
        var payload = verified.Payload;
        var now = DateTimeOffset.UtcNow;
        if (payload.CapturedAtUtc > now || payload.SnapshotCutoffUtc > now || payload.VerifiedAtUtc > now)
            throw new InvalidDataException("recovery_catalog_future_timestamp");

        var safeJson = System.Text.Encoding.UTF8.GetString(verified.CanonicalPayloadBytes);
        await using (var insertPoint = new NpgsqlCommand("""
            INSERT INTO recovery_points(
                point_id,capture_id,owner_id,library_id,source_region,captured_at_utc,snapshot_id,snapshot_lsn,
                snapshot_cutoff_utc,change_sequence,latest_changed_at_utc,migration_digest,manifest_schema_version,
                manifest_sha256,manifest_bytes,manifest,state,verified_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8::pg_lsn,$9,$10,$11,$12,$13,$14,$15,$16,'verified',$17)
            ON CONFLICT (point_id) DO NOTHING
            """, connection, transaction))
        {
            insertPoint.Parameters.AddWithValue(payload.RecoveryPointId);
            insertPoint.Parameters.AddWithValue(payload.CaptureId);
            insertPoint.Parameters.AddWithValue(payload.OwnerId);
            insertPoint.Parameters.AddWithValue(payload.LibraryId);
            insertPoint.Parameters.AddWithValue(payload.SourceRegion);
            insertPoint.Parameters.AddWithValue(payload.CapturedAtUtc);
            insertPoint.Parameters.AddWithValue(payload.SnapshotId);
            insertPoint.Parameters.AddWithValue(payload.SnapshotLsn);
            insertPoint.Parameters.AddWithValue(payload.SnapshotCutoffUtc);
            insertPoint.Parameters.AddWithValue(payload.ChangeSequence);
            insertPoint.Parameters.AddWithValue(payload.LatestChangedAtUtc is null ? DBNull.Value : payload.LatestChangedAtUtc.Value);
            insertPoint.Parameters.AddWithValue(payload.MigrationDigest);
            insertPoint.Parameters.AddWithValue(payload.SchemaVersion);
            insertPoint.Parameters.AddWithValue(payload.FullManifestSha256);
            insertPoint.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
            insertPoint.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = safeJson });
            insertPoint.Parameters.AddWithValue(payload.VerifiedAtUtc);
            await insertPoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertAttestation = new NpgsqlCommand("""
            INSERT INTO recovery_point_attestations(
                point_id,worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (point_id) DO NOTHING
            """, connection, transaction))
        {
            insertAttestation.Parameters.AddWithValue(payload.RecoveryPointId);
            insertAttestation.Parameters.AddWithValue(verified.WorkerId);
            insertAttestation.Parameters.AddWithValue(verified.SigningKeyId);
            insertAttestation.Parameters.AddWithValue(verified.SigningPublicKeySha256);
            insertAttestation.Parameters.AddWithValue(verified.PayloadSha256);
            insertAttestation.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
            insertAttestation.Parameters.AddWithValue(verified.Signature);
            await insertAttestation.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsurePointMatchesAsync(connection, transaction, verified, cancellationToken);
        return verified;
    }

    public static async Task ImportProposalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryRestoreProposalV1 proposal,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(connection, transaction);
        proposal.Validate();
        var scopeJson = System.Text.Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(proposal.RequestedScope));
        await using (var point = new NpgsqlCommand("""
            SELECT 1 FROM recovery_points
            WHERE point_id=$1 AND owner_id=$2 AND library_id=$3 AND state='verified'
            """, connection, transaction))
        {
            point.Parameters.AddWithValue(proposal.RecoveryPointId);
            point.Parameters.AddWithValue(proposal.OwnerId);
            point.Parameters.AddWithValue(proposal.LibraryId);
            if (await point.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidDataException("restore_point_not_verified_or_foreign");
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_sessions(
                session_id,owner_id,library_id,recovery_point_id,idempotency_key,request_digest,scope,
                isolated_namespace,state,expires_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,'restore/'||replace($1::text,'-',''),'requested',$8)
            ON CONFLICT (session_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(proposal.SessionId);
            insert.Parameters.AddWithValue(proposal.OwnerId);
            insert.Parameters.AddWithValue(proposal.LibraryId);
            insert.Parameters.AddWithValue(proposal.RecoveryPointId);
            insert.Parameters.AddWithValue(proposal.SessionId.ToString("N"));
            insert.Parameters.AddWithValue(proposal.RequestDigest);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = scopeJson });
            insert.Parameters.AddWithValue(proposal.ExpiresAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var match = new NpgsqlCommand("""
            SELECT owner_id,library_id,recovery_point_id,idempotency_key,request_digest,scope,
                   isolated_namespace,state,expires_at_utc,scope=$2::jsonb
            FROM recovery_sessions WHERE session_id=$1 FOR UPDATE
            """, connection, transaction);
        match.Parameters.AddWithValue(proposal.SessionId);
        match.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = scopeJson });
        await using var reader = await match.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != proposal.OwnerId ||
            reader.GetString(1) != proposal.LibraryId || reader.GetGuid(2) != proposal.RecoveryPointId ||
            reader.GetString(3) != proposal.SessionId.ToString("N") || reader.GetString(4).Trim() != proposal.RequestDigest ||
            reader.GetString(6) != "restore/" + proposal.SessionId.ToString("N") || !SessionStates.Contains(reader.GetString(7)) ||
            !SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(8), proposal.ExpiresAtUtc) || !reader.GetBoolean(9))
            throw new InvalidDataException("restore_session_replay_conflict");
    }

    public static async Task RecordStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string expectedState,
        string nextState,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(connection, transaction);
        if (sessionId == Guid.Empty || !SessionStates.Contains(expectedState) || !SessionStates.Contains(nextState))
            throw new InvalidDataException("restore_session_state_invalid");
        RecoveryValidation.Utc(atUtc, nameof(atUtc));
        if (!AllowedTransition(expectedState, nextState)) throw new InvalidDataException("restore_session_transition_invalid");
        await using var command = new NpgsqlCommand("""
            UPDATE recovery_sessions
            SET state=$3,completed_at_utc=CASE WHEN $3 IN ('ready','promoted','aborted','failed','expired') THEN $4 ELSE completed_at_utc END
            WHERE session_id=$1 AND state=$2 AND
              (($3='expired' AND expires_at_utc<=$4) OR ($3<>'expired' AND expires_at_utc>$4))
            """, connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(expectedState);
        command.Parameters.AddWithValue(nextState);
        command.Parameters.AddWithValue(atUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("restore_session_state_conflict");
    }

    public static async Task<long> AppendValidationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string sourceManifestSha256,
        string targetDigest,
        string state,
        IReadOnlyList<string> findings,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(connection, transaction);
        RecoveryValidation.Hash(sourceManifestSha256, nameof(sourceManifestSha256));
        RecoveryValidation.Hash(targetDigest, nameof(targetDigest));
        RecoveryValidation.Utc(atUtc, nameof(atUtc));
        if (sessionId == Guid.Empty || state is not ("verified" or "failed"))
            throw new InvalidDataException("restore_validation_invalid");
        var canonicalFindings = findings?.Order(StringComparer.Ordinal).ToArray() ?? throw new InvalidDataException("restore_validation_findings_missing");
        RecoveryEvidenceCodec.ValidateSafeCodes(canonicalFindings);
        if ((state == "verified") != (canonicalFindings.Length == 0))
            throw new InvalidDataException("restore_validation_findings_mismatch");

        await using (var session = new NpgsqlCommand("""
            SELECT p.manifest_sha256
            FROM recovery_sessions s JOIN recovery_points p ON p.point_id=s.recovery_point_id
            WHERE s.session_id=$1 AND s.state='validating' AND s.expires_at_utc>$2
            FOR UPDATE OF s
            """, connection, transaction))
        {
            session.Parameters.AddWithValue(sessionId);
            session.Parameters.AddWithValue(atUtc);
            var manifest = (string?)await session.ExecuteScalarAsync(cancellationToken);
            if (manifest is null || manifest.Trim() != sourceManifestSha256)
                throw new InvalidOperationException("restore_validation_session_or_manifest_mismatch");
        }

        var findingsJson = JsonSerializer.Serialize(canonicalFindings);
        var timestamp = TruncateToMicrosecond(atUtc);
        var digestInput = RecoveryEvidenceCodec.SerializeCanonical(new ValidationDigestPayload(
            sessionId, sourceManifestSha256, targetDigest, state, canonicalFindings, timestamp));
        var validationDigest = RecoveryEvidenceCodec.Hash(digestInput);
        long revision;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_validation_revisions(
                session_id,revision,source_manifest_sha256,target_digest,validation_digest,state,findings,validated_at_utc)
            SELECT $1,COALESCE(MAX(revision),0)+1,$2,$3,$4,$5,$6,$7
            FROM recovery_validation_revisions WHERE session_id=$1
            ON CONFLICT (session_id,validation_digest) DO NOTHING
            RETURNING revision
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(sessionId);
            insert.Parameters.AddWithValue(sourceManifestSha256);
            insert.Parameters.AddWithValue(targetDigest);
            insert.Parameters.AddWithValue(validationDigest);
            insert.Parameters.AddWithValue(state);
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = findingsJson });
            insert.Parameters.AddWithValue(timestamp);
            var inserted = await insert.ExecuteScalarAsync(cancellationToken);
            if (inserted is not null) revision = (long)inserted;
            else
            {
                await using var existing = new NpgsqlCommand("""
                    SELECT revision FROM recovery_validation_revisions
                    WHERE session_id=$1 AND source_manifest_sha256=$2 AND target_digest=$3
                      AND validation_digest=$4 AND state=$5 AND findings=$6::jsonb AND validated_at_utc=$7
                    """, connection, transaction);
                existing.Parameters.AddWithValue(sessionId);
                existing.Parameters.AddWithValue(sourceManifestSha256);
                existing.Parameters.AddWithValue(targetDigest);
                existing.Parameters.AddWithValue(validationDigest);
                existing.Parameters.AddWithValue(state);
                existing.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = findingsJson });
                existing.Parameters.AddWithValue(timestamp);
                revision = (long)(await existing.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("restore_validation_replay_conflict"));
            }
        }

        await using var update = new NpgsqlCommand("UPDATE recovery_sessions SET validation_digest=$2 WHERE session_id=$1 AND state='validating'", connection, transaction);
        update.Parameters.AddWithValue(sessionId);
        update.Parameters.AddWithValue(validationDigest);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("restore_validation_session_conflict");
        return revision;
    }

    public static async Task<int> RebindMaterializedObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        IReadOnlyList<RecoveryMaterializedObjectV1> materialized,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(connection, transaction);
        if (sessionId == Guid.Empty || materialized is null) throw new InvalidDataException("materialized_object_list_invalid");
        var rows = materialized.OrderBy(item => item.SourceBucket, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.SourceVersionId, StringComparer.Ordinal).ToArray();
        foreach (var item in rows) item.Validate();
        if (rows.Select(item => (item.SourceBucket, item.SourceKey, item.SourceVersionId)).Distinct().Count() != rows.Length)
            throw new InvalidDataException("materialized_object_duplicate_source");

        await using (var session = new NpgsqlCommand("""
            SELECT state FROM recovery_sessions WHERE session_id=$1 AND state IN ('restoring','validating') FOR UPDATE
            """, connection, transaction))
        {
            session.Parameters.AddWithValue(sessionId);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("materialized_object_session_not_active");
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var item = rows[index];
            var receiptDigest = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(
                new MaterializationDigestPayload(sessionId, item)));
            await using (var update = new NpgsqlCommand("""
                UPDATE content_objects
                SET storage_key=$5,storage_version=$6
                WHERE sha256=$1 AND byte_length=$2 AND storage_key=$3 AND storage_version=$4
                  AND integrity_state='authoritative'
                """, connection, transaction))
            {
                update.Parameters.AddWithValue(item.Sha256);
                update.Parameters.AddWithValue(item.ByteLength);
                update.Parameters.AddWithValue(item.SourceKey);
                update.Parameters.AddWithValue(item.SourceVersionId);
                update.Parameters.AddWithValue(item.DestinationKey);
                update.Parameters.AddWithValue(item.DestinationVersionId);
                var updated = await update.ExecuteNonQueryAsync(cancellationToken);
                if (updated == 0)
                {
                    await using var already = new NpgsqlCommand("""
                        SELECT 1 FROM content_objects
                        WHERE sha256=$1 AND byte_length=$2 AND storage_key=$3 AND storage_version=$4 AND integrity_state='authoritative'
                        """, connection, transaction);
                    already.Parameters.AddWithValue(item.Sha256);
                    already.Parameters.AddWithValue(item.ByteLength);
                    already.Parameters.AddWithValue(item.DestinationKey);
                    already.Parameters.AddWithValue(item.DestinationVersionId);
                    if (await already.ExecuteScalarAsync(cancellationToken) is null)
                        throw new InvalidDataException("materialized_source_identity_mismatch");
                }
            }

            await using var receipt = new NpgsqlCommand("""
                INSERT INTO recovery_materialization_receipts(
                    session_id,ordinal,source_bucket,source_key,source_version_id,sha256,byte_length,
                    destination_bucket,destination_key,destination_version_id,receipt_sha256)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                ON CONFLICT (session_id,ordinal) DO NOTHING
                """, connection, transaction);
            receipt.Parameters.AddWithValue(sessionId);
            receipt.Parameters.AddWithValue(index);
            receipt.Parameters.AddWithValue(item.SourceBucket);
            receipt.Parameters.AddWithValue(item.SourceKey);
            receipt.Parameters.AddWithValue(item.SourceVersionId);
            receipt.Parameters.AddWithValue(item.Sha256);
            receipt.Parameters.AddWithValue(item.ByteLength);
            receipt.Parameters.AddWithValue(item.DestinationBucket);
            receipt.Parameters.AddWithValue(item.DestinationKey);
            receipt.Parameters.AddWithValue(item.DestinationVersionId);
            receipt.Parameters.AddWithValue(receiptDigest);
            if (await receipt.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var existing = new NpgsqlCommand("""
                    SELECT receipt_sha256 FROM recovery_materialization_receipts WHERE session_id=$1 AND ordinal=$2
                    """, connection, transaction);
                existing.Parameters.AddWithValue(sessionId);
                existing.Parameters.AddWithValue(index);
                if ((string?)await existing.ExecuteScalarAsync(cancellationToken) != receiptDigest)
                    throw new InvalidDataException("materialized_receipt_replay_conflict");
            }
        }
        return rows.Length;
    }

    private static async Task EnsurePointMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerifiedRecoveryCatalog verified,
        CancellationToken cancellationToken)
    {
        var payload = verified.Payload;
        await using (var point = new NpgsqlCommand("""
            SELECT capture_id,owner_id,library_id,source_region,captured_at_utc,snapshot_id,snapshot_lsn::text,
                   snapshot_cutoff_utc,change_sequence,latest_changed_at_utc,migration_digest,
                   manifest_schema_version,manifest_sha256,manifest_bytes,state,verified_at_utc
            FROM recovery_points WHERE point_id=$1 FOR UPDATE
            """, connection, transaction))
        {
            point.Parameters.AddWithValue(payload.RecoveryPointId);
            await using var reader = await point.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != payload.CaptureId ||
                reader.GetString(1) != payload.OwnerId || reader.GetString(2) != payload.LibraryId ||
                reader.GetString(3) != payload.SourceRegion || !SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(4), payload.CapturedAtUtc) ||
                reader.GetString(5) != payload.SnapshotId || reader.GetString(6) != payload.SnapshotLsn ||
                !SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(7), payload.SnapshotCutoffUtc) ||
                reader.GetInt64(8) != payload.ChangeSequence ||
                !SameNullablePostgresTimestamp(reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9), payload.LatestChangedAtUtc) ||
                reader.GetString(10).Trim() != payload.MigrationDigest || reader.GetInt32(11) != payload.SchemaVersion ||
                reader.GetString(12).Trim() != payload.FullManifestSha256 ||
                !((byte[])reader[13]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) ||
                reader.GetString(14) != "verified" || !SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(15), payload.VerifiedAtUtc))
                throw new InvalidDataException("recovery_point_replay_conflict");
        }

        await using var attestation = new NpgsqlCommand("""
            SELECT worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature
            FROM recovery_point_attestations WHERE point_id=$1 FOR UPDATE
            """, connection, transaction);
        attestation.Parameters.AddWithValue(payload.RecoveryPointId);
        await using var readerAttestation = await attestation.ExecuteReaderAsync(cancellationToken);
        if (!await readerAttestation.ReadAsync(cancellationToken) ||
            readerAttestation.GetString(0) != verified.WorkerId || readerAttestation.GetString(1) != verified.SigningKeyId ||
            readerAttestation.GetString(2).Trim() != verified.SigningPublicKeySha256 ||
            readerAttestation.GetString(3).Trim() != verified.PayloadSha256 ||
            !((byte[])readerAttestation[4]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) ||
            !((byte[])readerAttestation[5]).AsSpan().SequenceEqual(verified.Signature))
            throw new InvalidDataException("recovery_point_attestation_conflict");
    }

    private static void EnsureTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection)) throw new InvalidOperationException("recovery_transaction_connection_mismatch");
    }

    private static bool AllowedTransition(string from, string to) => (from, to) switch
    {
        ("requested", "restoring" or "aborted" or "failed" or "expired") => true,
        ("restoring", "validating" or "aborted" or "failed") => true,
        ("validating", "ready" or "aborted" or "failed") => true,
        ("ready", "promoting" or "aborted" or "expired") => true,
        ("promoting", "promoted" or "failed") => true,
        _ => false
    };

    private static DateTimeOffset TruncateToMicrosecond(DateTimeOffset value) =>
        new(value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % 10, TimeSpan.Zero);

    private static bool SameNullablePostgresTimestamp(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right is null : right is not null && SamePostgresTimestamp(left.Value, right.Value);

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime().Ticks / 10 == right.ToUniversalTime().Ticks / 10;

    private sealed record ValidationDigestPayload(
        Guid SessionId,
        string SourceManifestSha256,
        string TargetDigest,
        string State,
        IReadOnlyList<string> Findings,
        DateTimeOffset ValidatedAtUtc);

    private sealed record MaterializationDigestPayload(Guid SessionId, RecoveryMaterializedObjectV1 Receipt);
}
