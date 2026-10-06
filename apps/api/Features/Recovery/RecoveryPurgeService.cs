using System.Globalization;
using Npgsql;
using Platform.Recovery;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Recovery;

public sealed record RecoveryPurgeProposalRequest(Guid AssetId, Guid? EditId, string Category);
public sealed record RecoveryPurgeProposalView(RecoveryPurgeProposalV1 Proposal, string Phase);

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
}
