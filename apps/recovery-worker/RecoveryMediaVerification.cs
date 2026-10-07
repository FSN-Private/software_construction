using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryMediaVerification
{
    internal static bool FullReadRequired(PrivateCopyReceiptV1 receipt, DateTimeOffset nowUtc)
    {
        if (receipt.FullByteVerifiedAtUtc is not { } verified) return true;
        if (verified.Offset != TimeSpan.Zero || verified > nowUtc || verified > receipt.VerifiedAtUtc)
            throw new RecoveryWorkerException("copy_full_verification_time_invalid");
        return nowUtc - verified >= TimeSpan.FromHours(24);
    }

    internal static async Task VerifyAsync(IAmazonS3 s3, PrivateCopyReceiptV1 receipt,
        bool forceFullRead, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (receipt.DataClass is not (nameof(RecoveryDataClass.OriginalImage) or nameof(RecoveryDataClass.XmpSidecar)))
            throw new RecoveryWorkerException("media_verification_class_invalid");
        var due = FullReadRequired(receipt, nowUtc);
        if (forceFullRead || due)
        {
            if (receipt.DataClass == nameof(RecoveryDataClass.XmpSidecar))
                await RecoveryXmpContentVerifier.VerifyExactAsync(s3, receipt, ct);
            else
                await S3ArtifactWriter.VerifyExactAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
                    receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, ct);
            return;
        }
        // This timestamp arrives only through the verified signed checkpoint and
        // exact receipt set. An unsigned time or a changed object version is never
        // authority to skip the periodic full hash/structure verification.
        var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        { BucketName=receipt.DestinationBucket,Key=receipt.DestinationKey,VersionId=receipt.DestinationVersionId },ct);
        if(metadata.VersionId!=receipt.DestinationVersionId || metadata.ContentLength!=receipt.ByteLength ||
            metadata.ObjectLockMode != ObjectLockMode.Governance && metadata.ObjectLockMode != ObjectLockMode.Compliance ||
            metadata.ObjectLockRetainUntilDate is not { } retainUntil || retainUntil.ToUniversalTime()<=nowUtc.UtcDateTime)
            throw new RecoveryWorkerException("media_verification_retention_identity_invalid");
        using var access = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName=receipt.DestinationBucket,Key=receipt.DestinationKey,VersionId=receipt.DestinationVersionId,
            ByteRange=receipt.ByteLength>0 ? new ByteRange(0,0) : null
        },ct);
        var expectedLength=receipt.ByteLength>0 ? 1 : 0;
        if(access.VersionId!=receipt.DestinationVersionId || access.ContentLength!=expectedLength)
            throw new RecoveryWorkerException("media_verification_access_invalid");
        var probe=new byte[2];
        var read=await access.ResponseStream.ReadAsync(probe,ct);
        if(read!=expectedLength || await access.ResponseStream.ReadAsync(probe,ct)!=0)
            throw new RecoveryWorkerException("media_verification_access_invalid");
    }
}
