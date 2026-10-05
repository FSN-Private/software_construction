using System.Security.Cryptography;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace Platform.Recovery.Worker;

internal sealed record PublishedS3Object(string Key, string VersionId, string Sha256, long ByteLength);

internal static class S3ArtifactWriter
{
    public static async Task<PublishedS3Object> UploadFileAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        string filePath,
        string? kmsKeyId,
        CancellationToken cancellationToken)
    {
        var (sha256, byteLength) = await HashFileAsync(filePath, cancellationToken);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = filePath,
            AutoCloseStream = true,
            StorageClass = S3StorageClass.Standard
        };
        if (!string.IsNullOrWhiteSpace(kmsKeyId))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
            request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
        }
        using var transfer = new TransferUtility(s3);
        await transfer.UploadAsync(request, cancellationToken);
        return await VerifyUploadedAsync(s3, bucket, key, sha256, byteLength, cancellationToken);
    }

    public static async Task<PublishedS3Object> PutBytesAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        ReadOnlyMemory<byte> bytes,
        string? kmsKeyId,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(bytes.ToArray(), writable: false),
            AutoCloseStream = true,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(hash)),
            IfNoneMatch = "*"
        };
        if (!string.IsNullOrWhiteSpace(kmsKeyId))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
            request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
        }
        string? versionId;
        try
        {
            var response = await s3.PutObjectAsync(request, cancellationToken);
            versionId = response.VersionId;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var existing = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                ChecksumMode = ChecksumMode.ENABLED
            }, cancellationToken);
            versionId = existing.VersionId;
        }
        if (string.IsNullOrWhiteSpace(versionId) || versionId == "null")
            throw new RecoveryWorkerException("artifact_version_missing");
        await VerifyExactAsync(s3, bucket, key, versionId, hash, bytes.Length, cancellationToken);
        return new(key, versionId, hash, bytes.Length);
    }

    public static async Task VerifyExactAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        string versionId,
        string sha256,
        long byteLength,
        CancellationToken cancellationToken)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, cancellationToken);
        using (response)
        {
            if (response.VersionId != versionId || response.ContentLength != byteLength)
                throw new RecoveryWorkerException("artifact_identity_mismatch");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long length = 0;
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                length = checked(length + read);
                if (length > byteLength) throw new RecoveryWorkerException("artifact_length_mismatch");
                hash.AppendData(buffer.AsSpan(0, read));
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != byteLength || actual != sha256) throw new RecoveryWorkerException("artifact_checksum_mismatch");
        }
    }

    private static async Task<PublishedS3Object> VerifyUploadedAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        string sha256,
        long byteLength,
        CancellationToken cancellationToken)
    {
        var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumMode = ChecksumMode.ENABLED
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(metadata.VersionId) || metadata.ContentLength != byteLength)
            throw new RecoveryWorkerException("artifact_identity_mismatch");
        await VerifyExactAsync(s3, bucket, key, metadata.VersionId, sha256, byteLength, cancellationToken);
        return new(key, metadata.VersionId, sha256, byteLength);
    }

    private static async Task<(string Sha256, long ByteLength)> HashFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length = checked(length + read);
            hash.AppendData(buffer.AsSpan(0, read));
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }
}
