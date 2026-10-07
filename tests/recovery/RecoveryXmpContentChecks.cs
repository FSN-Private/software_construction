using System.Text;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryXmpContentChecks
{
    public static async Task RunAsync()
    {
        var valid = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:dt='http://darktable.sf.net/'><dt:rating>3</dt:rating></x:xmpmeta>");
        await RecoveryXmpContentVerifier.ValidateXmpXmlAsync(new MemoryStream(valid), CancellationToken.None);

        await Rejects("recovery_xmp_malformed", Encoding.UTF8.GetBytes(
            "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x:xmpmeta xmlns:x='adobe:ns:meta/'>&e;</x:xmpmeta>"));
        await Rejects("recovery_xmp_malformed", Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'>"));
        await Rejects("recovery_xmp_unsupported_format", Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"));

        await VerifyExactAsync(valid);
        await RejectExactAsync("recovery_xmp_version_mismatch", valid, returnedVersion: "other-version");
        await RejectExactAsync("recovery_xmp_checksum_mismatch", valid,
            expectedSha256: new string('0', 64));

        var dtd = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:dt='http://darktable.sf.net/'>&e;</x:xmpmeta>");
        await RejectExactAsync("recovery_xmp_malformed", dtd);

        var truncated = valid[..^8];
        await RejectExactAsync("recovery_xmp_malformed", valid, body: truncated);
        await RejectExactAsync("recovery_xmp_read_failed", valid, failAfter: valid.Length / 2);

        await RejectExactAsync("recovery_xmp_size_or_class_invalid", valid,
            declaredLength: 16L * 1024 * 1024 + 1);

        // The object response claims the signed length but streams one extra byte.
        // The verifier must stop at the admitted byte limit while draining the stream.
        await RejectExactAsync("recovery_xmp_read_limit_exceeded", valid, body: [.. valid, (byte)' ']);
    }

    private static async Task Rejects(string code, byte[] bytes)
    {
        try
        {
            await RecoveryXmpContentVerifier.ValidateXmpXmlAsync(new MemoryStream(bytes), CancellationToken.None);
        }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected XMP rejection: {code}.");
    }

    private static async Task VerifyExactAsync(byte[] bytes)
    {
        var proxy = CreateS3(bytes, "v1", bytes.LongLength);
        await RecoveryXmpContentVerifier.VerifyExactAsync(proxy.Client, Receipt(bytes), CancellationToken.None);
    }

    private static async Task RejectExactAsync(string code, byte[] bytes, string? returnedVersion = null,
        string? expectedSha256 = null, byte[]? body = null, int? failAfter = null, long? declaredLength = null)
    {
        var actualBody = body ?? bytes;
        var proxy = CreateS3(actualBody, returnedVersion ?? "v1", declaredLength ?? bytes.LongLength, failAfter);
        var receipt = Receipt(bytes) with
        {
            Sha256 = expectedSha256 ?? RecoveryEvidenceCodec.Hash(bytes),
            ByteLength = declaredLength ?? bytes.LongLength
        };
        try
        {
            await RecoveryXmpContentVerifier.VerifyExactAsync(proxy.Client, receipt, CancellationToken.None);
        }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exact XMP rejection: {code}.");
    }

    private static (IAmazonS3 Client, XmpS3Proxy Proxy) CreateS3(byte[] bytes, string version,
        long contentLength, int? failAfter = null)
    {
        var client = DispatchProxy.Create<IAmazonS3, XmpS3Proxy>();
        var proxy = (XmpS3Proxy)(object)client;
        proxy.Configure(bytes, version, contentLength, failAfter);
        return (client, proxy);
    }

    private static PrivateCopyReceiptV1 Receipt(byte[] bytes) => new(
        "source", "source/xmp", "source-v1", RecoveryEvidenceCodec.Hash(bytes), bytes.LongLength,
        "destination", "payload/sha256/test", "v1", "XmpSidecar", DateTimeOffset.UtcNow);

    private class XmpS3Proxy : DispatchProxy
    {
        private byte[] bytes = [];
        private string version = string.Empty;
        private long contentLength;
        private int? failAfter;

        public void Configure(byte[] body, string versionId, long declaredLength, int? readFailureOffset)
        {
            bytes = body;
            version = versionId;
            contentLength = declaredLength;
            failAfter = readFailureOffset;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IAmazonS3.GetObjectAsync) || args is not [GetObjectRequest, CancellationToken])
                throw new NotSupportedException(targetMethod?.Name);
            var stream = failAfter is { } offset
                ? new InterruptedReadStream(bytes, offset)
                : new MemoryStream(bytes, writable: false);
            return Task.FromResult(new GetObjectResponse
            {
                VersionId = version,
                ContentLength = contentLength,
                ResponseStream = stream
            });
        }
    }

    private sealed class InterruptedReadStream(byte[] content, int failAfter) : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter) return ValueTask.FromException<int>(new IOException("fixture interrupted stream"));
            var allowed = (int)Math.Min(buffer.Length, failAfter - Position);
            return base.ReadAsync(buffer[..allowed], cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Position >= failAfter) return Task.FromException<int>(new IOException("fixture interrupted stream"));
            var allowed = (int)Math.Min(count, failAfter - Position);
            return base.ReadAsync(buffer, offset, allowed, cancellationToken);
        }
    }
}
