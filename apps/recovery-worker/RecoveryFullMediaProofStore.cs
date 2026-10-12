using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryFullMediaProofV1(int SchemaVersion, Guid PointId, Guid CaptureId,
    string OwnerId, string LibraryId, string CheckpointSha256, DateTimeOffset VerifiedAtUtc,
    string RecoveryWorkerBuildDigest);

internal static class RecoveryFullMediaProofStore
{
    internal static async Task<DateTimeOffset?> ReadAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration, CopyPointer pointer, RecoverySigningTrust trust,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var key = Key(pointer);
        var response = await dynamo.GetItemAsync(new GetItemRequest
        { TableName = configuration.Required("VerificationTableName"), Key = key, ConsistentRead = true }, ct);
        if (response.Item is not { Count: > 0 } item) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Value(item, "envelopeBytes"));
            if (bytes.Length is < 1 or > 64 * 1024) throw new InvalidDataException("full_media_proof_size_invalid");
            var envelope = ParseCanonical<PrivateSignedCheckpointV1>(bytes);
            if (envelope.SchemaVersion != 1 || envelope.MessageType != "recovery_full_media_verification" ||
                string.IsNullOrWhiteSpace(envelope.WorkerId)) throw new InvalidDataException("full_media_proof_envelope_invalid");
            var payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
            _ = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, envelope.PayloadSha256, envelope.SignatureBase64,
                envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
            var proof = ParseCanonical<RecoveryFullMediaProofV1>(payloadBytes);
            if (proof.SchemaVersion != 1 || proof.PointId != pointer.PointId || proof.CaptureId != pointer.CaptureId ||
                proof.OwnerId != pointer.OwnerId || proof.LibraryId != pointer.LibraryId ||
                proof.CheckpointSha256 != pointer.CheckpointSha256 || proof.RecoveryWorkerBuildDigest != configuration.WorkerBuildDigest ||
                proof.OwnerId != configuration.Required("OwnerId") || proof.LibraryId != configuration.Required("LibraryId") ||
                proof.VerifiedAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero ||
                proof.VerifiedAtUtc > nowUtc || proof.VerifiedAtUtc < pointer.CopiedAtUtc ||
                Value(item, "pk") != key["pk"].S || Value(item, "sk") != key["sk"].S ||
                Value(item, "payloadSha256") != envelope.PayloadSha256 || Value(item, "checkpointSha256") != proof.CheckpointSha256 ||
                Value(item, "ownerId") != proof.OwnerId || Value(item, "libraryId") != proof.LibraryId ||
                Value(item, "pointId") != proof.PointId.ToString("D") || Value(item, "captureId") != proof.CaptureId.ToString("D") ||
                Value(item, "verifiedAtUtc") != proof.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture) ||
                Value(item, "workerBuildDigest") != proof.RecoveryWorkerBuildDigest)
                throw new InvalidDataException("full_media_proof_binding_invalid");
            return proof.VerifiedAtUtc;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new RecoveryWorkerException("full_media_proof_invalid", exception); }
    }

    internal static async Task PersistAsync(IAmazonDynamoDB dynamo, RecoveryWorkerConfiguration configuration,
        IRecoveryEvidenceSigner signer, RecoverySigningTrust trust, VerifiedCopiedPoint point, CancellationToken ct)
    {
        if (point.FullMediaVerifiedAtUtc is not { } at || point.HistoricalOwnerAccess ||
            at.Offset != TimeSpan.Zero || at > DateTimeOffset.UtcNow || at < point.Pointer.CopiedAtUtc ||
            point.CheckpointEnvelopeSha256 != point.Pointer.CheckpointSha256 ||
            point.Pointer.OwnerId != configuration.Required("OwnerId") || point.Pointer.LibraryId != configuration.Required("LibraryId") ||
            signer.KeyId != trust.KeyId || signer.PublicKeySha256 != trust.PublicKeySha256)
            throw new RecoveryWorkerException("full_media_proof_requires_complete_read");
        var prior = await ReadAsync(dynamo, configuration, point.Pointer, trust, DateTimeOffset.UtcNow, ct);
        if (prior >= at) return;
        var payload = new RecoveryFullMediaProofV1(1, point.Pointer.PointId, point.Pointer.CaptureId,
            point.Pointer.OwnerId, point.Pointer.LibraryId, point.Pointer.CheckpointSha256, at, configuration.WorkerBuildDigest);
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
        var signature = await signer.SignDigestAsync(SHA256.HashData(bytes), ct);
        var envelope = new PrivateSignedCheckpointV1(1, "recovery_full_media_verification", configuration.WorkerId,
            signer.KeyId, signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes), Convert.ToBase64String(signature));
        _ = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        var item = Key(point.Pointer);
        item["ownerId"] = S(payload.OwnerId); item["libraryId"] = S(payload.LibraryId);
        item["pointId"] = S(payload.PointId.ToString("D")); item["captureId"] = S(payload.CaptureId.ToString("D"));
        item["checkpointSha256"] = S(payload.CheckpointSha256); item["payloadSha256"] = S(envelope.PayloadSha256);
        item["envelopeBytes"] = S(Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(envelope)));
        item["verifiedAtUtc"] = S(at.ToString("O", CultureInfo.InvariantCulture));
        item["workerBuildDigest"] = S(payload.RecoveryWorkerBuildDigest);
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = configuration.Required("VerificationTableName"), Item = item,
                ConditionExpression = "attribute_not_exists(pk) OR payloadSha256=:prior",
                ExpressionAttributeValues = new() { [":prior"] = S(prior is null ? "absent" :
                    RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(payload with { VerifiedAtUtc = prior.Value }))) }
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            var latest = await ReadAsync(dynamo, configuration, point.Pointer, trust, DateTimeOffset.UtcNow, ct);
            if (latest is null || latest < at) throw new RecoveryWorkerException("full_media_proof_concurrent_conflict");
        }
    }

    private static Dictionary<string, AttributeValue> Key(CopyPointer pointer) => new()
    { ["pk"] = S("POINT#" + pointer.PointId.ToString("D")), ["sk"] = S("FULL_VERIFY#" + pointer.CheckpointSha256) };
    private static AttributeValue S(string value) => new() { S = value };
    private static string Value(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) ? value.S ?? "" : "";
    private static T ParseCanonical<T>(byte[] bytes) where T : class
    {
        var result = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("full_media_proof_empty");
        if (!RecoveryEvidenceCodec.SerializeCanonical(result).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("full_media_proof_noncanonical");
        return result;
    }
}
