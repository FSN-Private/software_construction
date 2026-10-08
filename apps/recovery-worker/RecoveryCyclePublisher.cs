using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCyclePublisher
{
    private static readonly string[] SafeCodes =
    ["verification_inaccessible", "verification_integrity_failed", "verification_incomplete",
        "publication_failed", "owner_access_pending", "worker_cancelled"];

    internal static string FailureCode(string stage, Exception exception) => stage == "publication"
        ? "publication_failed" : exception switch
        {
            OperationCanceledException => "worker_cancelled",
            InvalidDataException => "verification_integrity_failed",
            RecoveryWorkerException => "verification_incomplete",
            _ => "verification_inaccessible"
        };

    // CYCLE records use an immutable outbox identity. The signed optional point ID
    // remains separate, so a pre-discovery failure never fabricates a recovery point.
    internal static Dictionary<string, AttributeValue> Key(string owner, string library, Guid? pointId)
    {
        var id = pointId ?? new Guid(Convert.FromHexString(RecoveryVerifierRunner.ScopeHash(owner, library))[..16]);
        return new() { ["pk"] = S("POINT#" + id.ToString("D")), ["sk"] = S("CYCLE") };
    }

    internal static async Task ReplayPendingAsync(IAmazonDynamoDB dynamo, IAmazonSQS sqs,
        RecoveryWorkerConfiguration configuration, IRecoveryEvidenceSigner signer, RecoverySigningTrust trust,
        CancellationToken cancellationToken)
    {
        var owner = configuration.Required("OwnerId");
        var library = configuration.Required("LibraryId");
        var table = configuration.Required("VerificationTableName");
        var pending = await ReadAsync(dynamo, table, PendingKey(owner, library), cancellationToken);
        if (pending is null || string.IsNullOrEmpty(Get(pending, "pointId"))) return;
        if (Get(pending, "ownerId") != owner || Get(pending, "libraryId") != library ||
            !Guid.TryParseExact(Get(pending, "pointId"), "D", out var id) || id == Guid.Empty)
            throw new RecoveryWorkerException("cycle_pending_pointer_invalid");
        var item = await ReadAsync(dynamo, table, Key(owner, library, id), cancellationToken)
            ?? throw new RecoveryWorkerException("cycle_pending_outbox_missing");
        if (Get(item, "payloadSha256") != Get(pending, "payloadSha256"))
            throw new RecoveryWorkerException("cycle_pending_pointer_invalid");
        VerifiedRecoveryCycle cycle;
        try
        {
            var encoded = Get(item, "envelopeBytes");
            if (encoded.Length > 90 * 1024) throw new InvalidDataException();
            cycle = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(Convert.FromBase64String(encoded)), trust);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { throw new RecoveryWorkerException("cycle_outbox_binding_invalid", exception); }
        if (cycle.Payload.EventId != id)
            throw new RecoveryWorkerException("cycle_pending_pointer_invalid");
        await PublishAsync(dynamo, sqs, configuration, signer, trust, cycle.Payload.RecoveryPointId,
            cycle.Payload.CaptureId, cycle.Payload.Stage, cycle.Payload.Outcome, cycle.Payload.SafeCode,
            cancellationToken, replayOnly: true, cycleId: id);
    }

    internal static async Task PublishAsync(IAmazonDynamoDB dynamo, IAmazonSQS sqs,
        RecoveryWorkerConfiguration configuration, IRecoveryEvidenceSigner signer, RecoverySigningTrust trust,
        Guid? pointId, Guid? captureId, string stage, string outcome, string safeCode,
        CancellationToken cancellationToken, bool replayOnly = false, Guid? cycleId = null)
    {
        if (stage is not ("verification" or "publication") || outcome is not ("incomplete" or "failed") ||
            !SafeCodes.Contains(safeCode, StringComparer.Ordinal) || pointId == Guid.Empty || captureId == Guid.Empty || cycleId == Guid.Empty)
            throw new RecoveryWorkerException("cycle_publication_input_invalid");
        if (signer.KeyId != trust.KeyId || signer.PublicKeySha256 != trust.PublicKeySha256)
            throw new RecoveryWorkerException("verification_signer_mismatch");
        var queueUrl = RecoveryVerificationPublisher.ValidateDestination(configuration);
        var table = configuration.Required("VerificationTableName");
        var owner = configuration.Required("OwnerId");
        var library = configuration.Required("LibraryId");
        var key = Key(owner, library, cycleId ?? pointId);
        if (!replayOnly)
            await ReplayPendingAsync(dynamo, sqs, configuration, signer, trust, cancellationToken);
        var item = await ReadAsync(dynamo, table, key, cancellationToken);
        if (item is null && replayOnly) return;
        if (item is null)
        {
            var payload = new RecoveryCycleEventV1(1, Guid.Parse(key["pk"].S[6..]), captureId, pointId, owner, library,
                DateTimeOffset.UtcNow, stage, "recovery_records", outcome, safeCode,
                configuration.RequiredHash("RecoveryWorkerBuildDigest"));
            var signature = await signer.SignDigestAsync(SHA256.HashData(RecoveryCycleCodec.SerializePayload(payload)), cancellationToken);
            var envelope = RecoveryCycleCodec.SerializeEnvelope(RecoveryCycleCodec.CreateEnvelope(payload,
                configuration.WorkerId, new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256, signature)));
            if (envelope.Length > 64 * 1024) throw new RecoveryWorkerException("cycle_outbox_size_limit");
            item = new(key)
            {
                ["ownerId"] = S(owner),
                ["libraryId"] = S(library),
                ["payloadSha256"] = S(RecoveryEvidenceCodec.Hash(envelope)),
                ["envelopeBytes"] = S(Convert.ToBase64String(envelope)),
                ["state"] = S("pending"),
                ["verifiedAtUtc"] = S(payload.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ["safeStatus"] = S(outcome),
                ["workerBuildDigest"] = S(payload.RecoveryWorkerBuildDigest)
            };
            try
            {
                await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new() { Put = new Put { TableName = table, Item = item, ConditionExpression = "attribute_not_exists(pk)" } },
                        new() { Update = new Update
                        {
                            TableName = table, Key = PendingKey(owner, library),
                            UpdateExpression = "SET ownerId=:owner, libraryId=:library, pointId=:point, payloadSha256=:sha",
                            ConditionExpression = "attribute_not_exists(pointId) OR (pointId=:point AND payloadSha256=:sha)",
                            ExpressionAttributeValues = new()
                            {
                                [":owner"] = S(owner), [":library"] = S(library),
                                [":point"] = S(key["pk"].S[6..]), [":sha"] = item["payloadSha256"]
                            }
                        } }
                    ]
                }, cancellationToken);
            }
            catch (TransactionCanceledException)
            {
                item = await ReadAsync(dynamo, table, key, cancellationToken)
                    ?? throw new RecoveryWorkerException("cycle_outbox_missing_after_conflict");
            }
        }
        var body = ReadBody(item, trust, owner, library, pointId, captureId);
        if (Get(item, "state") == "delivered")
        {
            await ClearPendingAsync(dynamo, table, owner, library, key, Get(item, "payloadSha256"), cancellationToken);
            return;
        }
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = body
        }, cancellationToken);
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET #state=:delivered, deliveredAtUtc=:now",
                ConditionExpression = "#state=:pending AND payloadSha256=:sha",
                ExpressionAttributeNames = new() { ["#state"] = "state" },
                ExpressionAttributeValues = new()
                {
                    [":pending"] = S("pending"),
                    [":delivered"] = S("delivered"),
                    [":sha"] = S(Get(item, "payloadSha256")),
                    [":now"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            var current = await ReadAsync(dynamo, table, key, cancellationToken)
                ?? throw new RecoveryWorkerException("cycle_outbox_delivery_conflict");
            if (ReadBody(current, trust, owner, library, pointId, captureId) != body || Get(current, "state") != "delivered")
                throw new RecoveryWorkerException("cycle_outbox_delivery_conflict");
        }
        await ClearPendingAsync(dynamo, table, owner, library, key, Get(item, "payloadSha256"), cancellationToken);
    }

    private static Dictionary<string, AttributeValue> PendingKey(string owner, string library) => new()
    {
        ["pk"] = S(RecoveryOwnerPointIndex.PartitionKey(owner, library)), ["sk"] = S("CYCLE_PENDING")
    };

    private static async Task ClearPendingAsync(IAmazonDynamoDB dynamo, string table, string owner, string library,
        Dictionary<string, AttributeValue> key, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table, Key = PendingKey(owner, library),
                UpdateExpression = "REMOVE pointId, payloadSha256",
                ConditionExpression = "ownerId=:owner AND libraryId=:library AND pointId=:point AND payloadSha256=:sha",
                ExpressionAttributeValues = new()
                {
                    [":owner"] = S(owner), [":library"] = S(library),
                    [":point"] = S(key["pk"].S[6..]), [":sha"] = S(sha256)
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException) { /* A concurrent drainer cleared it or advanced to a different immutable outbox. */ }
    }

    private static string ReadBody(IReadOnlyDictionary<string, AttributeValue> item, RecoverySigningTrust trust,
        string owner, string library, Guid? pointId, Guid? captureId)
    {
        try
        {
            var encoded = Get(item, "envelopeBytes");
            if (encoded.Length > 90 * 1024) throw new InvalidDataException();
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > 64 * 1024 || RecoveryEvidenceCodec.Hash(bytes) != Get(item, "payloadSha256"))
                throw new InvalidDataException();
            var payload = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(bytes), trust).Payload;
            if (payload.OwnerId != owner || payload.LibraryId != library || payload.RecoveryPointId != pointId ||
                Get(item, "pk") != "POINT#" + payload.EventId.ToString("D") || Get(item, "sk") != "CYCLE" ||
                payload.CaptureId != captureId || payload.ObservedAtUtc > DateTimeOffset.UtcNow ||
                payload.Stage is not ("verification" or "publication") || payload.Outcome is not ("incomplete" or "failed") ||
                payload.FailedDataClass != "recovery_records" || !SafeCodes.Contains(payload.SafeCode, StringComparer.Ordinal) ||
                Get(item, "ownerId") != owner || Get(item, "libraryId") != library ||
                Get(item, "state") is not ("pending" or "delivered")) throw new InvalidDataException();
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { throw new RecoveryWorkerException("cycle_outbox_binding_invalid", exception); }
    }

    private static async Task<Dictionary<string, AttributeValue>?> ReadAsync(IAmazonDynamoDB dynamo,
        string table, Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var result = await dynamo.GetItemAsync(new GetItemRequest
        { TableName = table, Key = key, ConsistentRead = true }, cancellationToken);
        return result.Item is { Count: > 0 } ? result.Item : null;
    }

    private static string Get(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? "" : "";
    private static AttributeValue S(string value) => new() { S = value };
}
