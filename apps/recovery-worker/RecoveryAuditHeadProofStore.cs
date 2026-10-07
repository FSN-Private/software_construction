using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryAuditHeadProofStore
{
    internal static async Task PersistAsync(IAmazonDynamoDB dynamo, RecoveryWorkerConfiguration configuration,
        AwsKmsEvidenceSigner signer, VerifiedCopiedPoint point, RecoveryAuditHeadV1 head,
        RecoveryAuditHeadRequirementV1 requirement, VerifiedRecoveryAuditGenesis genesis,
        CancellationToken cancellationToken) => await PersistAsync(dynamo, configuration, signer, signer.Trust,
        point, head, requirement, genesis, cancellationToken);

    internal static async Task PersistAsync(IAmazonDynamoDB dynamo, RecoveryWorkerConfiguration configuration,
        IRecoveryEvidenceSigner signer, RecoverySigningTrust trust, VerifiedCopiedPoint point,
        RecoveryAuditHeadV1 head, RecoveryAuditHeadRequirementV1 requirement,
        VerifiedRecoveryAuditGenesis genesis, CancellationToken cancellationToken)
    {
        if (signer.KeyId != trust.KeyId || signer.PublicKeySha256 != trust.PublicKeySha256)
            throw new RecoveryWorkerException("audit_head_proof_signer_mismatch");
        var table = configuration.Required("VerificationTableName");
        var fullManifestSha = point.FinalManifest is null
            ? point.SourceManifest.Digest()
            : point.Pointer.ManifestSha256;
        var state = point.FinalManifest is null ? "access_incomplete" : "copied";
        var existing = await ReadAsync(dynamo, table, point.Pointer.PointId, trust, cancellationToken);
        if (existing is not null)
        {
            ValidateStableHead(existing.Payload, point, head, requirement.Minimum, genesis,
                configuration.WorkerBuildDigest);
            if (state == "access_incomplete" && StringState(existing) == "copied")
                throw new RecoveryWorkerException("audit_head_proof_downgrade_rejected");
            if (existing.Payload.FullManifestSha256 == fullManifestSha)
            {
                if (StringState(existing) != state)
                    throw new RecoveryWorkerException("audit_head_proof_downgrade_rejected");
                return;
            }

            if (StringState(existing) != "access_incomplete" || state != "copied" ||
                existing.Payload.FullManifestSha256 != point.SourceManifest.Digest() ||
                existing.Payload.PreviouslyVerifiedHead != requirement.PreviouslyVerified)
                throw new RecoveryWorkerException("audit_head_proof_conflict");

            await UpgradeIncompleteProofAsync(dynamo, table, signer, trust, point, head, existing,
                fullManifestSha, genesis, configuration, cancellationToken);
            return;
        }

        var payload = new RecoveryAuditHeadProofPayloadV1(1, point.Pointer.PointId, point.Pointer.CaptureId,
            point.Pointer.OwnerId, point.Pointer.LibraryId, point.Pointer.SourceManifestSha256,
            fullManifestSha, point.SourceManifest.SnapshotCutoffUtc, point.SourceManifest.SnapshotLsn,
            head, requirement.Minimum, requirement.PreviouslyVerified, genesis.ArtifactSha256,
            DateTimeOffset.UtcNow, configuration.WorkerBuildDigest);
        var payloadBytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofPayload(payload);
        var signature = await signer.SignDigestAsync(SHA256.HashData(payloadBytes), cancellationToken);
        var envelope = RecoveryAuditLedgerCodec.CreateAuditHeadProofEnvelope(payload, configuration.WorkerId,
            new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256, signature));
        var envelopeBytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofEnvelope(envelope);
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = S(PointPartition(point.Pointer.PointId)),
            ["sk"] = S("AUDIT_VERIFY"),
            ["pointId"] = S(point.Pointer.PointId.ToString("D").ToLowerInvariant()),
            ["captureId"] = S(point.Pointer.CaptureId.ToString("D").ToLowerInvariant()),
            ["ownerId"] = S(point.Pointer.OwnerId),
            ["libraryId"] = S(point.Pointer.LibraryId),
            ["sourceManifestSha256"] = S(point.Pointer.SourceManifestSha256),
            ["fullManifestSha256"] = S(fullManifestSha),
            ["proofState"] = S(state),
            ["payloadSha256"] = S(envelope.PayloadSha256),
            ["envelopeBytes"] = S(Encoding.UTF8.GetString(envelopeBytes)),
            ["verifiedAtUtc"] = S(payload.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ["workerBuildDigest"] = S(configuration.WorkerBuildDigest)
        };
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk)"
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            existing = await ReadAsync(dynamo, table,
                point.Pointer.PointId, trust, cancellationToken);
            if (existing is null) throw new RecoveryWorkerException("audit_head_proof_conflict");
            ValidateStableHead(existing.Payload, point, head, requirement.Minimum, genesis,
                configuration.WorkerBuildDigest);
            if (existing.Payload.FullManifestSha256 != fullManifestSha || StringState(existing) != state)
                throw new RecoveryWorkerException("audit_head_proof_conflict");
        }
    }

    private static async Task UpgradeIncompleteProofAsync(IAmazonDynamoDB dynamo, string table,
        IRecoveryEvidenceSigner signer, RecoverySigningTrust trust, VerifiedCopiedPoint point, RecoveryAuditHeadV1 head,
        VerifiedRecoveryAuditHeadProof existing, string fullManifestSha, VerifiedRecoveryAuditGenesis genesis,
        RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var payload = new RecoveryAuditHeadProofPayloadV1(1, point.Pointer.PointId, point.Pointer.CaptureId,
            point.Pointer.OwnerId, point.Pointer.LibraryId, point.Pointer.SourceManifestSha256,
            fullManifestSha, point.SourceManifest.SnapshotCutoffUtc, point.SourceManifest.SnapshotLsn,
            head, existing.Payload.MinimumHead, existing.Payload.PreviouslyVerifiedHead, genesis.ArtifactSha256,
            DateTimeOffset.UtcNow, configuration.WorkerBuildDigest);
        var payloadBytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofPayload(payload);
        var signature = await signer.SignDigestAsync(SHA256.HashData(payloadBytes), cancellationToken);
        var envelope = RecoveryAuditLedgerCodec.CreateAuditHeadProofEnvelope(payload, configuration.WorkerId,
            new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256, signature));
        var envelopeBytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofEnvelope(envelope);
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = S(PointPartition(point.Pointer.PointId)),
                    ["sk"] = S("AUDIT_VERIFY")
                },
                UpdateExpression = "SET fullManifestSha256=:full, proofState=:copied, payloadSha256=:payload, envelopeBytes=:envelope, verifiedAtUtc=:verified, workerBuildDigest=:worker",
                ConditionExpression = "#state=:incomplete AND sourceManifestSha256=:source AND fullManifestSha256=:sourceFull AND payloadSha256=:prior",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "proofState" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":full"] = S(fullManifestSha),
                    [":copied"] = S("copied"),
                    [":payload"] = S(envelope.PayloadSha256),
                    [":envelope"] = S(Encoding.UTF8.GetString(envelopeBytes)),
                    [":verified"] = S(payload.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                    [":worker"] = S(configuration.WorkerBuildDigest),
                    [":incomplete"] = S("access_incomplete"),
                    [":source"] = S(point.Pointer.SourceManifestSha256),
                    [":sourceFull"] = S(point.SourceManifest.Digest()),
                    [":prior"] = S(existing.PayloadSha256)
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            var latest = await ReadAsync(dynamo, table, point.Pointer.PointId, trust, cancellationToken);
            if (latest is null || latest.Payload.FullManifestSha256 != fullManifestSha ||
                StringState(latest) != "copied")
                throw new RecoveryWorkerException("audit_head_proof_upgrade_conflict");
            ValidateStableHead(latest.Payload, point, head, existing.Payload.MinimumHead, genesis,
                configuration.WorkerBuildDigest);
        }
    }

    private static void ValidateStableHead(RecoveryAuditHeadProofPayloadV1 payload, VerifiedCopiedPoint point,
        RecoveryAuditHeadV1 head, RecoveryAuditHeadV1 minimum, VerifiedRecoveryAuditGenesis genesis,
        string workerBuildDigest)
    {
        if (payload.RecoveryPointId != point.Pointer.PointId || payload.CaptureId != point.Pointer.CaptureId ||
            payload.OwnerId != point.Pointer.OwnerId || payload.LibraryId != point.Pointer.LibraryId ||
            payload.SourceManifestSha256 != point.Pointer.SourceManifestSha256 ||
            payload.SnapshotCutoffUtc != point.SourceManifest.SnapshotCutoffUtc ||
            payload.SnapshotLsn != point.SourceManifest.SnapshotLsn || payload.VerifiedHead != head ||
            payload.MinimumHead != minimum || payload.GenesisArtifactSha256 != genesis.ArtifactSha256 ||
            payload.RecoveryWorkerBuildDigest != workerBuildDigest)
            throw new RecoveryWorkerException("audit_head_proof_conflict");
    }

    private static string? StringState(VerifiedRecoveryAuditHeadProof proof) =>
        proof.Payload.FullManifestSha256 == proof.Payload.SourceManifestSha256 ? "access_incomplete" : "copied";

    internal static async Task<VerifiedRecoveryAuditHeadProof?> ReadAsync(IAmazonDynamoDB dynamo,
        string table, Guid pointId, RecoverySigningTrust trust, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S(PointPartition(pointId)),
                ["sk"] = S("AUDIT_VERIFY")
            },
            ConsistentRead = true
        }, cancellationToken);
        if (response.Item is not { Count: > 0 } item) return null;
        if (!item.TryGetValue("envelopeBytes", out var envelopeValue) || string.IsNullOrEmpty(envelopeValue.S))
            throw new RecoveryWorkerException("audit_head_proof_missing");
        var bytes = Encoding.UTF8.GetBytes(envelopeValue.S);
        RecoveryAuditHeadProofEnvelopeV1 envelope;
        VerifiedRecoveryAuditHeadProof verified;
        try
        {
            envelope = RecoveryAuditLedgerCodec.ParseAuditHeadProofEnvelope(bytes);
            verified = RecoveryAuditLedgerCodec.VerifyAuditHeadProof(envelope, trust);
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or FormatException)
        {
            throw new RecoveryWorkerException("audit_head_proof_invalid", exception);
        }
        var payload = verified.Payload;
        var payloadState = payload.FullManifestSha256 == payload.SourceManifestSha256 ? "access_incomplete" : "copied";
        if (payload.RecoveryPointId != pointId ||
            String(item, "pointId") != pointId.ToString("D").ToLowerInvariant() ||
            String(item, "captureId") != payload.CaptureId.ToString("D").ToLowerInvariant() ||
            String(item, "ownerId") != payload.OwnerId || String(item, "libraryId") != payload.LibraryId ||
            String(item, "sourceManifestSha256") != payload.SourceManifestSha256 ||
            String(item, "fullManifestSha256") != payload.FullManifestSha256 ||
            String(item, "payloadSha256") != verified.PayloadSha256 ||
            String(item, "workerBuildDigest") != payload.RecoveryWorkerBuildDigest ||
            String(item, "proofState") != payloadState ||
            !DateTimeOffset.TryParse(String(item, "verifiedAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var itemVerifiedAt) || itemVerifiedAt.ToUniversalTime() != payload.VerifiedAtUtc)
            throw new RecoveryWorkerException("audit_head_proof_record_binding_invalid");
        return verified;
    }

    internal static RecoveryAuditHeadRequirementV1 ValidateForDrill(
        VerifiedRecoveryAuditHeadProof proof, RecoveryWorkerConfiguration configuration,
        VerifiedCopiedPoint point, VerifiedRecoveryAuditGenesis genesis)
    {
        var payload = proof.Payload;
        var fullManifestSha = point.FinalManifest is null ? point.SourceManifest.Digest() : point.Pointer.ManifestSha256;
        try
        {
            RecoveryAuditLedgerCodec.RequireAuditHeadProofBinding(payload, point.Pointer.PointId,
                point.Pointer.CaptureId, point.Pointer.OwnerId, point.Pointer.LibraryId,
                point.Pointer.SourceManifestSha256, fullManifestSha, point.SourceManifest.SnapshotCutoffUtc,
                point.SourceManifest.SnapshotLsn, genesis.ArtifactSha256, configuration.WorkerBuildDigest);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("audit_head_proof_point_binding_invalid", exception);
        }
        var configuredMinimum = new RecoveryAuditHeadV1(
            long.TryParse(configuration.Required("AuditMinimumSequence"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var sequence) && sequence >= 0
                ? sequence
                : throw new RecoveryWorkerException("audit_minimum_sequence_invalid"),
            configuration.RequiredHash("AuditMinimumEventSha256"));
        if (payload.MinimumHead != configuredMinimum)
            throw new RecoveryWorkerException("audit_head_proof_minimum_mismatch");
        var requirement = new RecoveryAuditHeadRequirementV1(payload.MinimumHead,
            payload.PreviouslyVerifiedHead, payload.VerifiedHead);
        try { requirement.Validate(); }
        catch (InvalidDataException exception) { throw new RecoveryWorkerException("audit_head_proof_requirement_invalid", exception); }
        return requirement;
    }

    private static string PointPartition(Guid pointId) => "POINT#" + pointId.ToString("D").ToLowerInvariant();
    private static string? String(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;
    private static AttributeValue S(string value) => new() { S = value };
}
