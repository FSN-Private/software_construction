using Amazon.S3;
using Amazon.S3.Model;
using Platform.Contracts.Rendering;

namespace Platform.Api.Features.Rendering;

public interface IPrivateDerivativeLocator
{
    string Locate(RenderPublication publication);
}

public sealed class S3PrivateDerivativeLocator(IAmazonS3 s3, IConfiguration configuration) : IPrivateDerivativeLocator
{
    private readonly string bucket = configuration["Rendering:DerivativeBucket"] ?? throw new InvalidOperationException("Rendering:DerivativeBucket is required.");

    public string Locate(RenderPublication publication)
    {
        publication.Validate();
        return s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = bucket, Key = publication.ObjectKey, VersionId = publication.ObjectVersion, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.AddMinutes(5) });
    }
}
