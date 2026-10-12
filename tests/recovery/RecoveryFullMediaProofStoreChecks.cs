using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryFullMediaProofStoreChecks
{
    internal static async Task RunAsync()
    {
        string[] names = ["Environment", "RecoveryWorkerBuildDigest", "VerificationTableName", "OwnerId", "LibraryId"];
        var previous = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        try { await CheckAsync(); }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable("Recovery__" + name, previous[name]); }
    }

    private static async Task CheckAsync()
    {
        var hash = new string('a', 64);
        Environment.SetEnvironmentVariable("Recovery__Environment", "fixture");
        Environment.SetEnvironmentVariable("Recovery__RecoveryWorkerBuildDigest", hash);
        Environment.SetEnvironmentVariable("Recovery__VerificationTableName", "verification-fixture");
        Environment.SetEnvironmentVariable("Recovery__OwnerId", "owner");
        Environment.SetEnvironmentVariable("Recovery__LibraryId", "library");
        var configuration = RecoveryWorkerConfiguration.Load("verify");
        var now = DateTimeOffset.UtcNow;
        var pointer = new CopyPointer(Guid.NewGuid(), Guid.NewGuid(), "owner", "library", "111111111111", "ap-southeast-1",
            "222222222222", "ap-southeast-2", "source-capture", "manifest", "v1", hash,
            "evidence", "final", "v1", hash, "checkpoint", "v1", hash, 1, now.AddDays(-1), "copied");
        var point = new VerifiedCopiedPoint(pointer, null!, null, null!, [], hash, hash, null,
            FullMediaVerifiedAtUtc: now.AddMinutes(-2));
        using var rsa = RSA.Create(2048);
        var signer = new Signer(rsa);
        var client = DispatchProxy.Create<IAmazonDynamoDB, Database>();
        var database = (Database)(object)client;
        async Task<DateTimeOffset?> Read(CopyPointer? p = null) => await RecoveryFullMediaProofStore.ReadAsync(client,
            configuration, p ?? pointer, signer.Trust, now, default);
        if (await Read() is not null) throw new InvalidOperationException("Absent proof acquired authority");
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, point, default);
        if (await Read() != point.FullMediaVerifiedAtUtc) throw new InvalidOperationException("Full read time was refreshed");
        var original = Clone(database.Item!);
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, point, default);
        if (database.Writes != 1 || signer.Signs != 1) throw new InvalidOperationException("Exact retry refreshed signed proof");
        var newer = point with { FullMediaVerifiedAtUtc = now.AddMinutes(-1) };
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, newer, default);
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, point, default);
        if (await Read() != newer.FullMediaVerifiedAtUtc || database.Writes != 2)
            throw new InvalidOperationException("Older verification overwrote a newer proof");
        foreach (var invalid in new[] { point with { FullMediaVerifiedAtUtc = null }, point with { HistoricalOwnerAccess = true },
            point with { FullMediaVerifiedAtUtc = now.AddDays(1) }, point with { CheckpointEnvelopeSha256 = new string('b', 64) } })
            await Reject(() => RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, invalid, default));
        foreach (var key in new[] { "pk", "sk", "pointId", "captureId", "ownerId", "libraryId", "checkpointSha256", "payloadSha256", "verifiedAtUtc", "workerBuildDigest" })
        {
            database.Item = Clone(original);
            database.Item[key] = S("altered");
            database.IgnoreKey = true;
            await Reject(async () => { _ = await Read(); });
        }
        database.IgnoreKey = false;
        database.Item = Clone(original);
        if (await Read(pointer with { CheckpointSha256 = new string('b', 64) }) is not null)
            throw new InvalidOperationException("New checkpoint inherited another closure's proof");

        var payload = new RecoveryFullMediaProofV1(1, pointer.PointId, pointer.CaptureId, pointer.OwnerId,
            pointer.LibraryId, pointer.CheckpointSha256, point.FullMediaVerifiedAtUtc!.Value, hash);
        Dictionary<string, AttributeValue> Signed(RecoveryFullMediaProofV1 value)
        {
            var bytes = RecoveryEvidenceCodec.SerializeCanonical(value);
            var envelope = new PrivateSignedCheckpointV1(1, "recovery_full_media_verification", "verify", signer.KeyId,
                signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes),
                Convert.ToBase64String(rsa.SignHash(SHA256.HashData(bytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            var item = Clone(original);
            item["envelopeBytes"] = S(Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(envelope)));
            item["payloadSha256"] = S(envelope.PayloadSha256);
            item["verifiedAtUtc"] = S(value.VerifiedAtUtc.ToString("O"));
            return item;
        }
        foreach (var altered in new[] { payload with { SchemaVersion = 2 }, payload with { OwnerId = "foreign" },
            payload with { CaptureId = Guid.NewGuid() }, payload with { PointId = Guid.NewGuid() },
            payload with { LibraryId = "foreign" }, payload with { CheckpointSha256 = new string('b', 64) },
            payload with { RecoveryWorkerBuildDigest = new string('b', 64) }, payload with { VerifiedAtUtc = now.AddSeconds(1) },
            payload with { VerifiedAtUtc = pointer.CopiedAtUtc.AddSeconds(-1) },
            payload with { VerifiedAtUtc = now.ToOffset(TimeSpan.FromHours(1)) } })
        {
            await Reject(async () => { database.Item = Signed(altered); _ = await Read(); });
        }
        database.Item = Clone(original);
        database.Item["envelopeBytes"] = S(database.Item["envelopeBytes"].S + " ");
        await Reject(async () => { _ = await Read(); });

        database.Item = null;
        database.LoseWriteResponse = true;
        try { await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, point, default); }
        catch (IOException error) when (error.Message == "fixture_lost_write_response") { }
        var writesAfterLoss = database.Writes;
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, point, default);
        if (database.Writes != writesAfterLoss || await Read() != point.FullMediaVerifiedAtUtc)
            throw new InvalidOperationException("Lost provider response refreshed proof or issued another write");
        // A newer concurrent signer wins the CAS; the slower caller verifies it
        // rather than replacing its full-read observation with an older one.
        database.Item = Clone(original);
        database.BeforePut = () => database.Item = Signed(payload with { VerifiedAtUtc = now.AddSeconds(-30) });
        await RecoveryFullMediaProofStore.PersistAsync(client, configuration, signer, signer.Trust, newer, default);
        if (await Read() != now.AddSeconds(-30)) throw new InvalidOperationException("Concurrent newer proof was not retained");
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static Dictionary<string, AttributeValue> Clone(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(pair => pair.Key, pair => S(pair.Value.S));
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (RecoveryWorkerException) { return; }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Untrusted or incomplete full media proof accepted");
    }
    private sealed class Signer(RSA rsa) : IRecoveryEvidenceSigner
    {
        public string KeyId => "fixture-full-media-key";
        public string PublicKeySha256 => RecoveryEvidenceCodec.Hash(rsa.ExportSubjectPublicKeyInfo());
        public RecoverySigningTrust Trust => new(KeyId, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), PublicKeySha256);
        internal int Signs;
        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken ct)
        { Signs++; return Task.FromResult(rsa.SignHash(digest.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); }
    }
    private class Database : DispatchProxy
    {
        internal Dictionary<string, AttributeValue>? Item;
        internal int Writes;
        internal bool LoseWriteResponse, IgnoreKey;
        internal Action? BeforePut;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IAmazonDynamoDB.GetItemAsync))
            {
                var request = (GetItemRequest)args![0]!;
                if (request.ConsistentRead != true || request.TableName != "verification-fixture")
                    throw new InvalidOperationException("Full media proof read lost custody or strong consistency");
                var found = Item is not null && (IgnoreKey || request.Key.All(pair => Item[pair.Key].S == pair.Value.S));
                return Task.FromResult(new GetItemResponse { Item = found ? Clone(Item!) : [] });
            }
            if (method?.Name == nameof(IAmazonDynamoDB.PutItemAsync))
            {
                var request = (PutItemRequest)args![0]!;
                var callback = BeforePut; BeforePut = null; callback?.Invoke();
                if (request.ConditionExpression != "attribute_not_exists(pk) OR payloadSha256=:prior")
                    throw new InvalidOperationException("Full media proof has no signed prior CAS");
                if (Item is not null && Item["payloadSha256"].S != request.ExpressionAttributeValues[":prior"].S)
                    throw new ConditionalCheckFailedException("fixture concurrent writer");
                Item = Clone(request.Item); Writes++;
                if (LoseWriteResponse) { LoseWriteResponse = false; throw new IOException("fixture_lost_write_response"); }
                return Task.FromResult(new PutItemResponse());
            }
            throw new NotSupportedException(method?.Name);
        }
    }
}
