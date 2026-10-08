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

    /// <summary>
    /// Revalidates an already-settled durable writer intent without creating another
    /// S3 version. Both the exact primary version and the recorded recovery version
    /// are streamed and hashed again before the caller may reuse the receipt.
    /// </summary>
    public async Task<RecoveryCopyReceipt> VerifyExistingAsync(
        RecoveryObjectReference source,
        string destinationKey,
        string destinationVersionId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        if (string.IsNullOrWhiteSpace(destinationKey) || string.IsNullOrWhiteSpace(destinationVersionId) ||
            destinationVersionId == "null")
            throw new InvalidDataException("recovery_existing_identity_invalid");
        if (admittedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("recovery_admission_time_not_utc");

        await CheckReadyAsync(cancellationToken);
        var expected = new ContentAddress(source.Sha256, source.ByteLength);
        await using (var staged = await ReadToVerifiedTempFileAsync(source, expected, cancellationToken)) { }
        await VerifyExactDestinationAsync(destinationKey, destinationVersionId, expected, cancellationToken);
        await VerifyRetentionAtAsync(destinationKey, destinationVersionId, admittedAtUtc, cancellationToken);

        var receipt = new RecoveryCopyReceipt(
            source,
            recoveryBucket,
            destinationKey,
            destinationVersionId,
            recoveryAccountAlias,
            recoveryRegion,
            admittedAtUtc,
            clock.GetUtcNow(),
            verifierBuild,
            RetentionPolicyVerified: true,
            AccessPolicyVerified: true);
        receipt.Validate();
        return receipt;
    }

    /// <summary>
    /// Reuses the exact immutable destination version named by a previously verified,
    /// signed point receipt. The caller must verify that signature and source-to-
    /// destination mapping before calling. This method performs fresh destination
    /// access and retention checks without rereading the full source or payload.
    /// </summary>
    public async Task<RecoveryCopyReceipt> ReuseVerifiedReceiptAsync(
        RecoveryObjectReference source,
        string destinationKey,
        RecoveryCopyReceipt signedReceipt,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signedReceipt);
        source.Validate();
        if (admittedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("recovery_admission_time_not_utc");
        if (source.DataClass != signedReceipt.Source.DataClass || source.OwnerId != signedReceipt.Source.OwnerId ||
            source.LibraryId != signedReceipt.Source.LibraryId || source.SourceKey != signedReceipt.Source.SourceKey ||
            source.SourceVersionId != signedReceipt.Source.SourceVersionId || source.Sha256 != signedReceipt.Source.Sha256 ||
            source.ByteLength != signedReceipt.Source.ByteLength || signedReceipt.DestinationBucket != recoveryBucket ||
            signedReceipt.DestinationKey != destinationKey || signedReceipt.DestinationRegion != recoveryRegion ||
            signedReceipt.DestinationAccountAlias != recoveryAccountAlias ||
            string.IsNullOrWhiteSpace(signedReceipt.DestinationVersionId) || signedReceipt.DestinationVersionId == "null" ||
            signedReceipt.VerifiedAtUtc.Offset != TimeSpan.Zero || signedReceipt.VerifiedAtUtc > clock.GetUtcNow())
            throw new InvalidDataException("recovery_signed_receipt_binding_invalid");

        await CheckReadyAsync(cancellationToken);
        var metadata = await recoveryS3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = recoveryBucket,
            Key = destinationKey,
            VersionId = signedReceipt.DestinationVersionId
        }, cancellationToken);
        if (metadata.VersionId != signedReceipt.DestinationVersionId || metadata.ContentLength != source.ByteLength)
            throw new InvalidDataException("recovery_destination_identity_mismatch");

        // A one-byte exact-version read confirms current S3/KMS access without
        // repeating the payload transfer; the signed receipt attests the full hash.
        if (source.ByteLength > 0)
        {
            using var response = await recoveryS3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = recoveryBucket,
                Key = destinationKey,
                VersionId = signedReceipt.DestinationVersionId,
                ByteRange = new ByteRange(0, 0)
            }, cancellationToken);
            if (response.VersionId != signedReceipt.DestinationVersionId || response.ContentLength != 1)
                throw new InvalidDataException("recovery_destination_access_probe_invalid");
            var firstByte = new byte[1];
            if (await response.ResponseStream.ReadAsync(firstByte, cancellationToken) != 1)
                throw new InvalidDataException("recovery_destination_access_probe_invalid");
        }

        await EnsureReuseRetentionAsync(source, destinationKey, signedReceipt.DestinationVersionId, admittedAtUtc, cancellationToken);
        var receipt = new RecoveryCopyReceipt(source, recoveryBucket, destinationKey,
            signedReceipt.DestinationVersionId, recoveryAccountAlias, recoveryRegion, admittedAtUtc,
            clock.GetUtcNow(), verifierBuild, RetentionPolicyVerified: true, AccessPolicyVerified: true);
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
            if (response.VersionId != source.SourceVersionId) throw new InvalidDataException("recovery_source_identity_mismatch");
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

    private async Task EnsureReuseRetentionAsync(RecoveryObjectReference source, string key, string version,
        DateTimeOffset admittedAtUtc, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (admittedAtUtc > now || now.Offset != TimeSpan.Zero ||
            source.DataClass is not (RecoveryDataClass.OriginalImage or RecoveryDataClass.XmpSidecar) ||
            key != "payload/sha256/" + source.Sha256)
            throw new InvalidDataException("recovery_retention_extension_scope_invalid");
        var current = await recoveryS3.GetObjectRetentionAsync(new GetObjectRetentionRequest
        { BucketName = recoveryBucket, Key = key, VersionId = version }, cancellationToken);
        var until = current.Retention?.RetainUntilDate?.ToUniversalTime();
        if (until is null || current.Retention?.Mode != ObjectLockRetentionMode.Governance &&
            current.Retention?.Mode != ObjectLockRetentionMode.Compliance)
            throw new InvalidDataException("recovery_object_retention_unverified");
        if (until.Value >= admittedAtUtc.AddDays(90).UtcDateTime) return;
        if (current.Retention?.Mode != ObjectLockRetentionMode.Governance)
            throw new InvalidDataException("recovery_retention_extension_governance_required");
        await recoveryS3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = recoveryBucket, Key = key, VersionId = version, BypassGovernanceRetention = false,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance,
                RetainUntilDate = now.AddDays(91).UtcDateTime }
        }, cancellationToken);
        // Only the reread establishes protection. An unknown write response leaves
        // the caller's existing fenced intent pending and cannot produce a receipt.
        await VerifyRetentionAtAsync(key, version, admittedAtUtc, cancellationToken);
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
