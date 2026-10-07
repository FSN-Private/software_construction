using System.Globalization;
using Npgsql;
using Platform.Recovery;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Recovery;

public sealed record RecoveryPurgeProposalRequest(Guid AssetId, Guid? EditId, string Category);
public sealed record RecoveryPurgeProposalView(RecoveryPurgeProposalV1 Proposal, string Phase);
public sealed record RecoveryPurgeFreezeView(RecoveryPurgeFreezeAcknowledgementV1 Acknowledgement, string Phase);

public sealed class RecoveryPurgeException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Creates reversible, owner-authenticated proposals only. It never writes a media
/// tombstone or purge journal; an independently MFA-authorized recovery operator acts later.</summary>
public sealed class RecoveryPurgeService(NpgsqlDataSource source, IConfiguration configuration,
    TimeProvider? clock = null)
{
    public async Task<RecoveryPurgeProposalView> ProposeAsync(string ownerId,
        RecoveryPurgeProposalRequest? request, string operationId, CancellationToken cancellationToken)
    {
        var libraryId = RecoveryHealthService.ConfiguredLibrary(configuration);
        if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly") ||
            string.IsNullOrWhiteSpace(libraryId))
            throw new RecoveryPurgeException("recovery_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (request is null || string.IsNullOrWhiteSpace(ownerId) || request.AssetId == Guid.Empty || request.EditId == Guid.Empty ||
            request.Category is not ("original" or "xmp") ||
            request.Category == "original" && request.EditId is not null ||
            request.Category == "xmp" && request.EditId is null)
            throw new RecoveryPurgeException("purge_proposal_invalid", StatusCodes.Status400BadRequest);

        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, ownerId, cancellationToken);

        string contentSha;
        long lifecycleVersion;
        DateTimeOffset eligibleAt;
        if (request.Category == "original")
        {
            await using var command = new NpgsqlCommand("""
                SELECT a.original_sha256,a.state,a.lifecycle_version,a.deleted_until
                FROM assets a JOIN content_objects c ON c.sha256=a.original_sha256
                WHERE a.id=$1 AND a.owner_id=$2 AND c.category<>'xmp' AND c.integrity_state='authoritative'
                FOR UPDATE OF a
                """, connection, transaction);
            command.Parameters.AddWithValue(request.AssetId);
            command.Parameters.AddWithValue(ownerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RecoveryPurgeException("purge_target_not_found", StatusCodes.Status404NotFound);
            contentSha = reader.GetString(0).Trim();
            if (reader.GetString(1) != "recoverable-deleted" || reader.IsDBNull(3))
                throw new RecoveryPurgeException("purge_target_not_eligible", StatusCodes.Status409Conflict);
            lifecycleVersion = reader.GetInt64(2);
            eligibleAt = reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime();
        }
        else
        {
            await using var command = new NpgsqlCommand("""
                SELECT e.xmp_sha256,e.state,e.lifecycle_version,e.deleted_until
                FROM edit_identities e JOIN assets a ON a.id=e.asset_id
                JOIN content_objects c ON c.sha256=e.xmp_sha256
                WHERE e.id=$1 AND e.asset_id=$2 AND a.owner_id=$3 AND a.state IN ('active','restored')
                  AND c.category='xmp' AND c.integrity_state='authoritative'
                FOR UPDATE OF e,a
                """, connection, transaction);
            command.Parameters.AddWithValue(request.EditId!.Value);
            command.Parameters.AddWithValue(request.AssetId);
            command.Parameters.AddWithValue(ownerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RecoveryPurgeException("purge_target_not_found", StatusCodes.Status404NotFound);
            contentSha = reader.GetString(0).Trim();
            if (reader.GetString(1) != "recoverable-deleted" || reader.IsDBNull(3))
                throw new RecoveryPurgeException("purge_target_not_eligible", StatusCodes.Status409Conflict);
            lifecycleVersion = reader.GetInt64(2);
            eligibleAt = reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime();
        }

        if (eligibleAt > now)
            throw new RecoveryPurgeException("purge_retention_not_eligible", StatusCodes.Status409Conflict);
        // A purge request made after the original deletion grace period starts a new, explicit
        // approval window at the request time; the signed proposal must not predate itself.
        eligibleAt = eligibleAt < now ? now : eligibleAt;
        if (await ScalarAsync(connection,
                "SELECT 1 FROM lifecycle_holds WHERE asset_id=$1 AND (expires_at IS NULL OR expires_at>$2) LIMIT 1",
                cancellationToken, request.AssetId, now) is not null)
            throw new RecoveryPurgeException("purge_active_lifecycle_hold", StatusCodes.Status409Conflict);
        if (await ScalarAsync(connection, """
            SELECT 1 FROM recovery_purge_proposals
             WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id IS NOT DISTINCT FROM $4
               AND category=$5 AND phase NOT IN ('completed','failed') LIMIT 1
            """, cancellationToken, ownerId, libraryId, request.AssetId, request.EditId, request.Category) is not null)
            throw new RecoveryPurgeException("purge_proposal_exists", StatusCodes.Status409Conflict);

        var generation = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT GREATEST(
                COALESCE((SELECT MAX(lifecycle_generation) FROM recovery_purge_proposals WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id IS NOT DISTINCT FROM $4 AND category=$5),0),
                COALESCE((SELECT MAX(lifecycle_generation) FROM media_purge_tombstones WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id IS NOT DISTINCT FROM $4 AND category=$5),0),
                COALESCE((SELECT MAX(lifecycle_generation) FROM recovery_purge_journals WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id IS NOT DISTINCT FROM $4 AND category=$5),0)
            ) + 1
            """, cancellationToken, ownerId, libraryId, request.AssetId, request.EditId, request.Category), CultureInfo.InvariantCulture);
        var proposal = RecoveryPurgeProposalV1.Create(Guid.NewGuid(), Guid.NewGuid(), ownerId, libraryId,
            request.AssetId, request.EditId, request.Category, contentSha, lifecycleVersion, generation,
            now, eligibleAt, now.AddDays(7));
        proposal.Validate(now);
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(proposal);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_purge_proposals(
                operation_id,event_id,owner_id,library_id,asset_id,edit_id,category,content_sha256,
                expected_lifecycle_version,lifecycle_generation,proposed_at_utc,eligible_at_utc,expires_at_utc,
                proposal_digest,canonical_proposal,phase,primary_actor,reason_code)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,'awaiting_independent_approval',$3,'owner_requested')
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(proposal.OperationId);
            insert.Parameters.AddWithValue(proposal.EventId);
            insert.Parameters.AddWithValue(proposal.OwnerId);
            insert.Parameters.AddWithValue(proposal.LibraryId);
            insert.Parameters.AddWithValue(proposal.AssetId);
            insert.Parameters.AddWithValue((object?)proposal.EditId ?? DBNull.Value);
            insert.Parameters.AddWithValue(proposal.Category);
            insert.Parameters.AddWithValue(proposal.ContentSha256);
            insert.Parameters.AddWithValue(proposal.ExpectedLifecycleVersion);
            insert.Parameters.AddWithValue(proposal.Generation);
            insert.Parameters.AddWithValue(proposal.ProposedAtUtc);
            insert.Parameters.AddWithValue(proposal.EligibleAtUtc);
            insert.Parameters.AddWithValue(proposal.ExpiresAtUtc);
            insert.Parameters.AddWithValue(proposal.PlanDigest);
            insert.Parameters.AddWithValue(bytes);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAndRecordRecoveryChangeAsync(connection, transaction, ownerId, libraryId, operationId,
            "recovery.purge_proposed", request.AssetId, "accepted", "recovery_purge_proposal",
            proposal.OperationId.ToString("D"), [contentSha], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(proposal, "awaiting_independent_approval");
    }

    public async Task<RecoveryPurgeProposalView?> GetProposalAsync(string ownerId, Guid operationId,
        CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("Recovery:RecoveryOnly")) throw new RecoveryUnavailableException();
        var libraryId = RecoveryHealthService.ConfiguredLibrary(configuration);
        if (string.IsNullOrWhiteSpace(libraryId)) throw new RecoveryUnavailableException();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT canonical_proposal,phase,proposal_digest
            FROM recovery_purge_proposals WHERE operation_id=$1 AND owner_id=$2 AND library_id=$3
            """, connection);
        command.Parameters.AddWithValue(operationId);
        command.Parameters.AddWithValue(ownerId);
        command.Parameters.AddWithValue(libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var bytes = reader.GetFieldValue<byte[]>(0);
        var proposal = RecoveryPurgeProposalV1.ParseCanonical(bytes);
        if (proposal.OwnerId != ownerId || proposal.LibraryId != libraryId || proposal.OperationId != operationId ||
            proposal.PlanDigest != reader.GetString(2).Trim())
            throw new RecoveryUnavailableException();
        return new(proposal, reader.GetString(1));
    }

    public async Task<RecoveryPurgeFreezeView> FreezeAsync(string ownerId, Guid operationId,
        DateTimeOffset authenticatedAtUtc, string requestOperationId, CancellationToken cancellationToken)
    {
        var libraryId = RecoveryHealthService.ConfiguredLibrary(configuration);
        if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly") ||
            string.IsNullOrWhiteSpace(libraryId))
            throw new RecoveryPurgeException("recovery_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (string.IsNullOrWhiteSpace(ownerId) || operationId == Guid.Empty)
            throw new RecoveryPurgeException("purge_freeze_invalid", StatusCodes.Status400BadRequest);

        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        authenticatedAtUtc = authenticatedAtUtc.ToUniversalTime();
        if (authenticatedAtUtc > now || now - authenticatedAtUtc > TimeSpan.FromMinutes(5))
            throw new RecoveryPurgeException("recent_authentication_required", StatusCodes.Status403Forbidden);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, ownerId, cancellationToken);

        RecoveryPurgeProposalV1 proposal;
        string phase;
        await using (var command = new NpgsqlCommand("""
            SELECT canonical_proposal,phase,proposal_digest
              FROM recovery_purge_proposals
             WHERE operation_id=$1 AND owner_id=$2 AND library_id=$3
             FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(operationId);
            command.Parameters.AddWithValue(ownerId);
            command.Parameters.AddWithValue(libraryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RecoveryPurgeException("purge_proposal_not_found", StatusCodes.Status404NotFound);
            var bytes = reader.GetFieldValue<byte[]>(0);
            proposal = RecoveryPurgeProposalV1.ParseCanonical(bytes);
            phase = reader.GetString(1);
            if (!RecoveryEvidenceCodec.SerializeCanonical(proposal).AsSpan().SequenceEqual(bytes) ||
                proposal.OperationId != operationId || proposal.OwnerId != ownerId || proposal.LibraryId != libraryId ||
                proposal.PlanDigest != reader.GetString(2).Trim())
                throw new RecoveryUnavailableException();
        }

        // Replays return the exact immutable acknowledgement, never a refreshed authority.
        RecoveryPurgeFreezeAcknowledgementV1? priorAcknowledgement = null;
        string? priorAcknowledgementDigest = null;
        await using (var existingCommand = new NpgsqlCommand("""
            SELECT canonical_freeze_ack,freeze_ack_sha256
              FROM recovery_purge_freezes
             WHERE operation_id=$1 AND owner_id=$2 AND library_id=$3
            """, connection, transaction))
        {
            existingCommand.Parameters.AddWithValue(operationId);
            existingCommand.Parameters.AddWithValue(ownerId);
            existingCommand.Parameters.AddWithValue(libraryId);
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var bytes = reader.GetFieldValue<byte[]>(0);
                priorAcknowledgement = RecoveryPurgeFreezeAcknowledgementV1.ParseCanonical(bytes);
                priorAcknowledgementDigest = reader.GetString(1).Trim();
            }
        }
        if (priorAcknowledgement is not null)
        {
            if (priorAcknowledgement.Digest() != priorAcknowledgementDigest || phase != "executing")
                throw new RecoveryUnavailableException();
            priorAcknowledgement.Validate(proposal);
            await transaction.CommitAsync(cancellationToken);
            return new(priorAcknowledgement, phase);
        }

        if (phase is not ("proposal" or "awaiting_independent_approval") || now < proposal.EligibleAtUtc)
            throw new RecoveryPurgeException("purge_freeze_phase_conflict", StatusCodes.Status409Conflict);

        // Serialize the freeze with every writer that can add a reference to this payload.
        // Content locks come before asset locks in the shared VC writer path.
        // Advance the payload row's MVCC version while acquiring its exclusive lock. A
        // repeatable-read/serializable writer that took an older snapshot and is queued on
        // the same digest will then fail with 40001 instead of reading the pre-freeze phase.
        if (await ScalarAsync(connection,
                "UPDATE content_objects SET storage_version=storage_version WHERE sha256=$1 RETURNING sha256",
                cancellationToken, proposal.ContentSha256) is null)
            throw new RecoveryPurgeException("purge_target_changed", StatusCodes.Status409Conflict);
        var relatedAssetIds = new List<Guid>();
        await using (var related = new NpgsqlCommand("""
            SELECT a.id FROM assets a
             WHERE a.original_sha256=$1
                OR EXISTS (SELECT 1 FROM edit_identities e WHERE e.asset_id=a.id AND e.xmp_sha256=$1)
                OR EXISTS (SELECT 1 FROM vc_commit_entries c
                            WHERE c.asset_id=a.id AND (c.base_content_sha256=$1 OR c.content_sha256=$1))
             ORDER BY a.id FOR UPDATE
            """, connection, transaction))
        {
            related.Parameters.AddWithValue(proposal.ContentSha256);
            await using var reader = await related.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) relatedAssetIds.Add(reader.GetGuid(0));
        }

        string contentSha;
        string targetState;
        long lifecycleVersion;
        DateTimeOffset? deletedUntil;
        if (proposal.Category == "original")
        {
            await using var target = new NpgsqlCommand("""
                SELECT original_sha256,state,lifecycle_version,deleted_until
                  FROM assets WHERE id=$1 AND owner_id=$2 FOR UPDATE
                """, connection, transaction);
            target.Parameters.AddWithValue(proposal.AssetId);
            target.Parameters.AddWithValue(ownerId);
            await using var reader = await target.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RecoveryPurgeException("purge_target_not_found", StatusCodes.Status404NotFound);
            contentSha = reader.GetString(0).Trim();
            targetState = reader.GetString(1);
            lifecycleVersion = reader.GetInt64(2);
            deletedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime();
        }
        else
        {
            await using var target = new NpgsqlCommand("""
                SELECT e.xmp_sha256,e.state,e.lifecycle_version,e.deleted_until,a.owner_id,a.state
                  FROM edit_identities e JOIN assets a ON a.id=e.asset_id
                 WHERE e.id=$1 AND e.asset_id=$2 AND a.owner_id=$3 FOR UPDATE OF e,a
                """, connection, transaction);
            target.Parameters.AddWithValue(proposal.EditId!.Value);
            target.Parameters.AddWithValue(proposal.AssetId);
            target.Parameters.AddWithValue(ownerId);
            await using var reader = await target.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RecoveryPurgeException("purge_target_not_found", StatusCodes.Status404NotFound);
            contentSha = reader.GetString(0).Trim();
            targetState = reader.GetString(1);
            lifecycleVersion = reader.GetInt64(2);
            deletedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime();
            if (reader.GetString(4) != ownerId || reader.GetString(5) is not ("active" or "restored"))
                throw new RecoveryPurgeException("purge_target_changed", StatusCodes.Status409Conflict);
        }

        if (contentSha != proposal.ContentSha256 || targetState != "recoverable-deleted" ||
            lifecycleVersion != proposal.ExpectedLifecycleVersion || deletedUntil is null || deletedUntil > now)
            throw new RecoveryPurgeException("purge_target_changed", StatusCodes.Status409Conflict);
        var activeLifecycleHold = await ScalarAsync(connection,
            "SELECT 1 FROM lifecycle_holds WHERE asset_id=$1 AND (expires_at IS NULL OR expires_at>$2) LIMIT 1",
            cancellationToken, proposal.AssetId, now);
        var activeRecoveryHold = await ScalarAsync(connection, """
            SELECT 1 FROM recovery_holds
             WHERE owner_id=$1 AND library_id=$2 AND released_at_utc IS NULL
               AND (expires_at_utc IS NULL OR expires_at_utc>$3)
               AND (asset_id IS NULL OR asset_id=$4)
               AND (edit_id IS NULL OR edit_id IS NOT DISTINCT FROM $5)
             LIMIT 1
            """, cancellationToken, ownerId, libraryId, now, proposal.AssetId, proposal.EditId);
        if (activeLifecycleHold is not null)
            throw new RecoveryPurgeException("purge_active_lifecycle_hold", StatusCodes.Status409Conflict);
        if (activeRecoveryHold is not null)
            throw new RecoveryPurgeException("purge_active_recovery_hold", StatusCodes.Status409Conflict);

        var acknowledgement = new RecoveryPurgeFreezeAcknowledgementV1(
            RecoveryPurgeFreezeAcknowledgementV1.CurrentSchemaVersion, proposal.OperationId, proposal.EventId,
            proposal.OwnerId, proposal.LibraryId, proposal.AssetId, proposal.EditId, proposal.Category,
            proposal.ContentSha256, proposal.ExpectedLifecycleVersion, proposal.Generation, proposal.PlanDigest,
            ownerId, authenticatedAtUtc, now);
        acknowledgement.Validate(proposal);
        var acknowledgementBytes = acknowledgement.SerializeCanonical();
        var acknowledgementDigest = RecoveryEvidenceCodec.Hash(acknowledgementBytes);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_purge_freezes(
                operation_id,event_id,owner_id,library_id,asset_id,edit_id,category,content_sha256,
                expected_lifecycle_version,lifecycle_generation,proposal_digest,freeze_ack_sha256,
                canonical_freeze_ack,frozen_by,auth_time_utc,frozen_at_utc)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(acknowledgement.OperationId);
            insert.Parameters.AddWithValue(acknowledgement.EventId);
            insert.Parameters.AddWithValue(acknowledgement.OwnerId);
            insert.Parameters.AddWithValue(acknowledgement.LibraryId);
            insert.Parameters.AddWithValue(acknowledgement.AssetId);
            insert.Parameters.AddWithValue((object?)acknowledgement.EditId ?? DBNull.Value);
            insert.Parameters.AddWithValue(acknowledgement.Category);
            insert.Parameters.AddWithValue(acknowledgement.ContentSha256);
            insert.Parameters.AddWithValue(acknowledgement.ExpectedLifecycleVersion);
            insert.Parameters.AddWithValue(acknowledgement.Generation);
            insert.Parameters.AddWithValue(acknowledgement.ProposalDigest);
            insert.Parameters.AddWithValue(acknowledgementDigest);
            insert.Parameters.AddWithValue(acknowledgementBytes);
            insert.Parameters.AddWithValue(ownerId);
            insert.Parameters.AddWithValue(authenticatedAtUtc);
            insert.Parameters.AddWithValue(now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE recovery_purge_proposals SET phase='executing',updated_at_utc=$3
             WHERE operation_id=$1 AND owner_id=$2 AND phase IN ('proposal','awaiting_independent_approval')
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(operationId);
            update.Parameters.AddWithValue(ownerId);
            update.Parameters.AddWithValue(now);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new RecoveryPurgeException("purge_freeze_phase_conflict", StatusCodes.Status409Conflict);
        }
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, ownerId, libraryId, requestOperationId,
            "recovery.purge_frozen", proposal.AssetId, "accepted", "recovery_purge_freeze",
            proposal.OperationId.ToString("D"), [proposal.ContentSha256], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(acknowledgement, "executing");
    }
}
