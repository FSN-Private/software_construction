using Amazon.DynamoDBv2.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryPointDiscoveryChecks
{
    public static async Task RunAsync()
    {
        const string owner = "discovery-owner";
        const string library = "main";
        var first = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var partition = RecoveryOwnerPointIndex.PartitionKey(owner, library);
        var firstCopy = Copy(first);
        var secondCopy = Copy(second);
        var marker = Item(partition, "INDEX");
        marker["status"] = S("complete");
        marker["indexVersion"] = N(1);
        marker["bootstrapPointCount"] = N(2);
        marker["indexedPointCount"] = N(2);
        marker["bootstrapPointSetSha256"] = S(RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, library, [first, second]));
        var seed = new[] { marker, Member(first), Member(second), firstCopy, secondCopy, Item(partition, "VERIFIED") };
        var client = RecoveryIndexDynamoFake.Create(seed, 1, out var fake);
        var points = await RecoveryVerifierRunner.ReadCopiedPointsAsync(client, "control", owner, library, CancellationToken.None);
        if (points.Count != 2 || fake.ScanCalls != 0 || fake.QueryCalls != 2)
            throw new InvalidOperationException("Discovery must use every strongly consistent membership page without Scan or verification-marker confusion.");

        await Reject(seed.Where(item => item != marker), "scope_index_bootstrap_required");
        await Reject(seed.Where(item => item != firstCopy), "scope_index_point_missing");
        var foreign = Copy(first);
        foreign["ownerId"] = S("foreign-owner");
        await Reject(seed.Select(item => item == firstCopy ? foreign : item), "scope_index_point_binding_mismatch");
        var altered = Copy(first);
        altered["sourceManifestSha256"] = S(new string('b', 64));
        await Reject(seed.Select(item => item == firstCopy ? altered : item), "scope_index_point_binding_mismatch");
        await Reject(seed.Where(item => item["sk"].S != RecoveryOwnerPointIndex.PointSortKey(first)), "scope_index_inventory_mismatch");

        var markerReads = 0;
        var racingClient = RecoveryIndexDynamoFake.Create(seed, 1, out var racingFake);
        racingFake.BeforeGet = request =>
        {
            if (request.Key["pk"].S != partition || request.Key["sk"].S != "INDEX" || ++markerReads != 2) return;
            var changed = racingFake.Read(partition, "INDEX")!;
            changed["indexedPointCount"] = N(3);
            racingFake.Replace(changed);
        };
        await RejectClient(racingClient, "scope_index_changed_during_read");

        Dictionary<string, AttributeValue> Member(Guid point)
        {
            var item = Item(partition, RecoveryOwnerPointIndex.PointSortKey(point));
            item["pointId"] = S(point.ToString("D"));
            item["sourceManifestSha256"] = S(new string('a', 64));
            item["bootstrapMember"] = new() { BOOL = true };
            return item;
        }

        Dictionary<string, AttributeValue> Copy(Guid point)
        {
            var item = Item("POINT#" + point.ToString("D"), "COPY");
            foreach (var (key, value) in new[]
            {
                ("captureId", Guid.NewGuid().ToString("D")), ("ownerId", owner), ("libraryId", library),
                ("sourceAccountId", "111111111111"), ("sourceRegion", "ap-southeast-1"),
                ("recoveryAccountId", "222222222222"), ("recoveryRegion", "ap-southeast-2"),
                ("sourceManifestBucket", "source-capture"), ("sourceManifestKey", "captures/private"),
                ("sourceManifestVersionId", "source-version"), ("sourceManifestSha256", new string('a', 64)),
                ("evidenceBucket", "recovery-evidence"), ("manifestKey", "points/private"),
                ("manifestVersionId", "manifest-version"), ("manifestSha256", new string('c', 64)),
                ("checkpointKey", "points/checkpoint"), ("checkpointVersionId", "checkpoint-version"),
                ("checkpointSha256", new string('d', 64)), ("copiedAtUtc", "2026-10-07T00:00:00Z"), ("state", "copied")
            }) item[key] = S(value);
            item["checkpointByteLength"] = N(100);
            return item;
        }

        Task Reject(IEnumerable<Dictionary<string, AttributeValue>> items, string code) =>
            RejectClient(RecoveryIndexDynamoFake.Create(items, 1, out _), code);

        async Task RejectClient(Amazon.DynamoDBv2.IAmazonDynamoDB source, string code)
        {
            try { _ = await RecoveryVerifierRunner.ReadCopiedPointsAsync(source, "control", owner, library, CancellationToken.None); }
            catch (RecoveryWorkerException exception) when (exception.SafeCode == code) { return; }
            throw new InvalidOperationException("Discovery accepted invalid evidence: " + code);
        }
    }

    private static Dictionary<string, AttributeValue> Item(string pk, string sk) => new() { ["pk"] = S(pk), ["sk"] = S(sk) };
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}
