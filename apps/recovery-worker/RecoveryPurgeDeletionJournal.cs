using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record PurgeDeletionAdmissionV1(int SchemaVersion, Guid OperationId, string OwnerId,
    string LibraryId, long Generation, string PlanDigest, RecoveryPurgeTargetVersionV1 Target,
    DateTimeOffset AdmittedAtUtc);
internal sealed record PurgeDeletionResultV1(int SchemaVersion, string AdmissionSha256,
    string Outcome, DateTimeOffset ConfirmedAtUtc, string WorkerBuildDigest);

/// <summary>Private immutable deletion receipts. They never grant permission to delete;
/// the caller must independently validate the exact owner approval, fences and holds.</summary>
internal sealed class RecoveryPurgeDeletionJournal(IAmazonDynamoDB dynamo, IAmazonS3 s3,
    string table, string scope, RecoveryPurgePlanV1 plan, string workerBuildDigest)
{
    private readonly string planDigest = plan.Digest();
    private readonly HashSet<string> targetDigests = plan.TargetVersions.Select(item =>
        RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(item))).ToHashSet(StringComparer.Ordinal);
    internal async Task<VerifiedRecoveryPurgeMissingVersion?> ReadMissingAsync(RecoveryPurgeTargetVersionV1 target, CancellationToken ct)
    {
        RequireTarget(target);
        var admissionId = RecordId(target, "ADMISSION");
        var admissionBytes = await ReadAsync(admissionId, ct);
        var resultId = RecordId(target, "RESULT");
        var resultBytes = await ReadAsync(resultId, ct);
        if (admissionBytes is null)
        {
            if (resultBytes is not null) throw new RecoveryWorkerException("purge_journal_admission_missing");
            return null;
        }
        var admission = Parse<PurgeDeletionAdmissionV1>(admissionBytes);
        ValidateAdmission(admission, target);
        if (await ExistsAsync(target, ct))
        {
            if (resultBytes is not null) throw new RecoveryWorkerException("purge_journal_absence_contradicted");
            return null;
        }
        // Reconcile a deletion whose result write or provider response was lost.
        var result = await ConfirmAsync(admissionBytes, admission, target, resultBytes, ct);
        return new(target, admissionId, RecoveryEvidenceCodec.Hash(admissionBytes), resultId,
            RecoveryEvidenceCodec.Hash(result));
    }

    internal async Task DeleteAsync(RecoveryPurgeTargetVersionV1 target, VerifiedRecoveryPurgeApproval approval, CancellationToken ct)
    {
        RequireTarget(target);
        var admissionId = RecordId(target, "ADMISSION");
        var admissionBytes = await ReadAsync(admissionId, ct);
        if (admissionBytes is null)
        {
            RequireDeletionAuthority(approval);
            if (!await ExistsAsync(target, ct)) throw new RecoveryWorkerException("purge_unadmitted_missing_target");
            var created = new PurgeDeletionAdmissionV1(1, plan.OperationId, plan.OwnerId, plan.LibraryId,
                plan.Generation, planDigest, target, DateTimeOffset.UtcNow);
            admissionBytes = await PutImmutableAsync(admissionId, RecoveryEvidenceCodec.SerializeCanonical(created), ct);
        }
        var admission = Parse<PurgeDeletionAdmissionV1>(admissionBytes);
        ValidateAdmission(admission, target);
        var priorResult = await ReadAsync(RecordId(target, "RESULT"), ct);
        if (priorResult is not null)
        {
            ValidateResult(Parse<PurgeDeletionResultV1>(priorResult), admissionBytes, admission);
            if (await ExistsAsync(target, ct)) throw new RecoveryWorkerException("purge_journal_absence_contradicted");
            return;
        }
        if (await ExistsAsync(target, ct))
        {
            RequireDeletionAuthority(approval);
            try
            {
                var response = await s3.DeleteObjectAsync(new DeleteObjectRequest
                { BucketName = target.Bucket, Key = target.Key, VersionId = target.VersionId }, ct);
                if (response.VersionId is not null && response.VersionId != target.VersionId)
                    throw new RecoveryWorkerException("purge_delete_version_mismatch");
            }
            catch (Exception error) when (error is AmazonS3Exception or IOException or HttpRequestException)
            {
                // A lost response is reconciled only by a fresh exact-version absence
                // check. If it remains present, preserve the original failure.
                if (await ExistsAsync(target, ct)) throw;
            }
        }
        if (await ExistsAsync(target, ct)) throw new RecoveryWorkerException("purge_exact_version_still_present");
        _ = await ConfirmAsync(admissionBytes, admission, target, null, ct);
    }

    private async Task<byte[]> ConfirmAsync(byte[] admissionBytes, PurgeDeletionAdmissionV1 admission,
        RecoveryPurgeTargetVersionV1 target, byte[]? prior, CancellationToken ct)
    {
        var result = prior ?? await PutImmutableAsync(RecordId(target, "RESULT"), RecoveryEvidenceCodec.SerializeCanonical(
            new PurgeDeletionResultV1(1, RecoveryEvidenceCodec.Hash(admissionBytes), "exact_version_absent",
                DateTimeOffset.UtcNow, workerBuildDigest)), ct);
        ValidateResult(Parse<PurgeDeletionResultV1>(result), admissionBytes, admission);
        return result;
    }

    private void RequireTarget(RecoveryPurgeTargetVersionV1 target)
    {
        target.Validate();
        var digest = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(target));
        if (target.Kind != "media" || target.RetainedForSharedIdentity || !targetDigests.Contains(digest) ||
            scope != RecoveryFenceScope.MediaScopeId(new(plan.OwnerId, plan.LibraryId, plan.AssetId, plan.EditId), plan.Category, plan.ContentSha256))
            throw new RecoveryWorkerException("purge_journal_target_invalid");
    }

    private void RequireDeletionAuthority(VerifiedRecoveryPurgeApproval approval)
    {
        var now = DateTimeOffset.UtcNow;
        plan.Validate(now);
        RecoveryPurgeRunner.RequireFreshApproval(approval, now);
        var payload = approval.Payload;
        if (payload.OperationId != plan.OperationId || payload.ProposalDigest != plan.ProposalDigest ||
            payload.PurgePlanDigest != planDigest || payload.OwnerId != plan.OwnerId || payload.LibraryId != plan.LibraryId ||
            payload.AssetId != plan.AssetId || payload.EditId != plan.EditId || payload.Category != plan.Category ||
            payload.ContentSha256 != plan.ContentSha256 || payload.Generation != plan.Generation)
            throw new RecoveryWorkerException("purge_approval_binding_invalid");
    }

    private void ValidateAdmission(PurgeDeletionAdmissionV1 admission, RecoveryPurgeTargetVersionV1 target)
    {
        if (admission.SchemaVersion != 1 || admission.OperationId != plan.OperationId || admission.OwnerId != plan.OwnerId ||
            admission.LibraryId != plan.LibraryId || admission.Generation != plan.Generation || admission.PlanDigest != planDigest ||
            !RecoveryEvidenceCodec.SerializeCanonical(admission.Target).AsSpan().SequenceEqual(RecoveryEvidenceCodec.SerializeCanonical(target)) ||
            admission.AdmittedAtUtc.Offset != TimeSpan.Zero || admission.AdmittedAtUtc < plan.PreparedAtUtc ||
            admission.AdmittedAtUtc > DateTimeOffset.UtcNow)
            throw new RecoveryWorkerException("purge_journal_admission_invalid");
    }

    private void ValidateResult(PurgeDeletionResultV1 result, byte[] admissionBytes, PurgeDeletionAdmissionV1 admission)
    {
        if (result.SchemaVersion != 1 || result.AdmissionSha256 != RecoveryEvidenceCodec.Hash(admissionBytes) ||
            result.Outcome != "exact_version_absent" || result.WorkerBuildDigest != workerBuildDigest ||
            result.ConfirmedAtUtc.Offset != TimeSpan.Zero || result.ConfirmedAtUtc < admission.AdmittedAtUtc ||
            result.ConfirmedAtUtc > DateTimeOffset.UtcNow)
            throw new RecoveryWorkerException("purge_journal_result_invalid");
    }

    private string RecordId(RecoveryPurgeTargetVersionV1 target, string kind) =>
        $"DELETE#{plan.OperationId:D}#{planDigest}#{RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(target))}#{kind}";
    private Dictionary<string, AttributeValue> Key(string id) => new()
    { ["scopeId"] = new() { S = scope }, ["recordId"] = new() { S = id } };

    private async Task<byte[]?> ReadAsync(string id, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        { TableName = table, Key = Key(id), ConsistentRead = true }, ct);
        if (response.Item is not { Count: > 0 } item) return null;
        if (!item.TryGetValue("scopeId", out var partition) || partition.S != scope ||
            !item.TryGetValue("recordId", out var record) || record.S != id ||
            !item.TryGetValue("payloadBytes", out var payload) || payload.S is null ||
            Encoding.UTF8.GetByteCount(payload.S) is < 1 or > 64 * 1024)
            throw new RecoveryWorkerException("purge_journal_item_invalid");
        var bytes = Encoding.UTF8.GetBytes(payload.S);
        if (!item.TryGetValue("payloadSha256", out var sha) || sha.S != RecoveryEvidenceCodec.Hash(bytes))
            throw new RecoveryWorkerException("purge_journal_item_invalid");
        return bytes;
    }

    private async Task<byte[]> PutImmutableAsync(string id, byte[] bytes, CancellationToken ct)
    {
        var item = Key(id);
        item["payloadBytes"] = new() { S = Encoding.UTF8.GetString(bytes) };
        item["payloadSha256"] = new() { S = RecoveryEvidenceCodec.Hash(bytes) };
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            { TableName = table, Item = item, ConditionExpression = "attribute_not_exists(scopeId)" }, ct);
            return bytes;
        }
        catch (ConditionalCheckFailedException)
        { return await ReadAsync(id, ct) ?? throw new RecoveryWorkerException("purge_journal_write_unconfirmed"); }
    }

    private Task<bool> ExistsAsync(RecoveryPurgeTargetVersionV1 target, CancellationToken ct) =>
        ExactVersionExistsAsync(s3, target.Bucket, target.Key, target.VersionId, target.ByteLength, ct);

    internal static async Task<bool> ExactVersionExistsAsync(IAmazonS3 s3, string bucket, string key,
        string versionId, long byteLength, CancellationToken ct)
    {
        try
        {
            // HEAD has no structured error body. A bounded GET distinguishes an
            // exact missing version from denied access or a generic missing key.
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = key, VersionId = versionId,
                ByteRange = byteLength > 0 ? new ByteRange(0, 0) : null }, ct);
            var expectedLength = byteLength > 0 ? 1 : 0;
            if (response.VersionId != versionId || response.ContentLength != expectedLength ||
                (byteLength > 0 && response.ContentRange != $"bytes 0-0/{byteLength}"))
                throw new RecoveryWorkerException("purge_exact_version_identity_mismatch");
            var bytes = new byte[2];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await response.ResponseStream.ReadAsync(bytes.AsMemory(read), ct);
                if (count == 0) break;
                read += count;
            }
            if (read != expectedLength) throw new RecoveryWorkerException("purge_exact_version_identity_mismatch");
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound && error.ErrorCode == "NoSuchVersion")
        { return false; }
    }

    private static T Parse<T>(byte[] bytes) where T : class
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (result is null || !RecoveryEvidenceCodec.SerializeCanonical(result).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new RecoveryWorkerException("purge_journal_payload_invalid", error); }
    }
}
