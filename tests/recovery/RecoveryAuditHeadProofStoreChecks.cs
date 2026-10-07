using System.Reflection;
using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryAuditHeadProofStoreChecks
{
    public static async Task RunAsync()
    {
        var oldEnvironment = Environment.GetEnvironmentVariable("Recovery__Environment");
        var oldWorkerDigest = Environment.GetEnvironmentVariable("Recovery__RecoveryWorkerBuildDigest");
        var oldTable = Environment.GetEnvironmentVariable("Recovery__VerificationTableName");
        try { await RunCoreAsync(); }
        finally
        {
            Environment.SetEnvironmentVariable("Recovery__Environment", oldEnvironment);
            Environment.SetEnvironmentVariable("Recovery__RecoveryWorkerBuildDigest", oldWorkerDigest);
            Environment.SetEnvironmentVariable("Recovery__VerificationTableName", oldTable);
        }
    }

    private static async Task RunCoreAsync()
    {
        Environment.SetEnvironmentVariable("Recovery__Environment", "fixture");
        Environment.SetEnvironmentVariable("Recovery__RecoveryWorkerBuildDigest", new string('f', 64));
        Environment.SetEnvironmentVariable("Recovery__VerificationTableName", "verification-fixture");
        var configuration = RecoveryWorkerConfiguration.Load("verify");
        using var rsa = RSA.Create(2048);
        var signer = new TestSigner(rsa, "fixture-audit-proof-key");
        var now = DateTimeOffset.UtcNow;
        var source = Manifest(Guid.NewGuid(), now);
        var sourceDigest = source.Digest();
        var incomplete = Point(source, sourceDigest, state: "access_incomplete", final: null);
        var minimum = new RecoveryAuditHeadV1(1, new string('a', 64));
        var prior = new RecoveryAuditHeadV1(2, new string('b', 64));
        var head = new RecoveryAuditHeadV1(3, new string('c', 64));
        var requirement = new RecoveryAuditHeadRequirementV1(minimum, prior);
        var genesis = new VerifiedRecoveryAuditGenesis(new RecoveryAuditGenesisPayloadV1(1, 0,
            new string('0', 64), now.AddDays(-1)), new string('d', 64), signer.KeyId, signer.PublicKeySha256);

        var client = Fake.Create(out var store);
        await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
            incomplete, head, requirement, genesis, CancellationToken.None);
        var first = store.Read("POINT#" + incomplete.Pointer.PointId.ToString("D").ToLowerInvariant(), "AUDIT_VERIFY")
            ?? throw new InvalidOperationException("Initial signed audit proof was not persisted.");
        var firstEnvelope = Value(first, "envelopeBytes");
        Check(store.PutCount == 1 && store.UpdateCount == 0 && signer.SignCount == 1,
            "Initial audit proof must be a single immutable insert.");

        await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
            incomplete, head, requirement, genesis, CancellationToken.None);
        Check(store.PutCount == 1 && store.UpdateCount == 0 && signer.SignCount == 1 &&
              Value(store.Read("POINT#" + incomplete.Pointer.PointId.ToString("D").ToLowerInvariant(), "AUDIT_VERIFY")!, "envelopeBytes") == firstEnvelope,
            "An exact incomplete-proof retry must preserve its signed bytes.");

        await RejectAsync("audit_head_proof_conflict", () => RecoveryAuditHeadProofStore.PersistAsync(client,
            configuration, signer, signer.Trust, incomplete, new RecoveryAuditHeadV1(4, new string('e', 64)),
            requirement, genesis, CancellationToken.None));

        var finalDigest = new string('9', 64);
        var copied = Point(source, finalDigest, "copied", source);
        await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
            copied, head, requirement, genesis, CancellationToken.None);
        var upgraded = await RecoveryAuditHeadProofStore.ReadAsync(client, "verification-fixture",
            copied.Pointer.PointId, signer.Trust, CancellationToken.None)
            ?? throw new InvalidOperationException("The incomplete proof did not upgrade after the final manifest was verified.");
        Check(store.PutCount == 1 && store.UpdateCount == 1 && signer.SignCount == 2 &&
              upgraded.Payload.FullManifestSha256 == finalDigest &&
              upgraded.Payload.PreviouslyVerifiedHead == prior &&
              store.Read("POINT#" + copied.Pointer.PointId.ToString("D").ToLowerInvariant(), "AUDIT_VERIFY") is { } upgradedRow &&
              Value(upgradedRow, "proofState") == "copied",
            "Only an access_incomplete-to-copied transition may replace the proof, preserving its accepted prior head.");

        await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
            copied, head, new RecoveryAuditHeadRequirementV1(minimum, head), genesis, CancellationToken.None);
        Check(store.UpdateCount == 1 && signer.SignCount == 2,
            "Revalidating an already copied point must preserve its historical proof and signature.");
        await RejectAsync("audit_head_proof_downgrade_rejected", () => RecoveryAuditHeadProofStore.PersistAsync(client,
            configuration, signer, signer.Trust, incomplete, head, requirement, genesis, CancellationToken.None));

        await RejectConcurrentInsertAsync(configuration, signer, incomplete, requirement, genesis, head, source, now);
        await RejectChangedUpgradeDigestAsync(configuration, signer, incomplete, copied, requirement, genesis, head);
    }

    private static async Task RejectConcurrentInsertAsync(RecoveryWorkerConfiguration configuration,
        TestSigner signer, VerifiedCopiedPoint point, RecoveryAuditHeadRequirementV1 requirement,
        VerifiedRecoveryAuditGenesis genesis, RecoveryAuditHeadV1 head, RecoveryPointManifestV1 source,
        DateTimeOffset now)
    {
        var competingPoint = Point(source, source.Digest(), "access_incomplete", null);
        var competingClient = Fake.Create(out var competingStore);
        await RecoveryAuditHeadProofStore.PersistAsync(competingClient, configuration, signer, signer.Trust,
            competingPoint, new RecoveryAuditHeadV1(4, new string('e', 64)), requirement, genesis, CancellationToken.None);
        var competing = competingStore.Read("POINT#" + point.Pointer.PointId.ToString("D").ToLowerInvariant(), "AUDIT_VERIFY")!;

        var client = Fake.Create(out var store);
        store.BeforePut = fake => fake.Seed(competing);
        await RejectAsync("audit_head_proof_conflict", () => RecoveryAuditHeadProofStore.PersistAsync(client,
            configuration, signer, signer.Trust, point, head, requirement, genesis, CancellationToken.None));
        Check(store.PutCount == 0 && store.Read("POINT#" + point.Pointer.PointId.ToString("D").ToLowerInvariant(), "AUDIT_VERIFY") is not null,
            "A concurrent signed proof with a different audit head must not be overwritten.");
        _ = now;
    }

    private static async Task RejectChangedUpgradeDigestAsync(RecoveryWorkerConfiguration configuration,
        TestSigner signer, VerifiedCopiedPoint incomplete, VerifiedCopiedPoint copied,
        RecoveryAuditHeadRequirementV1 requirement, VerifiedRecoveryAuditGenesis genesis,
        RecoveryAuditHeadV1 head)
    {
        var client = Fake.Create(out var store);
        await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
            incomplete, head, requirement, genesis, CancellationToken.None);
        store.BeforeUpdate = fake => fake.Change("POINT#" + incomplete.Pointer.PointId.ToString("D").ToLowerInvariant(),
            "AUDIT_VERIFY", "payloadSha256", new string('1', 64));
        try
        {
            await RecoveryAuditHeadProofStore.PersistAsync(client, configuration, signer, signer.Trust,
                copied, head, requirement, genesis, CancellationToken.None);
        }
        catch (RecoveryWorkerException) { return; }
        throw new InvalidOperationException("A changed prior proof digest must prevent the incomplete-proof upgrade.");
    }

    private static VerifiedCopiedPoint Point(RecoveryPointManifestV1 source, string fullManifestSha,
        string state, RecoveryPointManifestV1? final)
    {
        var sourceDigest = source.Digest();
        var point = new CopyPointer(source.RecoveryPointId, source.CaptureId, source.OwnerId, source.LibraryId,
            "111111111111", "ap-southeast-1", "222222222222", "ap-southeast-2", "capture-bucket",
            "manifests/source.json", "source-version", sourceDigest, "evidence-bucket", "manifests/final.json",
            "final-version", fullManifestSha, "checkpoints/point.json", "checkpoint-version", new string('8', 64),
            1024, source.CapturedAtUtc, state);
        return new(point, source, final, null!, [], new string('7', 64), new string('6', 64), null);
    }

    private static RecoveryPointManifestV1 Manifest(Guid point, DateTimeOffset now)
    {
        byte[] content = [1, 2, 3, 4];
        var digest = RecoveryEvidenceCodec.Hash(content);
        var components = new[]
        {
            new RecoveryComponentReference("database", RecoveryDataClass.DatabaseSnapshot,
                "s3://capture-bucket/database.dump", "db-v1", digest, content.Length),
            new RecoveryComponentReference("inventory", RecoveryDataClass.ObjectInventory,
                "s3://capture-bucket/inventory.json", "inventory-v1", digest, content.Length)
        };
        var classes = Enum.GetValues<RecoveryDataClass>().Where(item => item != RecoveryDataClass.PreviewDerivative).ToArray();
        return new RecoveryPointManifestV1(1, point, Guid.NewGuid(), "owner-fixture", "main", "ap-southeast-1",
            now, now.AddMinutes(-1), "snapshot-fixture", "16/B374D848", new string('5', 64),
            new RecoveryChangeWatermark(1, now.AddMinutes(-2)), components, [],
            classes.Select(item => new RecoveryClassCoverage(item, 0, 0)).ToArray());
    }

    private static async Task RejectAsync(string safeCode, Func<Task> action)
    {
        try { await action(); }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == safeCode) { return; }
        throw new InvalidOperationException($"Expected signed audit proof failure '{safeCode}'.");
    }

    private static string Value(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;

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
        public int SignCount { get; private set; }

        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignCount++;
            return Task.FromResult(rsa.SignHash(sha256Digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
    }

    private class Fake : DispatchProxy
    {
        private readonly Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> items = [];
        public int PutCount { get; private set; }
        public int UpdateCount { get; private set; }
        public Action<Fake>? BeforePut { get; set; }
        public Action<Fake>? BeforeUpdate { get; set; }

        public static IAmazonDynamoDB Create(out Fake fake)
        {
            var client = DispatchProxy.Create<IAmazonDynamoDB, Fake>();
            fake = (Fake)(object)client;
            return client;
        }

        public Dictionary<string, AttributeValue>? Read(string pk, string sk) =>
            items.TryGetValue((pk, sk), out var item) ? Clone(item) : null;

        public void Seed(Dictionary<string, AttributeValue> item) => items[Key(item)] = Clone(item);

        public void Change(string pk, string sk, string name, string value)
        {
            if (!items.TryGetValue((pk, sk), out var item)) throw new InvalidOperationException("proof_row_missing");
            item[name] = S(value);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("dynamo_call_missing");
            return targetMethod.Name switch
            {
                nameof(IAmazonDynamoDB.GetItemAsync) => Task.FromResult(Get((GetItemRequest)args[0]!)),
                nameof(IAmazonDynamoDB.PutItemAsync) => Task.FromResult(Put((PutItemRequest)args[0]!)),
                nameof(IAmazonDynamoDB.UpdateItemAsync) => Task.FromResult(Update((UpdateItemRequest)args[0]!)),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }

        private GetItemResponse Get(GetItemRequest request)
        {
            if (request.ConsistentRead != true) throw new InvalidOperationException("proof_reads_must_be_strong");
            var key = Key(request.Key);
            return new GetItemResponse { Item = items.TryGetValue(key, out var item) ? Clone(item) : new() };
        }

        private PutItemResponse Put(PutItemRequest request)
        {
            BeforePut?.Invoke(this);
            var key = Key(request.Item);
            if (items.ContainsKey(key)) throw new ConditionalCheckFailedException("proof_already_exists");
            PutCount++;
            items.Add(key, Clone(request.Item));
            return new PutItemResponse();
        }

        private UpdateItemResponse Update(UpdateItemRequest request)
        {
            BeforeUpdate?.Invoke(this);
            var key = Key(request.Key);
            if (!items.TryGetValue(key, out var item) || Read(item, "proofState") != "access_incomplete" ||
                Read(item, "sourceManifestSha256") != Read(request.ExpressionAttributeValues, ":source") ||
                Read(item, "fullManifestSha256") != Read(request.ExpressionAttributeValues, ":sourceFull") ||
                Read(item, "payloadSha256") != Read(request.ExpressionAttributeValues, ":prior"))
                throw new ConditionalCheckFailedException("proof_upgrade_condition_failed");
            foreach (var (attribute, valueName) in new[]
                     {
                         ("fullManifestSha256", ":full"), ("proofState", ":copied"), ("payloadSha256", ":payload"),
                         ("envelopeBytes", ":envelope"), ("verifiedAtUtc", ":verified"), ("workerBuildDigest", ":worker")
                     })
                item[attribute] = Clone(request.ExpressionAttributeValues[valueName]);
            UpdateCount++;
            return new UpdateItemResponse { Attributes = Clone(item) };
        }

        private static string Read(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
            item.TryGetValue(name, out var value) ? value.S ?? string.Empty : string.Empty;

        private static (string Pk, string Sk) Key(IReadOnlyDictionary<string, AttributeValue> item) =>
            (Read(item, "pk"), Read(item, "sk"));

        private static AttributeValue S(string value) => new() { S = value };

        private static Dictionary<string, AttributeValue> Clone(IReadOnlyDictionary<string, AttributeValue> item) =>
            item.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);

        private static AttributeValue Clone(AttributeValue value) => new() { S = value.S, N = value.N, BOOL = value.BOOL };
    }
}
