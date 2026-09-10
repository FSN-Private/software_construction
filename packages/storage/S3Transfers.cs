using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace Platform.Storage;

public sealed partial class S3ObjectStore
{
    public const int PartSize = 8 * 1024 * 1024;
    public const long MaxObjectSize = 1024L * 1024 * 1024;

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        var versioning = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, ct);
        if (versioning.VersioningConfig.Status != VersionStatus.Enabled) throw new InvalidOperationException("storage_versioning_required");
    }

    public async Task<string> StartUploadAsync(Guid session, CancellationToken ct)
    {
        var result = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = StagingKey(session),
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId
        }, ct);
        return result.UploadId;
    }

    public async Task<string> UploadPartAsync(Guid session, string uploadId, int number, Stream bytes, CancellationToken ct)
    {
        var result = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket, Key = StagingKey(session), UploadId = uploadId,
            PartNumber = number, InputStream = bytes, PartSize = bytes.Length
        }, ct);
        return result.ETag;
    }

    public async Task<string> CompleteUploadAsync(Guid session, string uploadId, IReadOnlyList<ReceivedPart> parts, CancellationToken ct)
    {
        try
        {
            var result = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = StagingKey(session), UploadId = uploadId,
                PartETags = parts.OrderBy(p => p.Number).Select(p => new PartETag(p.Number, p.ETag)).ToList()
            }, ct);
            return result.VersionId ?? throw new InvalidDataException("staging_version_missing");
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload")
        {
            // Completion may have reached S3 immediately before a database rollback/restart.
            var result = await s3.GetObjectMetadataAsync(bucket, StagingKey(session), ct);
            return result.VersionId ?? throw new InvalidDataException("staging_version_missing");
        }
    }

    public async Task AbortUploadAsync(Guid session, string uploadId, CancellationToken ct)
    {
        try { await s3.AbortMultipartUploadAsync(bucket, StagingKey(session), uploadId, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload") { }
    }

    public async Task<FileStream> ReadVerifiedAsync(string key, string version, ContentAddress address, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = version }, ct);
        if (response.ContentLength != address.Length) throw new InvalidDataException("checksum_mismatch");
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"ingest-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920]; long received = 0; int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > address.Length) throw new InvalidDataException("checksum_mismatch");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            file.Position = 0;
            await ContentAddress.VerifyAsync(file, address, ct);
            file.Position = 0;
            return file;
        }
        catch { await file.DisposeAsync(); throw; }
    }

    public async Task<string> PublishVerifiedAsync(ContentAddress address, Stream bytes, CancellationToken ct)
    {
        await PutAuthoritativeAsync(address, bytes, ct);
        return await VerifyAuthoritativeAsync(address, ct);
    }

    public async Task<string> QuarantineAsync(Guid session, string version, CancellationToken ct)
    {
        var result = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = StagingKey(session), SourceVersionId = version,
            DestinationBucket = bucket, DestinationKey = QuarantineKey(session),
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId
        }, ct);
        return result.VersionId ?? throw new InvalidDataException("quarantine_version_missing");
    }

    public async Task DeleteVersionAsync(string key, string version, CancellationToken ct) =>
        _ = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = version }, ct);

    public async Task DeleteStagingAsync(Guid session, string? uploadId, CancellationToken ct)
    {
        if (uploadId is not null) await AbortUploadAsync(session, uploadId, ct);
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(bucket, StagingKey(session), ct);
            if (metadata.VersionId is not null) await DeleteVersionAsync(StagingKey(session), metadata.VersionId, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { }
    }

    public async Task<IReadOnlyList<string>> InventoryAsync(CancellationToken ct)
    {
        var keys = new List<string>(); string? next = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "sha256/", ContinuationToken = next }, ct);
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            next = page.NextContinuationToken;
        } while (next is not null);
        return keys;
    }
}

public sealed record ReceivedPart(int Number, string ETag, string Sha256, long Length);
