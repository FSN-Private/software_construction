using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Storage;

namespace Platform.Recovery.Worker;

internal static class RecoveryMaterializer
{
    internal static async Task<IReadOnlyList<RecoveryMaterializedObjectV1>> MaterializeAsync(
        IAmazonS3 s3, RecoveryWorkerConfiguration configuration, VerifiedCopiedPoint point,
        DynamoFenceIntentStore intentStore, Guid runId,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty) throw new RecoveryWorkerException("materialization_run_id_invalid");
        var targetBucket = configuration.Required("TargetBucket");
        var targetKey = configuration.Required("TargetKmsKeyId");
        var recoveryAccount = configuration.RequiredAccountId("DestinationAccountId");
        if (point.Pointer.RecoveryAccountId != recoveryAccount ||
            targetBucket == configuration.Required("DestinationBucket") ||
            targetBucket == configuration.Required("EvidenceBucket") ||
            targetBucket == configuration.Required("SourceBucket") || targetBucket == point.Pointer.SourceCaptureBucket)
            throw new RecoveryWorkerException("materialization_target_not_isolated");
        var versioning = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = targetBucket, ExpectedBucketOwner = recoveryAccount
        }, cancellationToken);
        if (versioning.VersioningConfig.Status != VersionStatus.Enabled)
            throw new RecoveryWorkerException("materialization_target_versioning_required");
        var location = await s3.GetBucketLocationAsync(new GetBucketLocationRequest
        {
            BucketName = targetBucket, ExpectedBucketOwner = recoveryAccount
        }, cancellationToken);
        if (location.Location.Value != configuration.RequiredRegion("DestinationRegion"))
            throw new RecoveryWorkerException("materialization_target_region_mismatch");

        var materialized = new List<RecoveryMaterializedObjectV1>();
        var targets = new Dictionary<string, PublishedS3Object>(StringComparer.Ordinal);
        var admissions = new Dictionary<RecoveryObjectReference, List<LogicalWriteAdmission>>();
        // Admit every logical reference before the first target write. Purge reads the
        // same media scopes, so an admitted or uncertain drill write prevents completion.
        foreach (var reference in point.SourceManifest.Objects)
        {
            reference.Validate();
            if (reference.LogicalReferences.Count == 0)
                throw new RecoveryWorkerException("materialization_logical_reference_missing");
            var physicalId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                runId.ToString("D") + "\0" + targetBucket + "\0" + reference.Sha256)).AsSpan(0, 16));
            var intents = new List<LogicalWriteAdmission>();
            foreach (var logical in reference.LogicalReferences)
                intents.Add(await intentStore.AdmitMediaAsync(runId, reference,
                    configuration.Required("SourceBucket"), targetBucket, "sha256/" + reference.Sha256,
                    logical, physicalId, configuration.WorkerId, DateTimeOffset.UtcNow, cancellationToken));
            admissions.Add(reference, intents);
        }
        foreach (var admission in admissions.Values.SelectMany(items => items).Where(item => item.State != "settled"))
            await intentStore.SetStoragePendingAsync(admission, cancellationToken);
        foreach (var reference in point.SourceManifest.Objects)
        {
            var source = point.Receipts.SingleOrDefault(receipt => receipt.SourceKey == reference.SourceKey &&
                receipt.SourceVersionId == reference.SourceVersionId && receipt.Sha256 == reference.Sha256 &&
                receipt.ByteLength == reference.ByteLength && receipt.DataClass == reference.DataClass.ToString())
                ?? throw new RecoveryWorkerException("materialization_source_receipt_missing");
            if (source.DestinationBucket != configuration.Required("DestinationBucket") ||
                !source.DestinationKey.StartsWith("payload/sha256/", StringComparison.Ordinal) ||
                source.ByteLength > S3ObjectStore.MaxObjectSize)
                throw new RecoveryWorkerException("materialization_source_binding_invalid");
            if (!targets.TryGetValue(source.Sha256, out var target))
            {
                var directory = Path.Combine(Path.GetTempPath(), "recovery-materialize", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var path = Path.Combine(directory, "payload");
                    await RecoveryDrillRunner.DownloadExactFileAsync(s3, source, path, cancellationToken);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var settledVersions = admissions[reference].Where(item => item.State == "settled")
                        .Select(item => item.DestinationVersionId).Distinct(StringComparer.Ordinal).ToArray();
                    if (settledVersions.Length > 1)
                        throw new RecoveryWorkerException("materialization_settled_version_conflict");
                    string? version = settledVersions.SingleOrDefault();
                    var key = "sha256/" + source.Sha256;
                    if (settledVersions.Length == 0) try
                    {
                        var response = await s3.PutObjectAsync(new PutObjectRequest
                        {
                            BucketName = targetBucket, ExpectedBucketOwner = recoveryAccount, Key = key,
                            InputStream = stream, AutoCloseStream = false, IfNoneMatch = "*",
                            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
                            ServerSideEncryptionKeyManagementServiceKeyId = targetKey,
                            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(source.Sha256))
                        }, cancellationToken);
                        version = response.VersionId;
                    }
                    catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        var existing = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
                        {
                            BucketName = targetBucket, ExpectedBucketOwner = recoveryAccount, Key = key
                        }, cancellationToken);
                        version = existing.VersionId;
                    }
                    if (string.IsNullOrWhiteSpace(version) || version == "null")
                        throw new RecoveryWorkerException("materialization_target_version_missing");
                    await S3ArtifactWriter.VerifyExactAsync(s3, targetBucket, key, version,
                        source.Sha256, source.ByteLength, cancellationToken);
                    target = new(key, version, source.Sha256, source.ByteLength);
                    targets.Add(source.Sha256, target);
                }
                catch
                {
                    foreach (var admission in admissions[reference].Where(item => item.State != "settled"))
                        await intentStore.SetUnknownAsync(admission, CancellationToken.None);
                    throw;
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
            if (target.ByteLength != source.ByteLength)
                throw new RecoveryWorkerException("materialization_digest_length_conflict");
            foreach (var admission in admissions[reference])
            {
                if (admission.State == "settled")
                {
                    if (admission.DestinationVersionId != target.VersionId)
                        throw new RecoveryWorkerException("materialization_settled_version_conflict");
                }
                else await intentStore.SetSettledAsync(admission, target.VersionId,
                    RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
                    {
                        targetBucket, target.Key, target.VersionId, target.Sha256, target.ByteLength
                    })), cancellationToken);
            }
            materialized.Add(new(source.SourceBucket, source.SourceKey, source.SourceVersionId, source.Sha256,
                source.ByteLength, targetBucket, target.Key, target.VersionId));
        }
        return materialized;
    }
}
