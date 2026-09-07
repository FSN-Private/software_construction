using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Storage;

public sealed class S3ObjectStore(IAmazonS3 s3, string bucket, string kmsKeyId)
{
    public async Task PutAuthoritativeAsync(ContentAddress address, Stream content, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest { BucketName = bucket, Key = address.AuthoritativeKey, InputStream = content, ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId, ChecksumAlgorithm = ChecksumAlgorithm.SHA256 };
        await s3.PutObjectAsync(request, cancellationToken);
    }

    public string CreateDownloadUrl(ContentAddress address, DateTime expiresAtUtc) =>
        s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = bucket, Key = address.AuthoritativeKey, Verb = HttpVerb.GET, Expires = expiresAtUtc });

    public string CreateStagingUploadUrl(Guid session, DateTime expiresAtUtc) =>
        s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = bucket, Key = StagingKey(session), Verb = HttpVerb.PUT, Expires = expiresAtUtc });
    public string StagingKey(Guid session) => $"staging/{session:N}";
    public string QuarantineKey(Guid session) => $"quarantine/{session:N}";
}
