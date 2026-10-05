using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record LogicalWriteAdmission(string ScopeId, string RecordId, Guid IntentId, string State,
    string? DestinationVersionId, DateTimeOffset AdmittedAtUtc);

/// <summary>Persists one recovery writer intent per protected logical reference. The writer
/// can read/check fences but has no operation that creates or changes a fence.</summary>
internal sealed class DynamoFenceIntentStore(IAmazonDynamoDB dynamo, string fenceTableName, string intentTableName)
{
    public async Task<LogicalWriteAdmission> AdmitMediaAsync(
        Guid pointId,
        RecoveryObjectReference source,
        string sourceBucket,
        string destinationBucket,
        string destinationKey,
        RecoveryLogicalReference logicalReference,
        Guid physicalWriteId,
        string writerId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken)
    {
        logicalReference.Validate();
        if (source.DataClass is not (RecoveryDataClass.OriginalImage or RecoveryDataClass.XmpSidecar))
            throw new RecoveryWorkerException("media_intent_class_invalid");
        var category = source.DataClass == RecoveryDataClass.OriginalImage ? "original" : "xmp";
        var scopeId = RecoveryFenceScope.MediaScopeId(logicalReference, category, source.Sha256);
        return await AdmitAsync(scopeId, pointId, source, sourceBucket, destinationBucket, destinationKey,
            logicalReference, category, physicalWriteId, writerId, admittedAtUtc, checkFence: true, cancellationToken);
    }

    public async Task<LogicalWriteAdmission> AdmitComponentAsync(
        Guid pointId,
        RecoveryObjectReference source,
        string sourceBucket,
        string destinationBucket,
        string destinationKey,
        string writerId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken)
    {
        var logicalReference = new RecoveryLogicalReference(source.OwnerId, source.LibraryId);
        var isDatabaseSnapshot = source.DataClass == RecoveryDataClass.DatabaseSnapshot;
        var scopeId = isDatabaseSnapshot
            ? RecoveryFenceScope.SnapshotScopeId(source.OwnerId, source.LibraryId)
            : "COMPONENT#" + RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(
                source.OwnerId + "\0" + source.LibraryId + "\0" + source.SourceKey + "\0" + source.SourceVersionId));
        var physicalWriteId = StableGuid(pointId.ToString("D") + "\0" + destinationKey + "\0" + source.Sha256);
        return await AdmitAsync(scopeId, pointId, source, sourceBucket, destinationBucket, destinationKey,
            logicalReference, isDatabaseSnapshot ? "database-snapshot" : "component", physicalWriteId, writerId,
            admittedAtUtc, checkFence: isDatabaseSnapshot, cancellationToken);
    }

    public Task SetStoragePendingAsync(LogicalWriteAdmission admission, CancellationToken cancellationToken) =>
        SetStateAsync(admission, "storage_pending", null, null, cancellationToken);

    public Task SetUnknownAsync(LogicalWriteAdmission admission, CancellationToken cancellationToken) =>
        SetStateAsync(admission, "unknown", null, null, cancellationToken);

    public Task SetSettledAsync(LogicalWriteAdmission admission, string destinationVersionId, string providerReceipt,
        CancellationToken cancellationToken) =>
        SetStateAsync(admission, "settled", destinationVersionId, providerReceipt, cancellationToken);

    private async Task<LogicalWriteAdmission> AdmitAsync(
        string scopeId,
        Guid pointId,
        RecoveryObjectReference source,
        string sourceBucket,
        string destinationBucket,
        string destinationKey,
        RecoveryLogicalReference logicalReference,
        string category,
        Guid physicalWriteId,
        string writerId,
        DateTimeOffset admittedAtUtc,
        bool checkFence,
        CancellationToken cancellationToken)
    {
        var logicalIdentity = scopeId + "\0" + pointId.ToString("D") + "\0" + destinationKey;
        var intentId = StableGuid(logicalIdentity);
        var recordId = $"INTENT#{pointId:D}#{intentId:D}";
        if (checkFence)
        {
            var fence = await dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = fenceTableName,
                Key = Key(scopeId, "FENCE"),
                ConsistentRead = true
            }, cancellationToken);
            if (fence.Item is { Count: > 0 }) throw new RecoveryWorkerException("purge_fence_present");
        }

        var item = BuildItem(scopeId, recordId, pointId, intentId, physicalWriteId, source, sourceBucket,
            destinationBucket, destinationKey, logicalReference, category, writerId, admittedAtUtc);
        var operations = new List<TransactWriteItem>();
        if (checkFence)
        {
            operations.Add(new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = fenceTableName,
                    Key = Key(scopeId, "FENCE"),
                    ConditionExpression = "attribute_not_exists(recordId)"
                }
            });
        }
        operations.Add(new TransactWriteItem
        {
            Put = new Put
            {
                TableName = intentTableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(recordId)"
            }
        });
        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = operations }, cancellationToken);
            return new(scopeId, recordId, intentId, "admitted", null, admittedAtUtc);
        }
        catch (TransactionCanceledException)
        {
            if (checkFence)
            {
                var fence = await dynamo.GetItemAsync(new GetItemRequest
                {
                    TableName = fenceTableName,
                    Key = Key(scopeId, "FENCE"),
                    ConsistentRead = true
                }, cancellationToken);
                if (fence.Item is { Count: > 0 }) throw new RecoveryWorkerException("purge_fence_present");
            }
            var existing = await dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = intentTableName,
                Key = Key(scopeId, recordId),
                ConsistentRead = true
            }, cancellationToken);
            if (existing.Item is not { Count: > 0 } existingItem || !Matches(existingItem, item))
                throw new RecoveryWorkerException("write_intent_conflict");
            var state = GetString(existingItem, "state");
            var version = OptionalString(existingItem, "destinationVersionId");
            if (!DateTimeOffset.TryParse(OptionalString(existingItem, "admittedAtUtc"),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind,
                    out var existingAdmission))
                throw new RecoveryWorkerException("write_intent_admission_timestamp_invalid");
            return new(scopeId, recordId, intentId, state, version, existingAdmission.ToUniversalTime());
        }
    }

    private async Task SetStateAsync(
        LogicalWriteAdmission admission,
        string state,
        string? destinationVersionId,
        string? providerReceipt,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string> { ["#state"] = "state" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":state"] = S(state),
            [":admitted"] = S("admitted"),
            [":pending"] = S("storage_pending"),
            [":unknown"] = S("unknown"),
            [":updated"] = S(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        };
        var expression = "SET #state=:state, updatedAtUtc=:updated";
        if (destinationVersionId is not null)
        {
            names["#version"] = "destinationVersionId";
            values[":version"] = S(destinationVersionId);
            expression += ", #version=:version, settledAtUtc=:updated";
        }
        if (providerReceipt is not null)
        {
            names["#receipt"] = "providerReceipt";
            values[":receipt"] = S(providerReceipt);
            expression += ", #receipt=:receipt";
        }
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = intentTableName,
            Key = Key(admission.ScopeId, admission.RecordId),
            UpdateExpression = expression,
            ConditionExpression = "#state IN (:admitted,:pending,:unknown)",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        }, cancellationToken);
    }

    private static Dictionary<string, AttributeValue> BuildItem(
        string scopeId,
        string recordId,
        Guid pointId,
        Guid intentId,
        Guid physicalWriteId,
        RecoveryObjectReference source,
        string sourceBucket,
        string destinationBucket,
        string destinationKey,
        RecoveryLogicalReference logicalReference,
        string category,
        string writerId,
        DateTimeOffset admittedAtUtc) => new()
        {
            ["scopeId"] = S(scopeId),
            ["recordId"] = S(recordId),
            ["pointId"] = S(pointId.ToString("D")),
            ["intentId"] = S(intentId.ToString("D")),
            ["physicalWriteId"] = S(physicalWriteId.ToString("D")),
            ["ownerId"] = S(logicalReference.OwnerId),
            ["libraryId"] = S(logicalReference.LibraryId),
            ["assetId"] = logicalReference.AssetId is { } assetId ? S(assetId.ToString("D")) : S(""),
            ["editId"] = logicalReference.EditId is { } editId ? S(editId.ToString("D")) : S(""),
            ["category"] = S(category),
            ["dataClass"] = S(source.DataClass.ToString()),
            ["sourceBucket"] = S(sourceBucket),
            ["sourceKey"] = S(source.SourceKey),
            ["sourceVersionId"] = S(source.SourceVersionId),
            ["sha256"] = S(source.Sha256),
            ["byteLength"] = N(source.ByteLength),
            ["destinationBucket"] = S(destinationBucket),
            ["destinationKey"] = S(destinationKey),
            ["writerId"] = S(writerId),
            ["state"] = S("admitted"),
            ["admittedAtUtc"] = S(admittedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        };

    private static bool Matches(Dictionary<string, AttributeValue> existing, Dictionary<string, AttributeValue> expected) =>
        new[] { "scopeId", "recordId", "pointId", "intentId", "physicalWriteId", "ownerId", "libraryId", "assetId", "editId", "category", "dataClass", "sourceBucket", "sourceKey", "sourceVersionId", "sha256", "byteLength", "destinationBucket", "destinationKey" }
        .All(key => existing.TryGetValue(key, out var value) && expected.TryGetValue(key, out var wanted) &&
            (value.S == wanted.S && value.N == wanted.N));

    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static Dictionary<string, AttributeValue> Key(string scopeId, string recordId) => new() { ["scopeId"] = S(scopeId), ["recordId"] = S(recordId) };
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static string GetString(Dictionary<string, AttributeValue> item, string key) => item.TryGetValue(key, out var value) ? value.S ?? "" : "";
    private static string? OptionalString(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value.S) ? value.S : null;
}
