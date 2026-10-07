using System.Security.Cryptography;
using Npgsql;
using Platform.Recovery;
using Platform.Storage;

namespace Platform.Api.Features.Recovery;

public sealed record RecoveryRestoreRequest(Guid RecoveryPointId, RecoveryRestoreScopeV1 Scope, string Reason);
public sealed class RecoveryRestoreRequestException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Records an owner-approved request only; the independent executor must
/// reverify the point and purge authority before retrieval, review or promotion.</summary>
public sealed class RecoveryRestoreRequestService(NpgsqlDataSource source, IConfiguration configuration,
    TimeProvider? clock = null)
{
    public async Task<RecoverySessionView> RequestAsync(string owner, RecoveryRestoreRequest? request,
        string idempotencyKey, string operationId, CancellationToken ct)
    {
        var library = RecoveryHealthService.ConfiguredLibrary(configuration);
        if (!RecoveryHealthService.IsEnabled(configuration) || configuration.GetValue<bool>("Recovery:RecoveryOnly") ||
            string.IsNullOrWhiteSpace(library) || !RecoveryHealthService.TryGetTrust(configuration,out var trust))
            throw new RecoveryUnavailableException();
        if (request is null || request.RecoveryPointId==Guid.Empty || request.Scope is null ||
            string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(request.Reason) || request.Scope.Reason is not null ||
            idempotencyKey.Length is < 1 or > 128 || idempotencyKey.Any(character=>character<'!' || character>'~'))
            throw new RecoveryRestoreRequestException("restore_request_invalid",400);
        var scope=request.Scope with { Kind=request.Scope.Kind=="library" ? "full_library" : request.Scope.Kind, Reason=request.Reason };
        try { scope.Validate(); }
        catch(InvalidDataException) { throw new RecoveryRestoreRequestException("restore_request_invalid",400); }
        var sessionId=new Guid(SHA256.HashData(RecoveryEvidenceCodec.SerializeCanonical(new
            { domain="recovery_restore_request_v1",owner,library,idempotencyKey }))[..16]);
        var now=(clock??TimeProvider.System).GetUtcNow().ToUniversalTime();
        now=now.AddTicks(-(now.Ticks%10));
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await IngestDb.LockOwnerAsync(connection,owner,ct);
        DateTimeOffset? existingExpiry;
        await using(var existing=new NpgsqlCommand("SELECT expires_at_utc FROM recovery_sessions WHERE session_id=$1 AND owner_id=$2 AND library_id=$3 FOR UPDATE",connection,transaction))
        {
            existing.Parameters.AddWithValue(sessionId);existing.Parameters.AddWithValue(owner);existing.Parameters.AddWithValue(library);
            await using var reader=await existing.ExecuteReaderAsync(ct);
            existingExpiry=await reader.ReadAsync(ct) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
        }
        if(existingExpiry<=now) throw new RecoveryRestoreRequestException("restore_expired",410);
        await using(var frozen=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM recovery_purge_freezes f JOIN recovery_purge_proposals p USING(operation_id)
              WHERE f.owner_id=$1 AND f.library_id=$2 AND p.phase NOT IN ('completed','aborted')
                AND ($3::uuid IS NULL OR f.asset_id=$3) AND ($4::uuid IS NULL OR f.category='original' OR f.edit_id=$4))
              OR EXISTS(SELECT 1 FROM media_purge_tombstones t WHERE t.owner_id=$1 AND t.library_id=$2
                AND $3::uuid IS NOT NULL AND t.asset_id=$3 AND ($4::uuid IS NULL OR t.category='original' OR t.edit_id=$4))
            """,connection,transaction))
        {
            frozen.Parameters.AddWithValue(owner);frozen.Parameters.AddWithValue(library);
            frozen.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=scope.AssetId is { } asset ? asset : DBNull.Value });
            frozen.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=scope.EditId is { } edit ? edit : DBNull.Value });
            if(await frozen.ExecuteScalarAsync(ct) is true) throw new RecoveryRestoreRequestException("purge_not_eligible",409);
        }
        RecoveryCatalogEnvelopeV1 envelope;
        await using(var point=new NpgsqlCommand("""
            SELECT a.worker_id,a.signing_key_id,a.signing_public_key_sha256,a.payload_sha256,a.canonical_payload,a.signature
            FROM recovery_points p JOIN recovery_point_attestations a USING(point_id)
            WHERE p.point_id=$1 AND p.owner_id=$2 AND p.library_id=$3 AND p.state='verified'
            """,connection,transaction))
        {
            point.Parameters.AddWithValue(request.RecoveryPointId);point.Parameters.AddWithValue(owner);point.Parameters.AddWithValue(library);
            await using var reader=await point.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)) throw new RecoveryRestoreRequestException("recovery_point_not_found",404);
            envelope=new(1,"recovery_point",reader.GetString(0),reader.GetString(1),reader.GetString(2).Trim(),reader.GetString(3).Trim(),
                Convert.ToBase64String((byte[])reader[4]),Convert.ToBase64String((byte[])reader[5]));
        }
        try
        {
            var point=await RestoreSessionStore.ImportVerifiedPointAsync(connection,transaction,envelope,trust,ct);
            if(point.Payload.RecoveryPointId!=request.RecoveryPointId || point.Payload.OwnerId!=owner || point.Payload.LibraryId!=library)
                throw new InvalidDataException("foreign_point");
            var proposal=RecoveryRestoreProposalV1.Create(sessionId,owner,library,request.RecoveryPointId,scope,existingExpiry??now.AddHours(23));
            await RestoreSessionStore.ImportProposalAsync(connection,transaction,proposal,ct);
        }
        catch(Exception error) when(error is InvalidDataException or CryptographicException or FormatException)
        { throw new RecoveryRestoreRequestException(existingExpiry is null ? "recovery_point_unverified" : "restore_request_conflict",409); }
        if(existingExpiry is null)
            await IngestDb.AuditAndRecordRecoveryChangeAsync(connection,transaction,owner,library,operationId,
                "restore_requested",sessionId,"accepted","restore-session",sessionId.ToString("D"),[],ct);
        await transaction.CommitAsync(ct);
        return await new RecoveryOperationQueryService(source,configuration,clock).GetSessionAsync(owner,sessionId,ct)
            ?? throw new RecoveryUnavailableException();
    }
}
