using System.Security.Cryptography;
using System.Xml;
using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Recovery.Worker;

internal static class RecoveryXmpContentVerifier
{
    private const long MaximumXmpBytes = 16L * 1024 * 1024;
    private const string DarktableNamespace = "http://darktable.sf.net/";

    internal static async Task VerifyExactAsync(IAmazonS3 s3, PrivateCopyReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.DataClass != "XmpSidecar" || receipt.ByteLength is <= 0 or > MaximumXmpBytes)
            throw new RecoveryWorkerException("recovery_xmp_size_or_class_invalid");
        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = receipt.DestinationBucket,
            Key = receipt.DestinationKey,
            VersionId = receipt.DestinationVersionId
        }, cancellationToken);
        if (response.VersionId != receipt.DestinationVersionId || response.ContentLength != receipt.ByteLength)
            throw new RecoveryWorkerException("recovery_xmp_version_mismatch");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var content = new HashingReadStream(response.ResponseStream, hash, receipt.ByteLength);
            await ValidateXmpXmlAsync(content, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            while (await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0) { }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (content.BytesRead != receipt.ByteLength || digest != receipt.Sha256)
                throw new RecoveryWorkerException("recovery_xmp_checksum_mismatch");
        }
        catch (IOException exception)
        {
            throw new RecoveryWorkerException("recovery_xmp_read_failed", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("recovery_xmp_read_limit_exceeded", exception);
        }
    }

    internal static async Task ValidateXmpXmlAsync(Stream stream, CancellationToken cancellationToken)
    {
        var rootSeen = false;
        var supportedNamespaceSeen = false;
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CloseInput = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmpBytes
            };
            using var reader = XmlReader.Create(stream, settings);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth == 0) rootSeen = true;
                if (reader.NamespaceURI == DarktableNamespace) supportedNamespaceSeen = true;
                if (!reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                    if (reader.NamespaceURI == DarktableNamespace) supportedNamespaceSeen = true;
                reader.MoveToElement();
            }
        }
        catch (XmlException exception) when (ContainsException<InvalidDataException>(exception))
        {
            throw new RecoveryWorkerException("recovery_xmp_read_limit_exceeded", exception);
        }
        catch (XmlException exception) when (ContainsException<IOException>(exception))
        {
            throw new RecoveryWorkerException("recovery_xmp_read_failed", exception);
        }
        catch (XmlException exception)
        {
            throw new RecoveryWorkerException("recovery_xmp_malformed", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("recovery_xmp_read_limit_exceeded", exception);
        }
        if (!rootSeen || !supportedNamespaceSeen)
            throw new RecoveryWorkerException("recovery_xmp_unsupported_format");
    }

    private static bool ContainsException<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TException) return true;
        return false;
    }

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash, long maximumBytes) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) TrackBytes(buffer[..read]);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) TrackBytes(buffer.Span[..read]);
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            if (read > 0) TrackBytes(buffer.AsSpan(offset, read));
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Track(int read, Span<byte> buffer)
        {
            if (read > 0) TrackBytes(buffer[..read]);
            return read;
        }

        private void TrackBytes(ReadOnlySpan<byte> bytes)
        {
            BytesRead = checked(BytesRead + bytes.Length);
            if (BytesRead > maximumBytes) throw new InvalidDataException("xmp_size_limit");
            hash.AppendData(bytes);
        }
    }
}
