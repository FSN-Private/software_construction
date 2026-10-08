using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryVerificationPublisherChecks
{
    private static readonly string[] EnvironmentKeys =
    [
        "Environment", "RecoveryWorkerBuildDigest", "WorkerId", "VerificationTableName", "ControlTableName",
        "OwnerId", "LibraryId", "SourceAccountId", "SourceRegion", "DestinationAccountId", "DestinationRegion",
        "ObservationQueueArn", "ObservationQueueUrl", "ApiBuildDigest", "IngestWorkerBuildDigest",
        "RendererBuildDigest", "RecoveryTemplateDigest", "MigrationDigest", "VerifierBuild"
    ];

    public static async Task RunAsync()
    {
        var previous = EnvironmentKeys.ToDictionary(name => name,
            name => Environment.GetEnvironmentVariable("Recovery__" + name), StringComparer.Ordinal);
        try { await RunCoreAsync(); }
        finally
        {
            foreach (var name in EnvironmentKeys)
                Environment.SetEnvironmentVariable("Recovery__" + name, previous[name]);
        }
    }

    private static async Task RunCoreAsync()
    {
        const string sourceAccount = "111111111111";
        const string recoveryAccount = "222222222222";
        const string sourceRegion = "ap-southeast-1";
        const string recoveryRegion = "ap-southeast-2";
        const string queueName = "platform-fixture-recovery-observations";
        Set("Environment", "fixture");
        Set("RecoveryWorkerBuildDigest", new string('f', 64));
        Set("WorkerId", "fixture-verifier");
        Set("VerificationTableName", "recovery-verification-fixture");
        Set("ControlTableName", "recovery-control-fixture");
        Set("OwnerId", "owner-fixture");

        Set("LibraryId", "main");
        Set("SourceAccountId", sourceAccount);
        Set("SourceRegion", sourceRegion);
        Set("DestinationAccountId", recoveryAccount);
        Set("DestinationRegion", recoveryRegion);
        Set("ObservationQueueArn", $"arn:aws:sqs:{sourceRegion}:{sourceAccount}:{queueName}");
        Set("ObservationQueueUrl", $"https://sqs.{sourceRegion}.amazonaws.com/{sourceAccount}/{queueName}");
        Set("ApiBuildDigest", new string('1', 64));
        Set("IngestWorkerBuildDigest", new string('2', 64));
        Set("RendererBuildDigest", new string('3', 64));
        Set("RecoveryTemplateDigest", new string('4', 64));
        Set("MigrationDigest", new string('5', 64));
        Set("VerifierBuild", "fixture-worker");
        var configuration = RecoveryWorkerConfiguration.Load("verify");
        Check(RecoveryCyclePublisher.FailureCode("verification", new InvalidDataException("private/path")) == "verification_integrity_failed" &&
            RecoveryCyclePublisher.FailureCode("verification", new IOException("credential=secret")) == "verification_inaccessible" &&
            RecoveryCyclePublisher.FailureCode("verification", new RecoveryWorkerException("private_filename")) == "verification_incomplete" &&
            RecoveryCyclePublisher.FailureCode("verification", new OperationCanceledException()) == "worker_cancelled" &&
            RecoveryCyclePublisher.FailureCode("publication", new IOException("queue/path")) == "publication_failed",
            "Exception details entered publication codes or failed captures lost their safe classification.");

        using var rsa = RSA.Create(2048);
        var signer = new TestSigner(rsa, "fixture-publisher-key");
        var trust = signer.Trust;
        var point = CopiedPoint(DateTimeOffset.UtcNow);
        var head = new RecoveryAuditHeadV1(3, new string('c', 64));
        var database = FakeDynamo.Create(out var dynamo);
        var queue = FakeSqs.Create(out var sqs);
        sqs.FailOnceAfterAcceptingCall = 2;

        try
        {
            await RecoveryVerificationPublisher.PublishAsync(database, queue, configuration, signer, trust,
                point, head, CancellationToken.None);
            throw new InvalidOperationException("A simulated accepted-but-timed-out SQS send should leave the outbox pending.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "simulated_send_timeout") { }

        var pointKey = PointKey(point.Pointer.PointId);
        var pending = dynamo.Read(pointKey["pk"].S, pointKey["sk"].S)
            ?? throw new InvalidOperationException("The exact signed outbox was not persisted before sending.");
        Check(Attr(pending, "state") == "pending", "An incomplete queue send must remain pending.");
        var expectedBodies = JsonSerializer.Deserialize<List<string>>(Attr(pending, "messageBodies"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The persisted outbox was empty.");
        var originalOutboxSha = Attr(pending, "outboxSha256");
        Check(expectedBodies.Count == RecoveryEvidenceCodec.ObservationClasses.Length + 1,
            "The outbox must contain the signed catalog and seven signed class observations.");

        await RecoveryVerificationPublisher.PublishAsync(database, queue, configuration, signer, trust,
            point, head, CancellationToken.None);
        Check(sqs.Sends.Skip(2).SequenceEqual(expectedBodies, StringComparer.Ordinal),
            "A lost-send retry must reuse the exact canonical signed bodies, including already accepted messages.");
        var delivered = dynamo.Read(pointKey["pk"].S, pointKey["sk"].S)!;
        Check(Attr(delivered, "state") == "delivered" && Attr(delivered, "outboxSha256") == originalOutboxSha &&
              dynamo.TransactionCount == 1 && dynamo.UpdateCount == 1,
            "Successful retry must mark the original immutable outbox delivered without replacing it.");

        var deliveredBody = Attr(delivered, "messageBodies");
        var controlPoint = await RecoveryControlReader.ReadPointAsync(database, configuration, trust,
            point.Pointer.PointId, CancellationToken.None);
        Check(controlPoint?.RecoveryPointId == point.Pointer.PointId, "Independent control did not verify the exact signed point.");
        var independentHealth = await RecoveryControlReader.ReadHealthAsync(database, configuration, trust,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Check(independentHealth.Status == "verified" && independentHealth.Point?.PointId == point.Pointer.PointId,
            "Independent health did not verify its signed owner head.");
        var overdueHealth = await RecoveryControlReader.ReadHealthAsync(database, configuration, trust,
            DateTimeOffset.UtcNow.AddMinutes(46), CancellationToken.None);
        Check(overdueHealth.Status == "overdue", "Independent health ignored the 45-minute warning boundary.");
        Set("OwnerId", "foreign");
        try
        {
            await RecoveryControlReader.ReadPointAsync(database, configuration, trust, point.Pointer.PointId, CancellationToken.None);
            throw new InvalidOperationException("Independent control accepted a foreign signed catalog.");
        }
        catch (RecoveryWorkerException error) when (error.SafeCode == "control_point_binding_invalid") { }
        Set("OwnerId", "owner-fixture");
        var unverifiedId=Guid.Parse("00000000-0000-0000-0000-000000000001");
        var partition=RecoveryOwnerPointIndex.PartitionKey("owner-fixture","main");
        dynamo.PageItems=[Member(unverifiedId),Member(point.Pointer.PointId)];
        Dictionary<string,AttributeValue> Member(Guid id)=>new()
        {
            ["pk"]=S(partition),["sk"]=S(RecoveryOwnerPointIndex.PointSortKey(id)),
            ["pointId"]=S(id.ToString("D")),["ownerId"]=S("owner-fixture"),["libraryId"]=S("main")
        };
        var firstPage=await RecoveryControlReader.ReadPageAsync(database,configuration,trust,1,null,CancellationToken.None);
        Check(firstPage.Points.Single().Status=="unverified" && firstPage.Points.Single().CapturedAtUtc is null &&
            firstPage.NextAfterPointId==unverifiedId,"Unverified point invented trusted metadata or lost pagination.");
        var secondPage=await RecoveryControlReader.ReadPageAsync(database,configuration,trust,1,firstPage.NextAfterPointId,CancellationToken.None);
        Check(secondPage.Points.Single().PointId==point.Pointer.PointId && secondPage.Points.Single().Status=="verified" &&
            secondPage.NextAfterPointId is null,"Verified page or terminal cursor failed.");
        Check((await RecoveryControlReader.ReadPageAsync(database,configuration,trust,1,Guid.NewGuid(),CancellationToken.None)).Points.Count==0,
            "An absent owner cursor disclosed another scope.");
        dynamo.PageItems[0]["ownerId"]=S("foreign");
        try
        {
            await RecoveryControlReader.ReadPageAsync(database,configuration,trust,1,null,CancellationToken.None);
            throw new InvalidOperationException("Foreign membership entered control output.");
        }
        catch(RecoveryWorkerException error) when(error.SafeCode=="control_point_member_invalid") { }
        dynamo.PageItems=[];
        Check(dynamo.TransactionCount == 1 && dynamo.UpdateCount == 1,
            "Independent control read mutated verification records.");
        var deliveredSendCount = sqs.Sends.Count;
        await RecoveryVerificationPublisher.PublishAsync(database, queue, configuration, signer, trust,
            point, head, CancellationToken.None);
        Check(sqs.Sends.Count == deliveredSendCount && dynamo.TransactionCount == 1 &&
              Attr(dynamo.Read(pointKey["pk"].S, pointKey["sk"].S)!, "messageBodies") == deliveredBody,
            "A completed reverify must preserve the immutable delivered point outbox and avoid a second publication.");

        var changedHead = new RecoveryAuditHeadV1(4, new string('e', 64));
        try
        {
            await RecoveryVerificationPublisher.PublishAsync(database, queue, configuration, signer, trust,
                point, changedHead, CancellationToken.None);
            throw new InvalidOperationException("A different audit head for the same immutable snapshot must be rejected.");
        }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == "verification_point_marker_conflict") { }
        Check(sqs.Sends.Count == deliveredSendCount,
            "An audit-head conflict must not publish another observation.");

        var noAccess = point with { OwnerAccess = null };
        try
        {
            await RecoveryVerificationPublisher.PublishAsync(database, queue, configuration, signer, trust,
                noAccess, head, CancellationToken.None);
            throw new InvalidOperationException("A missing independently verified owner proof must block healthy publication.");
        }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == "owner_access_proof_required_for_healthy_catalog") { }
        Check(sqs.Sends.Count == deliveredSendCount,
            "An access-incomplete point must not send any healthy catalog or observations.");
        var failedPoint = Guid.NewGuid();
        var failedCapture = Guid.NewGuid();
        sqs.FailOnceAfterAcceptingCall = sqs.Sends.Count + 1;
        try
        {
            await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
                failedPoint, failedCapture, "verification", "failed", "verification_integrity_failed", CancellationToken.None);
            throw new InvalidOperationException("Lost cycle-send response did not leave a retryable outbox.");
        }
        catch (InvalidOperationException error) when (error.Message == "simulated_send_timeout") { }
        var cycleKey = RecoveryCyclePublisher.Key("owner-fixture", "main", failedPoint);
        var cycleItem = dynamo.Read(cycleKey["pk"].S, "CYCLE")!;
        Check(Attr(cycleItem, "state") == "pending", "Cycle bytes were not persisted before publication.");
        var firstBody = sqs.Sends[^1];
        var cycle = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(firstBody)), trust);
        using var safePayload = JsonDocument.Parse(cycle.CanonicalPayloadBytes);
        Check(safePayload.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "schemaVersion", "eventId", "captureId", "recoveryPointId", "ownerId", "libraryId", "observedAtUtc", "stage",
                "failedDataClass", "outcome", "safeCode", "recoveryWorkerBuildDigest" }.Order(StringComparer.Ordinal)),
            "Cycle payload included fields outside the approved publication contract.");
        Check(cycle.Payload.RecoveryPointId == failedPoint && cycle.Payload.CaptureId == failedCapture &&
            cycle.Payload.SafeCode == "verification_integrity_failed" && cycle.Payload.FailedDataClass == "recovery_records",
            "Cycle scope or fixed safe fields were lost.");
        await RecoveryCyclePublisher.ReplayPendingAsync(database, queue, configuration, signer, trust, CancellationToken.None);
        Check(sqs.Sends[^1] == firstBody && Attr(dynamo.Read(cycleKey["pk"].S, "CYCLE")!, "state") == "delivered",
            "A cycle retry changed its signature/event/time or did not finish delivery.");
        var cycleSendCount = sqs.Sends.Count;
        await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
            failedPoint, failedCapture, "verification", "failed", "verification_integrity_failed", CancellationToken.None);
        Check(sqs.Sends.Count == cycleSendCount && dynamo.TransactionCount == 2 &&
            Attr(dynamo.Read("OWNER#" + RecoveryOwnerPointIndex.ScopeHash("owner-fixture", "main"), "CYCLE_PENDING")!, "pointId") == "",
            "Delivered cycle replay sent again or modified a healthy owner marker.");
        await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
            null, null, "verification", "failed", "verification_inaccessible", CancellationToken.None, replayOnly: true);
        Check(sqs.Sends.Count == cycleSendCount, "Replay invented a failure when no outbox existed.");
        foreach (var invalid in new[] { "private/path", "database_password", "arbitrary_safe_looking_code" })
        {
            try
            {
                await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
                    failedPoint, failedCapture, "verification", "failed", invalid, CancellationToken.None);
                throw new InvalidOperationException("An unapproved cycle code entered the outbox.");
            }
            catch (RecoveryWorkerException error) when (error.SafeCode == "cycle_publication_input_invalid") { }
        }
        var secondCycle = Guid.NewGuid();
        dynamo.FailAfterCycleTransactionOnce = true;
        try
        {
            await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
                failedPoint, failedCapture, "verification", "failed", "verification_inaccessible", CancellationToken.None, cycleId: secondCycle);
            throw new InvalidOperationException("Unknown committed outbox transaction was not simulated.");
        }
        catch (InvalidOperationException error) when (error.Message == "cycle_transaction_response_lost") { }
        var secondKey = RecoveryCyclePublisher.Key("owner-fixture", "main", secondCycle);
        var secondItem = dynamo.Read(secondKey["pk"].S, "CYCLE")!;
        var secondBytes = Convert.FromBase64String(Attr(secondItem, "envelopeBytes"));
        dynamo.FailAfterCycleDeliveryOnce = true;
        try
        {
            await RecoveryCyclePublisher.ReplayPendingAsync(database, queue, configuration, signer, trust, CancellationToken.None);
            throw new InvalidOperationException("Unknown delivered update was not simulated.");
        }
        catch (InvalidOperationException error) when (error.Message == "cycle_delivery_response_lost") { }
        var sendsBeforeDeliveredReplay = sqs.Sends.Count;
        await RecoveryCyclePublisher.ReplayPendingAsync(database, queue, configuration, signer, trust, CancellationToken.None);
        Check(sqs.Sends.Count == sendsBeforeDeliveredReplay && System.Text.Encoding.UTF8.GetBytes(sqs.Sends[^1]).SequenceEqual(secondBytes),
            "Restart after a committed delivery resent or changed the signed cycle.");
        var secondPayload = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(secondBytes), trust).Payload;
        Check(secondPayload.EventId == secondCycle && secondPayload.RecoveryPointId == failedPoint &&
            dynamo.Read(cycleKey["pk"].S, "CYCLE") is not null,
            "A later failed cycle overwrote the first receipt or confused event and recovery-point identities.");
        var fallbackCycle = Guid.NewGuid();
        await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
            null, null, "verification", "failed", "verification_inaccessible", CancellationToken.None, cycleId: fallbackCycle);
        var fallbackPayload = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(System.Text.Encoding.UTF8.GetBytes(sqs.Sends[^1])), trust).Payload;
        Check(fallbackPayload.EventId == fallbackCycle && fallbackPayload.RecoveryPointId is null && fallbackPayload.CaptureId is null,
            "Pre-discovery failure fabricated a sealed point or capture identity.");
        cycleSendCount = sqs.Sends.Count;
        Set("ObservationQueueUrl", $"https://sqs.{sourceRegion}.amazonaws.com/{sourceAccount}/foreign");
        try
        {
            await RecoveryCyclePublisher.PublishAsync(database, queue, configuration, signer, trust,
                failedPoint, failedCapture, "verification", "failed", "verification_inaccessible", CancellationToken.None);
            throw new InvalidOperationException("Cycle publication accepted a foreign destination.");
        }
        catch (RecoveryWorkerException) { }
        Check(sqs.Sends.Count == cycleSendCount, "Rejected cycle inputs still reached the queue.");
    }

    private static VerifiedCopiedPoint CopiedPoint(DateTimeOffset now)
    {
        byte[] bytes = [1, 2, 3, 4];
        var digest = RecoveryEvidenceCodec.Hash(bytes);
        var source = Manifest(Guid.NewGuid(), now, digest, bytes.Length);
        var sourceDigest = source.Digest();
        var access = new RecoveryComponentReference("owner-access-proof", RecoveryDataClass.AccessEvidence,
            "s3://evidence-bucket/owner/access.json", "access-v1", digest, bytes.Length);
        var final = source with { Components = source.Components.Append(access).ToArray() };
        var fullDigest = final.Digest();
        var pointer = new CopyPointer(source.RecoveryPointId, source.CaptureId, source.OwnerId, source.LibraryId,
            "111111111111", "ap-southeast-1", "222222222222", "ap-southeast-2", "capture-bucket",
            "manifests/source.json", "source-v1", sourceDigest, "evidence-bucket", "manifests/final.json",
            "final-v1", fullDigest, "points/copy-checkpoint.json", "checkpoint-v1", new string('6', 64),
            512, now, "copied");
        var ownerAccess = new VerifiedRecoveryOwnerAccessEvidence(null!, [], new string('7', 64), "owner-key",
            new string('8', 64), []);
        return new(pointer, source, final, null!, [], new string('6', 64), new string('9', 64), ownerAccess);
    }

    private static RecoveryPointManifestV1 Manifest(Guid pointId, DateTimeOffset now, string digest, long length)
    {
        var components = new[]
        {
            new RecoveryComponentReference("database", RecoveryDataClass.DatabaseSnapshot,
                "s3://capture-bucket/database.dump", "db-v1", digest, length),
            new RecoveryComponentReference("inventory", RecoveryDataClass.ObjectInventory,
                "s3://capture-bucket/inventory.json", "inventory-v1", digest, length)
        };
        var classes = Enum.GetValues<RecoveryDataClass>().Where(item => item != RecoveryDataClass.PreviewDerivative).ToArray();
        return new RecoveryPointManifestV1(1, pointId, Guid.NewGuid(), "owner-fixture", "main", "ap-southeast-1",
            now, now.AddMinutes(-1), "snapshot-fixture", "16/B374D848", new string('5', 64),
            new RecoveryChangeWatermark(1, now.AddMinutes(-2)), components, [],
            classes.Select(item => new RecoveryClassCoverage(item,
                item == RecoveryDataClass.OriginalImage ? 1 : 0,
                item == RecoveryDataClass.OriginalImage ? length : 0)).ToArray());
    }

    private static Dictionary<string, AttributeValue> PointKey(Guid pointId) => new()
    {
        ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()), ["sk"] = S("VERIFY")
    };

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable("Recovery__" + name, value);
    private static string Attr(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> Clone(IReadOnlyDictionary<string, AttributeValue> item) =>
        item.ToDictionary(pair => pair.Key, pair => new AttributeValue { S = pair.Value.S, N = pair.Value.N, BOOL = pair.Value.BOOL }, StringComparer.Ordinal);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestSigner(RSA rsa, string keyId) : IRecoveryEvidenceSigner
    {
        private readonly byte[] publicKey = rsa.ExportSubjectPublicKeyInfo();
        public string KeyId { get; } = keyId;
        public string PublicKeySha256 => RecoveryEvidenceCodec.Hash(publicKey);
        public RecoverySigningTrust Trust => new(KeyId, Convert.ToBase64String(publicKey), PublicKeySha256);

        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(rsa.SignHash(sha256Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
    }

    private class FakeDynamo : DispatchProxy
    {
        private readonly Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> items = [];
        public int TransactionCount { get; private set; }
        public int UpdateCount { get; private set; }
        public bool FailAfterCycleTransactionOnce { get; set; }
        public bool FailAfterCycleDeliveryOnce { get; set; }
        public List<Dictionary<string,AttributeValue>> PageItems { get; set; }=[];
        public static IAmazonDynamoDB Create(out FakeDynamo fake)
        {
            var client = DispatchProxy.Create<IAmazonDynamoDB, FakeDynamo>();
            fake = (FakeDynamo)(object)client;
            return client;
        }
        public Dictionary<string, AttributeValue>? Read(string pk, string sk) =>
            items.TryGetValue((pk, sk), out var item) ? Clone(item) : null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("dynamo_call_missing");
            return targetMethod.Name switch
            {
                nameof(IAmazonDynamoDB.GetItemAsync) => Task.FromResult(Get((GetItemRequest)args[0]!)),
                nameof(IAmazonDynamoDB.TransactWriteItemsAsync) => Task.FromResult(Transact((TransactWriteItemsRequest)args[0]!)),
                nameof(IAmazonDynamoDB.UpdateItemAsync) => Task.FromResult(Update((UpdateItemRequest)args[0]!)),
                nameof(IAmazonDynamoDB.QueryAsync) => Task.FromResult(Query((QueryRequest)args[0]!)),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }


        private GetItemResponse Get(GetItemRequest request)
        {
            if (request.ConsistentRead != true) throw new InvalidOperationException("publisher_reads_must_be_strong");
            var key = Key(request.Key);
            var member=PageItems.SingleOrDefault(item=>Key(item)==key);
            if(member is not null) return new GetItemResponse { Item=Clone(member) };
            return new GetItemResponse { Item = items.TryGetValue(key, out var item) ? Clone(item) : new() };
        }

        private QueryResponse Query(QueryRequest request)
        {
            Check(request.ConsistentRead==true && request.Limit is >=1 and <=100 &&
                request.TableName=="recovery-control-fixture" && request.ExpressionAttributeValues[":owner"].S==
                RecoveryOwnerPointIndex.PartitionKey("owner-fixture","main") && request.ExpressionAttributeValues[":points"].S=="POINT#",
                "Control page query escaped its strong bounded owner partition.");
            var after=request.ExclusiveStartKey is { Count:>0 } ? Attr(request.ExclusiveStartKey,"sk") : "";
            var remaining=PageItems.Where(item=>string.CompareOrdinal(Attr(item,"sk"),after)>0)
                .OrderBy(item=>Attr(item,"sk"),StringComparer.Ordinal).ToArray();
            var page=remaining.Take(request.Limit!.Value).Select(Clone).ToList();
            return new QueryResponse { Items=page,LastEvaluatedKey=remaining.Length>page.Count ?
                new() { ["pk"]=page[^1]["pk"],["sk"]=page[^1]["sk"] } : new() };
        }

        private TransactWriteItemsResponse Transact(TransactWriteItemsRequest request)
        {
            TransactionCount++;
            var put = request.TransactItems.Single(transaction => transaction.Put is not null).Put!;
            var update = request.TransactItems.Single(transaction => transaction.Update is not null).Update!;
            var pointKey = Key(put.Item);
            if (items.ContainsKey(pointKey)) throw new TransactionCanceledException("point_marker_exists");
            var ownerKey = Key(update.Key);
            if (pointKey.Sk == "CYCLE")
            {
                Check(put.TableName == "recovery-verification-fixture" && update.TableName == put.TableName &&
                    ownerKey.Sk == "CYCLE_PENDING" && put.ConditionExpression == "attribute_not_exists(pk)",
                    "Cycle transaction crossed table, scope or immutable envelope boundaries.");
                var incoming = update.ExpressionAttributeValues;
                if (items.TryGetValue(ownerKey, out var existing) && Attr(existing, "pointId") != "" &&
                    (Attr(existing, "pointId") != Attr(incoming, ":point") || Attr(existing, "payloadSha256") != Attr(incoming, ":sha")))
                    throw new TransactionCanceledException("cycle_pending_exists");
                items.Add(pointKey, Clone(put.Item));
                items[ownerKey] = new(update.Key)
                {
                    ["ownerId"] = incoming[":owner"], ["libraryId"] = incoming[":library"],
                    ["pointId"] = incoming[":point"], ["payloadSha256"] = incoming[":sha"]
                };
                if (FailAfterCycleTransactionOnce)
                {
                    FailAfterCycleTransactionOnce = false;
                    throw new InvalidOperationException("cycle_transaction_response_lost");
                }
                return new TransactWriteItemsResponse();
            }
            if (items.TryGetValue(ownerKey, out var current))
            {
                var values = update.ExpressionAttributeValues;
                var currentCutoff = Attr(current, "snapshotCutoffUtc");
                var incomingCutoff = Attr(values, ":cutoff");
                var currentSequence = long.TryParse(Attr(current, "changeSequence"), out var parsed) ? parsed : -1;
                var incomingSequence = long.Parse(Attr(values, ":changeSequence"), System.Globalization.CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(incomingCutoff, currentCutoff) < 0 ||
                    incomingCutoff == currentCutoff && currentSequence > incomingSequence)
                    throw new TransactionCanceledException("owner_marker_not_monotonic");
            }
            items.Add(pointKey, Clone(put.Item));
            items[ownerKey] = OwnerMarker(update.ExpressionAttributeValues);
            return new TransactWriteItemsResponse();
        }

        private UpdateItemResponse Update(UpdateItemRequest request)
        {
            var key = Key(request.Key);
            if (!items.TryGetValue(key, out var item)) throw new ConditionalCheckFailedException("marker_missing");
            var values = request.ExpressionAttributeValues;
            if (key.Sk == "CYCLE_PENDING")
            {
                if (Attr(item, "ownerId") != Attr(values, ":owner") || Attr(item, "libraryId") != Attr(values, ":library") ||
                    Attr(item, "pointId") != Attr(values, ":point") || Attr(item, "payloadSha256") != Attr(values, ":sha"))
                    throw new ConditionalCheckFailedException("pending_clear_conflict");
                item.Remove("pointId");
                item.Remove("payloadSha256");
                return new UpdateItemResponse();
            }
            if (key.Sk == "CYCLE")
            {
                Check(request.TableName == "recovery-verification-fixture", "Cycle delivery escaped its table.");
                if (Attr(item, "state") != "pending" || Attr(item, "payloadSha256") != Attr(values, ":sha"))
                    throw new ConditionalCheckFailedException("cycle_delivery_condition_failed");
                item["state"] = values[":delivered"];
                item["deliveredAtUtc"] = values[":now"];
                UpdateCount++;
                if (FailAfterCycleDeliveryOnce)
                {
                    FailAfterCycleDeliveryOnce = false;
                    throw new InvalidOperationException("cycle_delivery_response_lost");
                }
                return new UpdateItemResponse();
            }
            if (Attr(item, "state") != Attr(values, ":pending") ||
                Attr(item, "sourceManifestSha256") != Attr(values, ":source") ||
                Attr(item, "fullManifestSha256") != Attr(values, ":full"))
                throw new ConditionalCheckFailedException("delivery_condition_failed");
            item["state"] = values[":delivered"];
            item["deliveredAtUtc"] = values[":now"];
            UpdateCount++;
            return new UpdateItemResponse();
        }

        private static Dictionary<string, AttributeValue> OwnerMarker(Dictionary<string, AttributeValue> values)
        {
            var names = new[]
            {
                ("ownerId", ":owner"), ("libraryId", ":library"), ("pointId", ":point"),
                ("sourceManifestSha256", ":sourceManifest"), ("fullManifestSha256", ":fullManifest"),
                ("checkpointSha256", ":checkpoint"), ("snapshotCutoffUtc", ":cutoff"),
                ("changeSequence", ":changeSequence"), ("verifiedAtUtc", ":verifiedAt"),
                ("auditHeadSequence", ":auditSequence"), ("auditHeadEventSha256", ":auditSha"),
                ("payloadSha256", ":payloadSha"), ("safeStatus", ":healthy"),
                ("safeClassCoverage", ":coverage"), ("workerBuildDigest", ":worker")
            };
            var item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S("OWNER#" + RecoveryOwnerPointIndex.ScopeHash(Attr(values, ":owner"), Attr(values, ":library"))),
                ["sk"] = S("VERIFIED")
            };
            foreach (var (attribute, value) in names) item[attribute] = values[value];
            return Clone(item);
        }

        private static (string Pk, string Sk) Key(IReadOnlyDictionary<string, AttributeValue> item) =>
            (Attr(item, "pk"), Attr(item, "sk"));
    }

    private class FakeSqs : DispatchProxy
    {
        public List<string> Sends { get; } = [];
        public int? FailOnceAfterAcceptingCall { get; set; }
        private int _sendCount;
        public static IAmazonSQS Create(out FakeSqs fake)
        {
            var client = DispatchProxy.Create<IAmazonSQS, FakeSqs>();
            fake = (FakeSqs)(object)client;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IAmazonSQS.SendMessageAsync) || args is null)
                throw new NotSupportedException(targetMethod?.Name ?? "sqs_call_missing");
            var request = (SendMessageRequest)args[0]!;
            _sendCount++;
            Sends.Add(request.MessageBody);
            if (FailOnceAfterAcceptingCall == _sendCount)
            {
                FailOnceAfterAcceptingCall = null;
                throw new InvalidOperationException("simulated_send_timeout");
            }
            return Task.FromResult(new SendMessageResponse { MessageId = "fixture-message" });
        }
    }
}
