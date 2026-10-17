using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Contracts.Rendering;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Platform.Render.Worker;

public sealed class S3RenderSourceResolver(IAmazonS3 s3, string bucket) : IImmutableRenderSourceResolver
{
    public async Task<ResolvedRenderInput?> ResolveAsync(RenderJob job, RenderWorkspace workspace, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = job.Message ?? throw new ArgumentException("invalid_render_job");
            var original = await DownloadAsync(message.Original, workspace.PrivatePath("original"), cancellationToken);
            var xmp = await DownloadAsync(message.Xmp, workspace.PrivatePath("edit.xmp"), cancellationToken);
            return new(original, xmp, message.Original.Sha256, message.Xmp.Sha256);
        }
        catch (Exception error) when (error is AmazonS3Exception or InvalidDataException) { return null; }
    }

    private async Task<string> DownloadAsync(RenderInputObject source, string destination, CancellationToken cancellationToken)
    {
        source.Validate();
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = source.Key, VersionId = source.Version }, cancellationToken);
        if (response.ContentLength != source.ByteLength) throw new InvalidDataException("source_unavailable");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken)).ToLowerInvariant();
        if (hash != source.Sha256) throw new InvalidDataException("source_unavailable");
        return destination;
    }
}

public sealed class S3PrivateDerivativeStore(IAmazonS3 s3, string bucket) : IPrivateDerivativeStore
{
    public async Task<PublishedDerivative> PublishAsync(RenderJob job, string outputPath, CancellationToken cancellationToken = default)
    {
        var message = job.Message ?? throw new ArgumentException("invalid_render_job");
        var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        try
        {
            await using var input = new MemoryStream(bytes, writable: false);
            var result = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = message.DerivativeKey,
                InputStream = input,
                ContentType = "image/jpeg",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)),
                IfNoneMatch = "*",
            }, cancellationToken);
            return new(message.DerivativeKey, result.VersionId ?? throw new InvalidDataException("storage_unavailable"), sha256, bytes.LongLength, "image/jpeg");
        }
        catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            var existing = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = message.DerivativeKey, ChecksumMode = ChecksumMode.ENABLED }, cancellationToken);
            if (existing.VersionId is null || existing.ContentLength != bytes.LongLength || existing.ChecksumSHA256 != Convert.ToBase64String(Convert.FromHexString(sha256))) throw new InvalidDataException("storage_unavailable");
            return new(message.DerivativeKey, existing.VersionId, sha256, bytes.LongLength, "image/jpeg");
        }
    }
}

public sealed class DarktableCommandFactory : IRendererCommandFactory
{
    public RendererCommand Create(RenderJob job, ResolvedRenderInput input, RenderWorkspace workspace)
    {
        var width = job.Request.Rendition == RenditionKind.Thumbnail512 ? "512" : "2048";
        return new("/opt/darktable/bin/darktable-cli", [input.OriginalPath, input.XmpPath, workspace.PrivatePath("output.jpg"), "--width", width, "--height", width, "--library", ":memory:"], workspace.Path);
    }
}

public sealed class SqsReceiptQueue(IAmazonSQS sqs, string queueUrl, string receiptHandle) : IRenderQueue
{
    public Task CompleteAsync(Guid attemptId, CancellationToken cancellationToken = default) => sqs.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
    public Task FailAsync(Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default) => sqs.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
}

public sealed class SigV4RenderCallbackClient(string callbackBaseUrl, string region) : IRenderWorkerCallback
{
    private static readonly HttpClient Http = new();
    private readonly Uri baseUri = new(callbackBaseUrl, UriKind.Absolute);
    private readonly AmazonSQSConfig config = new() { AuthenticationRegion = region, AuthenticationServiceName = "execute-api", RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
    private readonly DefaultAWSCredentialsIdentityResolver credentials = new();

    public async Task<bool> ClaimAsync(Guid requestId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("claim", new { requestId, attemptId }, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public Task CompleteAsync(Guid requestId, Guid attemptId, string privateObjectKey, string privateObjectVersion, string outputSha256, long byteLength, string mediaType, RenderProvenance provenance, CancellationToken cancellationToken = default) =>
        SendRequiredAsync("complete", new { requestId, attemptId, publication = new { objectKey = privateObjectKey, objectVersion = privateObjectVersion, outputSha256, byteLength, mediaType }, provenance }, cancellationToken);

    public Task FailAsync(Guid requestId, Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default) =>
        SendRequiredAsync("fail", new { requestId, attemptId, safeFailureCode }, cancellationToken);

    private async Task SendRequiredAsync(string route, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(route, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(string route, object body, CancellationToken cancellationToken)
    {
        var target = new Uri(baseUri, $"internal/render-callbacks/{route}");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        var request = new DefaultRequest(new SendMessageRequest(), "execute-api")
        {
            Endpoint = target,
            HttpMethod = "POST",
            ResourcePath = target.AbsolutePath,
            ContentStream = new MemoryStream(bytes, writable: false),
        };
        request.Headers["Content-Type"] = "application/json";
        request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        var identity = credentials.ResolveIdentity(config, cancellationToken);
        new AWS4Signer().Sign(request, config, new RequestMetrics(), identity);
        using var message = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(bytes) };
        foreach (var header in request.Headers)
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value)) message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await Http.SendAsync(message, cancellationToken);
    }
}
