using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCaptureCompletionReceipt
{
    private const int MaximumBoundScopes = 128;
    private const int MaximumBindingBytes = 300 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static CaptureMarkerSnapshot Parse(string bucket, string key, string versionId, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(versionId) || versionId == "null" ||
            bytes.Length is <= 0 or > 1024 * 1024)
            throw new RecoveryWorkerException("capture_complete_marker_invalid");
        CaptureCompleteMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<CaptureCompleteMarker>(bytes, Json)
                ?? throw new RecoveryWorkerException("capture_complete_marker_invalid");
        }
        catch (JsonException exception)
        {
            throw new RecoveryWorkerException("capture_complete_marker_invalid", exception);
        }
        if (marker.SchemaVersion != 1 || marker.CaptureId == Guid.Empty || marker.ManifestObjects is null ||
            marker.ManifestObjects.Count == 0 || marker.ScopeCount != marker.ManifestObjects.Count)
            throw new RecoveryWorkerException("capture_complete_marker_invalid");
        var capturePrefix = $"captures/{marker.CaptureId:D}/";
        if (!key.StartsWith(capturePrefix, StringComparison.Ordinal) ||
            marker.ManifestObjects.Any(item => item is null ||
                !item.Key.StartsWith(capturePrefix + "scopes/", StringComparison.Ordinal) ||
                !item.Key.EndsWith("/manifest.json", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.VersionId) || item.VersionId == "null" ||
                !IsHash(item.Sha256) || item.ByteLength <= 0) ||
            marker.ManifestObjects.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != marker.ManifestObjects.Count)
            throw new RecoveryWorkerException("capture_marker_scope_invalid");
        return new(bucket, key, versionId, RecoveryEvidenceCodec.Hash(bytes), marker);
    }

    internal static async Task<bool> IsAlreadyProcessedAsync(IAmazonDynamoDB dynamo, string controlTable,
        CaptureMarkerSnapshot marker, CancellationToken cancellationToken)
    {
        if (!CanPersist(marker.Marker)) return false;
        var row = await GetAsync(dynamo, controlTable, Key(marker.Marker.CaptureId), cancellationToken);
        if (row is null) return false;
        if (String(row, "state") != "complete" || String(row, "markerBucket") != marker.Bucket ||
            String(row, "markerKey") != marker.Key || String(row, "markerVersionId") != marker.VersionId ||
            String(row, "markerSha256") != marker.Sha256 ||
            Number(row, "manifestPointerCount") != marker.Marker.ManifestObjects.Count ||
            String(row, "manifestPointerSetSha256") != PointerSetSha256(marker.Marker.ManifestObjects))
            return false;
        var json = String(row, "scopeBindingsJson");
        if (string.IsNullOrEmpty(json)) throw new RecoveryWorkerException("capture_copy_receipt_invalid");
        CaptureScopeBinding[] bindings;
        try
        {
            var bytes = Convert.FromBase64String(json);
            if (bytes.Length > MaximumBindingBytes || RecoveryEvidenceCodec.Hash(bytes) != String(row, "scopeBindingsSha256"))
                throw new RecoveryWorkerException("capture_copy_receipt_invalid");
            bindings = JsonSerializer.Deserialize<CaptureScopeBinding[]>(bytes, Json)
                ?? throw new RecoveryWorkerException("capture_copy_receipt_invalid");
            if (!RecoveryEvidenceCodec.SerializeCanonical(bindings).AsSpan().SequenceEqual(bytes))
                throw new RecoveryWorkerException("capture_copy_receipt_invalid");
        }
        catch (FormatException exception)
        {
            throw new RecoveryWorkerException("capture_copy_receipt_invalid", exception);
        }
        catch (JsonException exception)
        {
            throw new RecoveryWorkerException("capture_copy_receipt_invalid", exception);
        }
        ValidateBindings(marker.Marker, bindings);
        return true;
    }

    internal static async Task<bool> WriteProcessedAsync(IAmazonDynamoDB dynamo, string controlTable,
        CaptureMarkerSnapshot marker, IReadOnlyList<CaptureScopeBinding> bindings,
        CancellationToken cancellationToken)
    {
        if (!CanPersist(marker.Marker)) return false;
        ValidateBindings(marker.Marker, bindings);
        var bindingBytes = RecoveryEvidenceCodec.SerializeCanonical(bindings.OrderBy(item => item.SourceManifestKey,
            StringComparer.Ordinal).ToArray());
        if (bindingBytes.Length > MaximumBindingBytes) return false;
        var bindingSha = RecoveryEvidenceCodec.Hash(bindingBytes);
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = controlTable,
                Key = Key(marker.Marker.CaptureId),
                UpdateExpression = "SET #state=:complete, markerBucket=:bucket, markerKey=:key, markerVersionId=:version, markerSha256=:sha, manifestPointerCount=:count, manifestPointerSetSha256=:pointerSet, scopeBindingsJson=:bindings, scopeBindingsSha256=:bindingSha",
                ConditionExpression = "attribute_not_exists(pk) OR (#state=:complete AND markerBucket=:bucket AND markerKey=:key AND markerVersionId=:version AND markerSha256=:sha AND manifestPointerSetSha256=:pointerSet AND scopeBindingsSha256=:bindingSha)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":complete"] = S("complete"),
                    [":bucket"] = S(marker.Bucket),
                    [":key"] = S(marker.Key),
                    [":version"] = S(marker.VersionId),
                    [":sha"] = S(marker.Sha256),
                    [":count"] = N(marker.Marker.ManifestObjects.Count),
                    [":pointerSet"] = S(PointerSetSha256(marker.Marker.ManifestObjects)),
                    [":bindings"] = S(Convert.ToBase64String(bindingBytes)),
                    [":bindingSha"] = S(bindingSha)
                }
            }, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // DynamoDB can commit the update while the response is lost. A strong read
            // makes that retry idempotent without replacing a different marker receipt.
            try
            {
                if (await IsAlreadyProcessedAsync(dynamo, controlTable, marker, cancellationToken)) return true;
            }
            catch (RecoveryWorkerException) when (exception is ConditionalCheckFailedException) { }
            if (exception is ConditionalCheckFailedException)
                throw new RecoveryWorkerException("capture_copy_receipt_conflict", exception);
            throw;
        }
    }

    internal static string BindingSetSha256(IReadOnlyList<CaptureScopeBinding> bindings) =>
        RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(bindings.OrderBy(item =>
            item.SourceManifestKey, StringComparer.Ordinal).ToArray()));

    private static bool CanPersist(CaptureCompleteMarker marker) =>
        marker.ManifestObjects.Count <= MaximumBoundScopes;

    private static void ValidateBindings(CaptureCompleteMarker marker, IReadOnlyList<CaptureScopeBinding> bindings)
    {
        if (bindings.Count != marker.ManifestObjects.Count || bindings.Count > MaximumBoundScopes ||
            bindings.Any(item => item is null || item.CaptureId != marker.CaptureId || item.PointId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.OwnerId) || string.IsNullOrWhiteSpace(item.LibraryId) ||
                string.IsNullOrWhiteSpace(item.SourceManifestVersionId) || !IsHash(item.SourceManifestSha256) ||
                item.SourceManifestByteLength <= 0 || !IsHash(item.CheckpointSha256) ||
                item.State is not ("copied" or "access_incomplete") || item.ChangeSequence < 0 ||
                item.SnapshotCutoffUtc.Offset != TimeSpan.Zero) ||
            bindings.Select(item => item.PointId).Distinct().Count() != bindings.Count ||
            bindings.Select(item => item.SourceManifestKey).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw new RecoveryWorkerException("capture_copy_receipt_bindings_invalid");
        var scopePartitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            try
            {
                _ = RecoveryOwnerPointIndex.ScopeHash(binding.OwnerId, binding.LibraryId);
                if (!scopePartitions.Add(RecoveryOwnerPointIndex.PartitionKey(binding.OwnerId, binding.LibraryId)))
                    throw new RecoveryWorkerException("capture_copy_receipt_bindings_invalid");
            }
            catch (RecoveryWorkerException) { throw; }
            catch (InvalidDataException exception)
            { throw new RecoveryWorkerException("capture_copy_receipt_bindings_invalid", exception); }
        }

        var actual = bindings.Select(item => new CaptureManifestPointer(item.SourceManifestKey,
            item.SourceManifestVersionId, item.SourceManifestSha256, item.SourceManifestByteLength))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var expected = marker.ManifestObjects.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new RecoveryWorkerException("capture_copy_receipt_pointer_mismatch");
    }

    private static string PointerSetSha256(IReadOnlyList<CaptureManifestPointer> pointers) =>
        RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(pointers.OrderBy(item => item.Key,
            StringComparer.Ordinal).ToArray()));

    private static async Task<Dictionary<string, AttributeValue>?> GetAsync(IAmazonDynamoDB dynamo,
        string table, Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private static Dictionary<string, AttributeValue> Key(Guid captureId) => new()
    {
        ["pk"] = S("CAPTURE#" + captureId.ToString("D").ToLowerInvariant()),
        ["sk"] = S("COPY")
    };

    private static string String(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;

    private static int Number(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && int.TryParse(value.N, NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) ? number : -1;

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}

internal sealed record CaptureManifestPointer(string Key, string VersionId, string Sha256, long ByteLength);
internal sealed record CaptureCompleteMarker(int SchemaVersion, Guid CaptureId, DateTimeOffset CompletedAtUtc,
    int ScopeCount, IReadOnlyList<CaptureManifestPointer> ManifestObjects);
internal sealed record CaptureMarkerSnapshot(string Bucket, string Key, string VersionId, string Sha256,
    CaptureCompleteMarker Marker);
internal sealed record CaptureScopeBinding(Guid CaptureId, Guid PointId, string OwnerId, string LibraryId,
    string SourceManifestKey, string SourceManifestVersionId, string SourceManifestSha256,
    long SourceManifestByteLength, DateTimeOffset SnapshotCutoffUtc, long ChangeSequence,
    string CheckpointSha256, string State);
