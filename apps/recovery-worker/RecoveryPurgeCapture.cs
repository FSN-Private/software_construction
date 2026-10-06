using System.Text.Json;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal readonly record struct RecoveryPurgedReference(
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256);

/// <summary>Verifies the owner/MFA-authenticated completion before a capture may omit bytes.
/// A primary timestamp or purge phase alone never authorizes omission.</summary>
internal static class RecoveryPurgeCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    internal static async Task<HashSet<RecoveryPurgedReference>> ReadAuthorizedOmissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryWorkerConfiguration configuration,
        string ownerId,
        string libraryId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        RecoverySigningTrust? trust = null;
        var keyId = configuration.Optional("OwnerApprovalTrust__KeyId");
        var spki = configuration.Optional("OwnerApprovalTrust__PublicKeySpkiBase64");
        var fingerprint = configuration.Optional("OwnerApprovalTrust__PublicKeySha256");
        if (keyId is not null || spki is not null || fingerprint is not null)
        {
            if (keyId is null || spki is null || fingerprint is null)
                throw new RecoveryWorkerException("purge_owner_trust_incomplete");
            trust = new RecoverySigningTrust(keyId, spki, fingerprint);
            try { _ = trust.ValidateAndGetPublicKey(); }
            catch (InvalidDataException exception) { throw new RecoveryWorkerException("purge_owner_trust_invalid", exception); }
        }

        const string sql = """
            SELECT p.canonical_proposal, p.phase, p.proposal_digest,
                   j.phase, j.plan_digest, j.independent_receipt,
                   t.event_id, t.owner_id, t.library_id, t.asset_id, t.edit_id, t.category,
                   t.content_sha256, t.lifecycle_generation, t.recovery_acknowledged_at_utc, t.completed_at_utc,
                   c.canonical_payload, c.signature, c.signing_key_id, c.signing_public_key_sha256,
                   c.payload_sha256, c.completed_at_utc
              FROM recovery_purge_proposals p
              LEFT JOIN recovery_purge_journals j ON j.operation_id=p.operation_id
              LEFT JOIN media_purge_tombstones t ON t.event_id=p.event_id
              LEFT JOIN recovery_purge_completions c ON c.event_id=p.event_id
             WHERE p.owner_id=$1 AND p.library_id=$2
             ORDER BY p.proposed_at_utc, p.operation_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(ownerId);
        command.Parameters.AddWithValue(libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var omissions = new HashSet<RecoveryPurgedReference>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var phase = reader.GetString(1);
            var hasJournal = !reader.IsDBNull(3);
            var hasTombstone = !reader.IsDBNull(6);
            var hasCompletion = !reader.IsDBNull(16);
            if (phase is "proposal" or "awaiting_independent_approval")
            {
                if (hasJournal || hasTombstone || hasCompletion)
                    throw new RecoveryWorkerException("purge_reversible_state_conflict");
                continue;
            }
            if (phase != "completed" || !hasJournal || !hasTombstone || !hasCompletion || trust is null)
                throw new RecoveryWorkerException("purge_not_independently_completed");
            if (reader.GetString(3) != "completed" || reader.IsDBNull(4) || reader.IsDBNull(5) ||
                reader.IsDBNull(14) || reader.IsDBNull(15) || reader.IsDBNull(21))
                throw new RecoveryWorkerException("purge_completion_state_incomplete");

            var proposalBytes = reader.GetFieldValue<byte[]>(0);
            var proposal = Deserialize<RecoveryPurgeProposalV1>(proposalBytes, "purge_proposal_invalid");
            proposal.Validate(proposal.ProposedAtUtc);
            if (!RecoveryEvidenceCodec.SerializeCanonical(proposal).AsSpan().SequenceEqual(proposalBytes) ||
                proposal.OwnerId != ownerId || proposal.LibraryId != libraryId ||
                proposal.PlanDigest != reader.GetString(2).Trim())
                throw new RecoveryWorkerException("purge_proposal_binding_invalid");

            var approvalBytes = reader.GetFieldValue<byte[]>(5);
            var approvalEnvelope = RecoveryPurgeApprovalCodec.ParseEnvelope(approvalBytes);
            var approval = RecoveryPurgeApprovalCodec.VerifyForCapture(approvalEnvelope, trust, proposal,
                reader.GetString(4).Trim(), nowUtc, allowExpiredForCompletedPurge: true);

            var completionPayloadBytes = reader.GetFieldValue<byte[]>(16);
            var completionEnvelope = new RecoveryPurgeCompletionEnvelopeV1(1, "recovery_purge_completion",
                reader.GetString(18), reader.GetString(19).Trim(), reader.GetString(20).Trim(),
                Convert.ToBase64String(completionPayloadBytes),
                Convert.ToBase64String(reader.GetFieldValue<byte[]>(17)));
            var completed = RecoveryPurgeCompletionCodec.Verify(completionEnvelope, trust, proposal,
                approval, nowUtc).Payload;

            var tombstoneEvent = reader.GetGuid(6);
            var tombstoneEdit = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10);
            var tombstoneCompleted = reader.GetFieldValue<DateTimeOffset>(15).ToUniversalTime();
            var completionCompleted = reader.GetFieldValue<DateTimeOffset>(21).ToUniversalTime();
            if (tombstoneEvent != proposal.EventId || reader.GetString(7) != ownerId ||
                reader.GetString(8) != libraryId || reader.GetGuid(9) != proposal.AssetId ||
                tombstoneEdit != proposal.EditId || reader.GetString(11) != proposal.Category ||
                reader.GetString(12).Trim() != proposal.ContentSha256 || reader.GetInt64(13) != proposal.Generation ||
                completed.CompletedAtUtc != tombstoneCompleted || completed.CompletedAtUtc != completionCompleted ||
                completed.OperationId != proposal.OperationId)
                throw new RecoveryWorkerException("purge_completion_row_binding_invalid");

            omissions.Add(new(ownerId, libraryId, proposal.AssetId, proposal.EditId,
                proposal.Category, proposal.ContentSha256));
        }
        return omissions;
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> bytes, string safeCode)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new RecoveryWorkerException(safeCode);
        }
        catch (JsonException exception)
        {
            throw new RecoveryWorkerException(safeCode, exception);
        }
    }
}
