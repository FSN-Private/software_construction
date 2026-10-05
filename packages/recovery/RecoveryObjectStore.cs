using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Storage;

namespace Platform.Recovery;

/// <summary>
/// Copies an exact source version into the recovery account and verifies the exact
/// destination version by reading and hashing its bytes. A caller must durably admit
/// the write intent before calling this adapter.
/// </summary>
public sealed class RecoveryObjectStore(
    IAmazonS3 sourceS3,
    IAmazonS3 recoveryS3,
    string sourceBucket,
    string recoveryBucket,
    string recoveryAccountAlias,
    string recoveryRegion,
    string recoveryKmsKeyId,
    string verifierBuild,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim readinessLock = new(1, 1);
    private DateTimeOffset readinessExpiresAtUtc;

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        if (readinessExpiresAtUtc > clock.GetUtcNow()) return;
        await readinessLock.WaitAsync(cancellationToken);
        try
        {
            if (readinessExpiresAtUtc > clock.GetUtcNow()) return;
            var versioning = await recoveryS3.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = recoveryBucket }, cancellationToken);
            if (versioning.VersioningConfig.Status != VersionStatus.Enabled)
                throw new InvalidOperationException("recovery_bucket_versioning_required");

            var objectLock = await recoveryS3.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = recoveryBucket }, cancellationToken);
            var rule = objectLock.ObjectLockConfiguration?.Rule?.DefaultRetention;
            if (objectLock.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled ||
                rule is null || (rule.Days ?? 0) < 90 && (rule.Years ?? 0) < 1 ||
                rule.Mode != ObjectLockRetentionMode.Governance && rule.Mode != ObjectLockRetentionMode.Compliance)
                throw new InvalidOperationException("recovery_bucket_retention_policy_required");

            var publicAccess = await recoveryS3.GetPublicAccessBlockAsync(
                new GetPublicAccessBlockRequest { BucketName = recoveryBucket }, cancellationToken);
            var block = publicAccess.PublicAccessBlockConfiguration;
            if (block?.BlockPublicAcls != true || block.IgnorePublicAcls != true ||
                block.BlockPublicPolicy != true || block.RestrictPublicBuckets != true)
                throw new InvalidOperationException("recovery_bucket_public_access_block_required");
            readinessExpiresAtUtc = clock.GetUtcNow().AddMinutes(5);
        }
        finally { readinessLock.Release(); }
    }

    public async Task<RecoveryCopyReceipt> CopyVerifiedAsync(
        RecoveryWriteIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        if (intent.State != RecoveryWriteState.StoragePending)
            throw new InvalidOperationException("recovery_write_intent_not_pending");
        if (intent.DestinationVersionId is not null)
            throw new InvalidOperationException("recovery_write_intent_already_settled");

        await CheckReadyAsync(cancellationToken);
        var expected = new ContentAddress(intent.Source.Sha256, intent.Source.ByteLength);
        await using var staged = await ReadToVerifiedTempFileAsync(intent.Source, expected, cancellationToken);

        string destinationVersion;
        try
        {
            var put = new PutObjectRequest
            {
                BucketName = recoveryBucket,
                Key = intent.DestinationKey,
                InputStream = staged,
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
                ServerSideEncryptionKeyManagementServiceKeyId = recoveryKmsKeyId,
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(expected.Sha256)),
                IfNoneMatch = "*"
            };
            staged.Position = 0;
            var result = await recoveryS3.PutObjectAsync(put, cancellationToken);
            destinationVersion = result.VersionId ?? throw new InvalidDataException("recovery_version_missing");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // A prior attempt may have completed its provider write before losing its
            // database settlement. Reuse only the exact current immutable version
            // after fetching and hashing it below.
            var existing = await recoveryS3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = recoveryBucket,
                Key = intent.DestinationKey,
                ChecksumMode = ChecksumMode.ENABLED
            }, cancellationToken);
            destinationVersion = existing.VersionId ?? throw new InvalidDataException("recovery_version_missing");
        }

        await VerifyExactDestinationAsync(intent.DestinationKey, destinationVersion, expected, cancellationToken);
        var now = clock.GetUtcNow();
        var retention = await recoveryS3.GetObjectRetentionAsync(new GetObjectRetentionRequest
        {
            BucketName = recoveryBucket,
            Key = intent.DestinationKey,
            VersionId = destinationVersion
        }, cancellationToken);
        var retentionUntil = retention.Retention?.RetainUntilDate;
        if (retention.Retention?.Mode != ObjectLockRetentionMode.Governance && retention.Retention?.Mode != ObjectLockRetentionMode.Compliance ||
            retentionUntil is null || retentionUntil.Value.ToUniversalTime() < intent.AdmittedAtUtc.AddDays(90))
            throw new InvalidDataException("recovery_object_retention_unverified");

        var receipt = new RecoveryCopyReceipt(
            intent.Source,
            recoveryBucket,
            intent.DestinationKey,
            destinationVersion,
            recoveryAccountAlias,
            recoveryRegion,
            intent.AdmittedAtUtc,
            now,
            verifierBuild,
            RetentionPolicyVerified: true,
            AccessPolicyVerified: true);
        receipt.Validate();
        return receipt;
    }

    public async Task<Stream> ReadVerifiedAsync(
        RecoveryCopyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (receipt.DestinationBucket != recoveryBucket)
            throw new InvalidDataException("recovery_destination_bucket_mismatch");
        await CheckReadyAsync(cancellationToken);
        await VerifyRetentionAtAsync(receipt.DestinationKey, receipt.DestinationVersionId, receipt.RetentionProtectedFromUtc, cancellationToken);

        var response = await recoveryS3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = recoveryBucket,
            Key = receipt.DestinationKey,
            VersionId = receipt.DestinationVersionId
        }, cancellationToken);
        try
        {
            if (response.VersionId != receipt.DestinationVersionId || response.ContentLength != receipt.Source.ByteLength)
                throw new InvalidDataException("recovery_destination_identity_mismatch");
            var file = CreateTempFile("recovery-read");
            try
            {
                await CopyAndHashAsync(response.ResponseStream, file, new ContentAddress(receipt.Source.Sha256, receipt.Source.ByteLength), cancellationToken);
                file.Position = 0;
                return new OwnedResponseStream(file, response);
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<FileStream> ReadToVerifiedTempFileAsync(
        RecoveryObjectReference source,
        ContentAddress expected,
        CancellationToken cancellationToken)
    {
        var response = await sourceS3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = sourceBucket,
            Key = source.SourceKey,
            VersionId = source.SourceVersionId
        }, cancellationToken);
        using (response)
        {
            if (response.ContentLength != expected.Length) throw new InvalidDataException("recovery_source_length_mismatch");
            var file = CreateTempFile("recovery-copy");
            try
            {
                await CopyAndHashAsync(response.ResponseStream, file, expected, cancellationToken);
                file.Position = 0;
                return file;
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
        }
    }

    private async Task VerifyExactDestinationAsync(
        string key,
        string version,
        ContentAddress expected,
        CancellationToken cancellationToken)
    {
        var response = await recoveryS3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = recoveryBucket,
            Key = key,
            VersionId = version
        }, cancellationToken);
        using (response)
        {
            if (response.VersionId != version || response.ContentLength != expected.Length)
                throw new InvalidDataException("recovery_destination_identity_mismatch");
            await using var scratch = CreateTempFile("recovery-verify");
            await CopyAndHashAsync(response.ResponseStream, scratch, expected, cancellationToken);
        }
    }

    private async Task VerifyRetentionAtAsync(string key, string version, DateTimeOffset protectedFromUtc, CancellationToken cancellationToken)
    {
        var retention = await recoveryS3.GetObjectRetentionAsync(new GetObjectRetentionRequest
        {
            BucketName = recoveryBucket,
            Key = key,
            VersionId = version
        }, cancellationToken);
        var retainUntil = retention.Retention?.RetainUntilDate?.ToUniversalTime();
        if (retention.Retention?.Mode != ObjectLockRetentionMode.Governance && retention.Retention?.Mode != ObjectLockRetentionMode.Compliance ||
            retainUntil is null || retainUntil.Value < protectedFromUtc.AddDays(90))
            throw new InvalidDataException("recovery_object_retention_unverified");
    }

    private static async Task CopyAndHashAsync(
        Stream source,
        Stream destination,
        ContentAddress expected,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length = checked(length + read);
            if (length > expected.Length) throw new InvalidDataException("checksum_mismatch");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != expected.Length || digest != expected.Sha256)
            throw new InvalidDataException("checksum_mismatch");
    }

    private static FileStream CreateTempFile(string prefix) => new(
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        81920,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);

    private sealed class OwnedResponseStream(Stream stream, GetObjectResponse response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
