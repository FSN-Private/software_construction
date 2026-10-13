using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

// Explicitly approved verifier-only custody: purge-completion-proposal.md,
// Hold-release custody and private-file handoff. Purge never reads this table.
internal static class RecoveryHoldReleaseStore
{
    internal static async Task<RecoveryHoldReleaseEnvelopeV1> PersistAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration, IRecoveryEvidenceSigner signer, RecoverySigningTrust recoveryTrust,
        RecoverySigningTrust ownerTrust, VerifiedCopiedPoint point, Dictionary<string, string?> capturedRow,
        RecoveryHoldReleaseEnvelopeV1 approvalEnvelope, CancellationToken ct,
        (Guid PointId, DateTimeOffset CreatedAtUtc)? lifecycleCreation = null, string? lifecycleAssetOwnerId = null)
    {
        // The verifier calls this only after pinned native dump/graph/audit validation.
        var now = DateTimeOffset.UtcNow;
        var requested = RecoveryHoldReleaseCodec.VerifyApprovalSignature(approvalEnvelope, ownerTrust);
        var hold = RecoveryPurgeRunner.ReadHoldObservation(capturedRow, requested.Kind, point.SourceManifest.SnapshotCutoffUtc);
        var key = Key(point.Pointer.PointId, hold.ScopeSha256);
        var prior = await ReadAsync(dynamo, configuration, key, recoveryTrust, ownerTrust, now, ct);
        var priorProof = prior is null ? null : RecoveryHoldReleaseCodec.VerifyProof(prior, recoveryTrust, ownerTrust,
            configuration.WorkerBuildDigest, now).Payload;
        var approval = RecoveryHoldReleaseCodec.VerifyApproval(approvalEnvelope, ownerTrust, priorProof?.ObservedAtUtc ?? now);
        var validScope = approval.Kind == "recovery"
            ? capturedRow.GetValueOrDefault("owner_id") == approval.OwnerId && capturedRow.GetValueOrDefault("library_id") == approval.LibraryId &&
                DateTimeOffset.TryParse(capturedRow.GetValueOrDefault("created_at_utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var created) && created.ToUniversalTime() == approval.CreatedAtUtc
            : lifecycleCreation is { } origin && origin.PointId == point.Pointer.PointId &&
                origin.CreatedAtUtc == approval.CreatedAtUtc && lifecycleAssetOwnerId == approval.OwnerId &&
                (hold.CreatedAtUtc is null || hold.CreatedAtUtc == origin.CreatedAtUtc);
        if (!validScope || approval.OwnerId != point.Pointer.OwnerId || approval.LibraryId != point.Pointer.LibraryId ||
            approval.OwnerId != configuration.Required("OwnerId") || approval.LibraryId != configuration.Required("LibraryId") ||
            hold.HoldId != approval.HoldId || hold.ScopeSha256 != approval.DefinitionSha256 ||
            point.CheckpointEnvelopeSha256 != point.Pointer.CheckpointSha256 || point.Pointer.CopiedAtUtc > now ||
            signer.KeyId != recoveryTrust.KeyId || signer.PublicKeySha256 != recoveryTrust.PublicKeySha256)
            throw new RecoveryWorkerException("hold_release_captured_definition_mismatch");
        if (prior is not null)
        {
            RequireReplay(priorProof!, point, approvalEnvelope);
            return prior;
        }
        var payload = new RecoveryHoldReleaseProofV1(1, point.Pointer.PointId, point.Pointer.CaptureId,
            approval.OwnerId, approval.LibraryId, approval.HoldId, approval.Kind, hold.ScopeSha256, approval.CreatedAtUtc,
            point.Pointer.CheckpointSha256, now, configuration.WorkerId, configuration.WorkerBuildDigest, approvalEnvelope);
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
        var signature = await signer.SignDigestAsync(SHA256.HashData(bytes), ct);
        var envelope = new RecoveryHoldReleaseEnvelopeV1(1, "recovery_hold_release", signer.KeyId,
            signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes), Convert.ToBase64String(signature));
        _ = RecoveryHoldReleaseCodec.VerifyProof(envelope, recoveryTrust, ownerTrust, configuration.WorkerBuildDigest, now);
        var item = new Dictionary<string, AttributeValue>(key)
        {
            ["ownerId"] = S(payload.OwnerId),
            ["libraryId"] = S(payload.LibraryId),
            ["pointId"] = S(payload.PointId.ToString("D")),
            ["captureId"] = S(payload.CaptureId.ToString("D")),
            ["checkpointSha256"] = S(payload.CheckpointSha256),
            ["payloadSha256"] = S(envelope.PayloadSha256),
            ["envelopeBytes"] = S(Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(envelope))),
            ["verifiedAtUtc"] = S(payload.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ["workerBuildDigest"] = S(payload.WorkerBuildDigest)
        };
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = configuration.Required("VerificationTableName"),
                Item = item,
                ConditionExpression = "attribute_not_exists(pk)"
            }, ct);
            return envelope;
        }
        catch (ConditionalCheckFailedException)
        {
            var latest = await ReadAsync(dynamo, configuration, key, recoveryTrust, ownerTrust, now, ct)
                ?? throw new RecoveryWorkerException("hold_release_write_unconfirmed");
            RequireReplay(RecoveryHoldReleaseCodec.VerifyProof(latest, recoveryTrust, ownerTrust,
                configuration.WorkerBuildDigest, now).Payload, point, approvalEnvelope);
            return latest;
        }
    }

    internal static async Task<RecoveryHoldReleaseEnvelopeV1?> ExportAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration, Guid pointId, string definitionSha256,
        RecoverySigningTrust recoveryTrust, RecoverySigningTrust ownerTrust, CancellationToken ct)
    {
        if (pointId == Guid.Empty) throw new RecoveryWorkerException("hold_release_point_invalid");
        if (definitionSha256.Length != 64 || definitionSha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            definitionSha256 != definitionSha256.ToLowerInvariant())
            throw new RecoveryWorkerException("hold_release_definition_invalid");
        return await ReadAsync(dynamo, configuration, Key(pointId, definitionSha256), recoveryTrust, ownerTrust,
            DateTimeOffset.UtcNow, ct);
    }

    private static async Task<RecoveryHoldReleaseEnvelopeV1?> ReadAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration, Dictionary<string, AttributeValue> key,
        RecoverySigningTrust recoveryTrust, RecoverySigningTrust ownerTrust, DateTimeOffset now, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = configuration.Required("VerificationTableName"),
            Key = key,
            ConsistentRead = true
        }, ct);
        if (response.Item is not { Count: > 0 } item) return null;
        var text = Value(item, "envelopeBytes");
        if (Encoding.UTF8.GetByteCount(text) is < 1 or > 64 * 1024) throw new RecoveryWorkerException("hold_release_record_invalid");
        var envelope = RecoveryHoldReleaseCodec.ParseEnvelope(Encoding.UTF8.GetBytes(text));
        var proof = RecoveryHoldReleaseCodec.VerifyProof(envelope, recoveryTrust, ownerTrust,
            configuration.WorkerBuildDigest, now).Payload;
        if (proof.OwnerId != configuration.Required("OwnerId") || proof.LibraryId != configuration.Required("LibraryId") ||
            Value(item, "pk") != key["pk"].S || Value(item, "sk") != key["sk"].S ||
            key["pk"].S != "POINT#" + proof.PointId.ToString("D") || key["sk"].S != "HOLD_RELEASE#" + proof.DefinitionSha256 ||
            Value(item, "payloadSha256") != envelope.PayloadSha256 || Value(item, "checkpointSha256") != proof.CheckpointSha256 ||
            Value(item, "ownerId") != proof.OwnerId || Value(item, "libraryId") != proof.LibraryId ||
            Value(item, "pointId") != proof.PointId.ToString("D") || Value(item, "captureId") != proof.CaptureId.ToString("D") ||
            Value(item, "workerBuildDigest") != proof.WorkerBuildDigest ||
            Value(item, "verifiedAtUtc") != proof.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            throw new RecoveryWorkerException("hold_release_record_invalid");
        return envelope;
    }
    private static void RequireReplay(RecoveryHoldReleaseProofV1 proof, VerifiedCopiedPoint point,
        RecoveryHoldReleaseEnvelopeV1 approval)
    {
        if (proof.PointId != point.Pointer.PointId || proof.CaptureId != point.Pointer.CaptureId ||
            proof.CheckpointSha256 != point.Pointer.CheckpointSha256 || proof.OwnerApproval != approval)
            throw new RecoveryWorkerException("hold_release_replay_mismatch");
    }
    private static Dictionary<string, AttributeValue> Key(Guid pointId, string definitionSha256) => new()
    { ["pk"] = S("POINT#" + pointId.ToString("D")), ["sk"] = S("HOLD_RELEASE#" + definitionSha256) };
    private static AttributeValue S(string value) => new() { S = value };
    private static string Value(Dictionary<string, AttributeValue> item, string key) => item.TryGetValue(key, out var value) ? value.S ?? "" : "";
}
