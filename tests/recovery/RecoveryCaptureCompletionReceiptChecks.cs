using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryCaptureCompletionReceiptChecks
{
    public static async Task RunAsync()
    {
        const string bucket = "primary-capture";
        const string key = "captures/74000000-0000-0000-0000-000000000001/capture-complete.json";
        const string version = "capture-v1";
        var captureId = Guid.Parse("74000000-0000-0000-0000-000000000001");
        var pointId = Guid.Parse("75000000-0000-0000-0000-000000000001");
        var sourceKey = $"captures/{captureId:D}/scopes/{RecoveryVerifierRunner.ScopeHash("receipt-owner", "main")}/manifest.json";
        var pointer = new CaptureManifestPointer(sourceKey, "manifest-v1", new string('a', 64), 1024);
        var markerValue = new CaptureCompleteMarker(1, captureId, DateTimeOffset.UtcNow, 1, [pointer]);
        var markerBytes = RecoveryEvidenceCodec.SerializeCanonical(markerValue);
        var marker = RecoveryCaptureCompletionReceipt.Parse(bucket, key, version, markerBytes);
        var binding = new CaptureScopeBinding(captureId, pointId, "receipt-owner", "main", pointer.Key,
            pointer.VersionId, pointer.Sha256, pointer.ByteLength, DateTimeOffset.UtcNow.AddMinutes(-2), 17,
            new string('b', 64), "copied");

        var store = new ReceiptDynamo();
        Check(!await RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(store, "control", marker,
                CancellationToken.None), "An unknown capture marker is never skipped.");
        Check(await RecoveryCaptureCompletionReceipt.WriteProcessedAsync(store, "control", marker, [binding],
                CancellationToken.None), "A fully bound capture receipt is persisted.");
        Check(await RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(store, "control", marker,
                CancellationToken.None), "An exact S3 version and digest can be skipped after recovery finalization.");

        var changedVersion = RecoveryCaptureCompletionReceipt.Parse(bucket, key, "capture-v2", markerBytes);
        Check(!await RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(store, "control", changedVersion,
                CancellationToken.None), "A different primary object version is processed again, never trusted by timestamp.");
        await ExpectSafeCode(() => RecoveryCaptureCompletionReceipt.WriteProcessedAsync(store, "control",
            changedVersion, [binding], CancellationToken.None), "capture_copy_receipt_conflict");

        var alteredPointer = pointer with { Sha256 = new string('c', 64) };
        var changedMarkerBytes = RecoveryEvidenceCodec.SerializeCanonical(markerValue with
        { ManifestObjects = [alteredPointer] });
        var changedPointer = RecoveryCaptureCompletionReceipt.Parse(bucket, key, version, changedMarkerBytes);
        Check(!await RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(store, "control", changedPointer,
                CancellationToken.None), "A changed manifest pointer set cannot match an earlier receipt.");
        await ExpectSafeCode(() => RecoveryCaptureCompletionReceipt.WriteProcessedAsync(store, "control",
            changedPointer, [binding with { SourceManifestSha256 = alteredPointer.Sha256 }], CancellationToken.None),
            "capture_copy_receipt_conflict");

        var unfinished = binding with { State = "copying" };
        await ExpectSafeCode(() => RecoveryCaptureCompletionReceipt.WriteProcessedAsync(new ReceiptDynamo(), "control",
            marker, [unfinished], CancellationToken.None), "capture_copy_receipt_bindings_invalid");

        var lostResponseStore = new ReceiptDynamo { LoseNextUpdateResponse = true };
        Check(await RecoveryCaptureCompletionReceipt.WriteProcessedAsync(lostResponseStore, "control", marker,
                [binding], CancellationToken.None) &&
              await RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(lostResponseStore, "control", marker,
                CancellationToken.None),
            "A committed receipt with a lost response is recognized idempotently by a strong read.");

        var tamperedStore = new ReceiptDynamo();
        await RecoveryCaptureCompletionReceipt.WriteProcessedAsync(tamperedStore, "control", marker, [binding],
            CancellationToken.None);
        tamperedStore.Corrupt("scopeBindingsSha256", new string('f', 64));
        await ExpectSafeCode(() => RecoveryCaptureCompletionReceipt.IsAlreadyProcessedAsync(tamperedStore,
            "control", marker, CancellationToken.None), "capture_copy_receipt_invalid");

        Check(RecoveryCaptureCompletionReceipt.BindingSetSha256([binding]).Length == 64,
            "The durable receipt includes a stable canonical digest over its repair mapping.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectSafeCode(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == expected) { return; }
        throw new InvalidOperationException("Expected capture receipt failure: " + expected);
    }

    private sealed class ReceiptDynamo : AmazonDynamoDBClient
    {
        private Dictionary<string, AttributeValue>? item;

        public bool LoseNextUpdateResponse { get; set; }

        public ReceiptDynamo() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig
        { ServiceURL = "http://127.0.0.1:1" }) { }

        public void Corrupt(string name, string value)
        {
            if (item is null) throw new InvalidOperationException("receipt_missing");
            item[name] = S(value);
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ConsistentRead != true) throw new InvalidOperationException("capture_receipt_read_not_strong");
            return Task.FromResult(new GetItemResponse
            {
                Item = item is null ? new Dictionary<string, AttributeValue>() : Clone(item)
            });
        }

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            var values = request.ExpressionAttributeValues;
            if (item is not null &&
                (Get(item, "state") != Get(values, ":complete") || Get(item, "markerBucket") != Get(values, ":bucket") ||
                 Get(item, "markerKey") != Get(values, ":key") || Get(item, "markerVersionId") != Get(values, ":version") ||
                 Get(item, "markerSha256") != Get(values, ":sha") ||
                 Get(item, "manifestPointerSetSha256") != Get(values, ":pointerSet") ||
                 Get(item, "scopeBindingsSha256") != Get(values, ":bindingSha")))
                throw new ConditionalCheckFailedException("capture_receipt_conflict");

            var key = request.Key.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);
            item = key;
            var names = new Dictionary<string, string>
            {
                ["#state"] = "state"
            };
            foreach (var (expression, attribute) in new[]
                     {
                         (":complete", "state"), (":bucket", "markerBucket"), (":key", "markerKey"),
                         (":version", "markerVersionId"), (":sha", "markerSha256"),
                         (":pointerSet", "manifestPointerSetSha256"), (":bindingSha", "scopeBindingsSha256"),
                         (":bindings", "scopeBindingsJson"), (":count", "manifestPointerCount")
                     })
                item[attribute] = Clone(values[expression]);
            if (LoseNextUpdateResponse)
            {
                LoseNextUpdateResponse = false;
                throw new AmazonServiceException("simulated_response_lost_after_commit");
            }
            return Task.FromResult(new UpdateItemResponse { Attributes = Clone(item) });
        }

        private static string Get(Dictionary<string, AttributeValue> values, string name) =>
            values.TryGetValue(name, out var value) ? value.S ?? value.N ?? string.Empty : string.Empty;

        private static AttributeValue S(string value) => new() { S = value };

        private static Dictionary<string, AttributeValue> Clone(Dictionary<string, AttributeValue> values) =>
            values.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);

        private static AttributeValue Clone(AttributeValue value) => new()
        { S = value.S, N = value.N, BOOL = value.BOOL };
    }
}
