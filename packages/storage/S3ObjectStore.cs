using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Storage;

public sealed class S3ObjectStore(IAmazonS3 s3, string bucket, string kmsKeyId)
{
    public async Task PutAuthoritativeAsync(ContentAddress address, Stream content, CancellationToken cancellationToken = default)
    {
        if (!address.IsValid || !content.CanSeek) throw new ArgumentException("Authoritative content must be seekable and have a canonical address.");
        var position = content.Position;
        await ContentAddress.VerifyAsync(content, address, cancellationToken);
        content.Position = position;
        var request = new PutObjectRequest { BucketName = bucket, Key = address.AuthoritativeKey, InputStream = content, ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId, ChecksumAlgorithm = ChecksumAlgorithm.SHA256, ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(address.Sha256)), IfNoneMatch = "*" };
        await s3.PutObjectAsync(request, cancellationToken);
    }

    public PresignedTransfer CreateStagingUploadUrl(TransferGrant grant, DateTime nowUtc)
    {
        grant.Validate(nowUtc);
        var headers = new Dictionary<string, string> { ["x-amz-checksum-sha256"] = grant.ExpectedChecksumBase64, ["x-amz-server-side-encryption"] = "aws:kms", ["x-amz-server-side-encryption-aws-kms-key-id"] = kmsKeyId, ["x-amz-meta-owner"] = grant.OwnerId };
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = StagingKey(grant.SessionId), Verb = HttpVerb.PUT, Expires = grant.ExpiresAtUtc };
        foreach (var header in headers) request.Headers[header.Key] = header.Value;
        return new(s3.GetPreSignedURL(request), headers);
    }
    public async Task PromoteStagedAsync(Guid session, ContentAddress address, CancellationToken cancellationToken = default)
    {
        var staging = await s3.GetObjectAsync(bucket, StagingKey(session), cancellationToken);
        await using var body = staging.ResponseStream;
        await PutAuthoritativeAsync(address, await CopyToSeekableAsync(body, cancellationToken), cancellationToken);
    }

    private static async Task<MemoryStream> CopyToSeekableAsync(Stream source, CancellationToken cancellationToken)
    {
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        copy.Position = 0;
        return copy;
    }
    public string StagingKey(Guid session) => $"staging/{session:N}";
    public string QuarantineKey(Guid session) => $"quarantine/{session:N}";
}
