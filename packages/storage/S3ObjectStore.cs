using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace Platform.Storage;

public sealed partial class S3ObjectStore(IAmazonS3 s3, string bucket, string kmsKeyId)
{
    public async Task PutAuthoritativeAsync(ContentAddress address, Stream content, CancellationToken cancellationToken = default)
    {
        if (!address.IsValid || !content.CanSeek) throw new ArgumentException("Authoritative content must be seekable and have a canonical address.");
        var position = content.Position;
        await ContentAddress.VerifyAsync(content, address, cancellationToken);
        content.Position = position;
        var request = new PutObjectRequest { BucketName = bucket, Key = address.AuthoritativeKey, InputStream = content, ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId, ChecksumAlgorithm = ChecksumAlgorithm.SHA256, ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(address.Sha256)), IfNoneMatch = "*" };
        try { await s3.PutObjectAsync(request, cancellationToken); }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            await VerifyAuthoritativeAsync(address, cancellationToken);
        }
    }

    public PresignedTransfer CreateStagingUploadUrl(TransferGrant grant, DateTime nowUtc)
    {
        grant.Validate(nowUtc);
        var headers = new Dictionary<string, string> { ["content-length"] = grant.ExpectedContent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["x-amz-checksum-sha256"] = grant.ExpectedChecksumBase64, ["x-amz-server-side-encryption"] = "aws:kms", ["x-amz-server-side-encryption-aws-kms-key-id"] = kmsKeyId, ["x-amz-meta-owner"] = grant.OwnerId };
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = StagingKey(grant.SessionId), Verb = HttpVerb.PUT, Expires = grant.ExpiresAtUtc };
        foreach (var header in headers) request.Headers[header.Key] = header.Value;
        return new(s3.GetPreSignedURL(request), headers);
    }

    public PresignedTransfer CreateAuthoritativeDownloadUrl(ContentAddress address, DateTime expiresAtUtc, DateTime nowUtc)
    {
        if (!address.IsValid || expiresAtUtc <= nowUtc || expiresAtUtc > nowUtc.AddMinutes(15)) throw new ArgumentException("invalid_download_grant");
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = address.AuthoritativeKey, Verb = HttpVerb.GET, Expires = expiresAtUtc };
        return new(s3.GetPreSignedURL(request), new Dictionary<string, string> { ["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(address.Sha256)) });
    }
    public async Task<string> PromoteStagedAsync(Guid session, ContentAddress address, CancellationToken cancellationToken = default)
    {
        if (!address.IsValid) throw new ArgumentException("Authoritative content must have a canonical address.", nameof(address));
        var staged = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = StagingKey(session), ChecksumMode = ChecksumMode.ENABLED }, cancellationToken);
        if (staged.ContentLength != address.Length || staged.ChecksumSHA256 != Convert.ToBase64String(Convert.FromHexString(address.Sha256))) throw new InvalidDataException("checksum_mismatch");
        if (string.IsNullOrWhiteSpace(staged.VersionId)) throw new InvalidDataException("staging_version_missing");
        try
        {
            var copied = await s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket, SourceKey = StagingKey(session), SourceVersionId = staged.VersionId, DestinationBucket = bucket, DestinationKey = address.AuthoritativeKey,
                IfNoneMatch = "*", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
                ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId, ChecksumAlgorithm = ChecksumAlgorithm.SHA256
            }, cancellationToken);
            if (string.IsNullOrWhiteSpace(copied.VersionId) || !string.IsNullOrEmpty(copied.ChecksumSHA256) && copied.ChecksumSHA256 != Convert.ToBase64String(Convert.FromHexString(address.Sha256))) throw new InvalidDataException("authoritative_content_mismatch");
            return copied.VersionId;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return await VerifyAuthoritativeAsync(address, cancellationToken);
        }
    }

    private async Task<string> VerifyAuthoritativeAsync(ContentAddress address, CancellationToken cancellationToken)
    {
        var existing = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = address.AuthoritativeKey, ChecksumMode = ChecksumMode.ENABLED }, cancellationToken);
        if (existing.ContentLength != address.Length || existing.ChecksumSHA256 != Convert.ToBase64String(Convert.FromHexString(address.Sha256))) throw new InvalidDataException("authoritative_content_mismatch");
        if (string.IsNullOrWhiteSpace(existing.VersionId)) throw new InvalidDataException("authoritative_version_missing");
        return existing.VersionId;
    }
    public string StagingKey(Guid session) => $"staging/{session:N}";
    public string QuarantineKey(Guid session) => $"quarantine/{session:N}";
}
