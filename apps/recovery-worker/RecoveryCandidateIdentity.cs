using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCandidateIdentity
{
    internal static RecoveryIdentityApprovalArtifactV1 VerifyApproval(byte[] artifactBytes,
        RecoverySigningTrust trust, string ownerId, string issuer, string audience, DateTimeOffset nowUtc)
    {
        if (artifactBytes.Length is < 1 or > 64 * 1024)
            throw new RecoveryWorkerException("candidate_identity_approval_invalid");
        try
        {
            var artifact = JsonSerializer.Deserialize<RecoveryIdentityApprovalArtifactV1>(artifactBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("candidate_identity_approval_invalid");
            var payload = artifact.Payload;
            if (!artifactBytes.AsSpan().SequenceEqual(RecoveryEvidenceCodec.SerializeCanonical(artifact)) ||
                payload is null || payload.SchemaVersion != 1 || payload.MappingId == Guid.Empty ||
                payload.PreservedOwnerId != ownerId || payload.Issuer != issuer || payload.Audience != audience ||
                payload.ApprovalKeyId != trust.KeyId || payload.ApprovedAtUtc.Offset != TimeSpan.Zero ||
                payload.ApprovedAtUtc.Ticks % 10 != 0 ||
                payload.ApprovedAtUtc > nowUtc || payload.ApprovedAtUtc < nowUtc.AddHours(-24) ||
                string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > 256 || payload.Subject.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(payload.ApprovedBy) || payload.ApprovedBy.Length > 512 || payload.ApprovedBy.Any(char.IsControl))
                throw new InvalidDataException("candidate_identity_approval_binding_mismatch");
            var spki = trust.ValidateAndGetPublicKey();
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || !rsa.VerifyData(RecoveryEvidenceCodec.SerializeCanonical(payload),
                    Convert.FromBase64String(artifact.SignatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("candidate_identity_approval_signature_invalid");
            return artifact;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or CryptographicException or FormatException)
        {
            throw new RecoveryWorkerException("candidate_identity_approval_invalid", exception);
        }
    }

    internal static async Task InstallAsync(NpgsqlConnection connection, Guid candidateId,
        byte[] artifactBytes, RecoverySigningTrust trust, RecoveryWorkerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.Required("RecoveryOnly") != "true" || !connection.Database.StartsWith("recovery_drill_", StringComparison.Ordinal))
            throw new RecoveryWorkerException("candidate_identity_target_invalid");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var locked = new NpgsqlCommand("""
            SELECT owner_id FROM recovery_candidate_sessions WHERE candidate_id=$1
              AND target_database_name=current_database() AND state='owner_access_pending' AND expires_at_utc>now()
            FOR UPDATE
            """, connection, transaction);
        locked.Parameters.AddWithValue(candidateId);
        var owner = await locked.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new RecoveryWorkerException("candidate_identity_session_missing");
        var artifact = VerifyApproval(artifactBytes, trust, owner, configuration.Required("OwnerUserPoolIssuer"),
            configuration.Required("OwnerUserPoolClientId"), DateTimeOffset.UtcNow);
        var payload = artifact.Payload;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO recovery_identity_mappings(mapping_id,issuer,audience,subject,owner_id,approved_by,
                approval_key_id,approval_receipt,approved_at_utc)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT(mapping_id) DO NOTHING
            """, connection, transaction);
        object[] values = [payload.MappingId,payload.Issuer,payload.Audience,payload.Subject,payload.PreservedOwnerId,
            payload.ApprovedBy,payload.ApprovalKeyId,Convert.FromBase64String(artifact.SignatureBase64),payload.ApprovedAtUtc];
        foreach (var value in values) insert.Parameters.AddWithValue(value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await using var replay = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM recovery_identity_mappings WHERE mapping_id=$1 AND issuer=$2 AND audience=$3
                AND subject=$4 AND owner_id=$5 AND approved_by=$6 AND approval_key_id=$7 AND approval_receipt=$8
                AND approved_at_utc=$9 AND revoked_at_utc IS NULL)
            """, connection, transaction);
        foreach (var value in values) replay.Parameters.AddWithValue(value);
        if (await replay.ExecuteScalarAsync(cancellationToken) is not true)
            throw new RecoveryWorkerException("candidate_identity_replay_mismatch");
        await using var update = new NpgsqlCommand("""
            UPDATE recovery_candidate_sessions SET owner_mapping_approval_sha256=$2
            WHERE candidate_id=$1 AND (owner_mapping_approval_sha256 IS NULL OR owner_mapping_approval_sha256=$2)
            """, connection, transaction);
        update.Parameters.AddWithValue(candidateId);
        update.Parameters.AddWithValue(RecoveryEvidenceCodec.Hash(artifactBytes));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new RecoveryWorkerException("candidate_identity_replay_mismatch");
        await transaction.CommitAsync(cancellationToken);
    }
}
