using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryMediaVerificationChecks
{
    internal static async Task RunAsync()
    {
        var bytes="fixture immutable original"u8.ToArray();
        var now=DateTimeOffset.UtcNow;
        var receipt=new PrivateCopyReceiptV1("source","source-key","source-version",RecoveryEvidenceCodec.Hash(bytes),
            bytes.Length,"recovery","payload/sha256/fixture","exact-version",nameof(RecoveryDataClass.OriginalImage),now,now.AddHours(-1));
        Require(!RecoveryMediaVerification.FullReadRequired(receipt,now),"recent_full_read_not_reusable");
        Require(RecoveryMediaVerification.FullReadRequired(receipt with { FullByteVerifiedAtUtc=now.AddHours(-24) },now),"day_boundary_not_enforced");
        Require(RecoveryMediaVerification.FullReadRequired(receipt with { FullByteVerifiedAtUtc=null },now),"legacy_receipt_not_rehashed");
        Require(!System.Text.Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(receipt with { FullByteVerifiedAtUtc=null }))
            .Contains("fullByteVerifiedAtUtc",StringComparison.Ordinal),"legacy_receipt_bytes_changed");
        foreach(var invalid in new[] { now.AddSeconds(1),now.ToOffset(TimeSpan.FromHours(1)) })
            await Reject(()=>RecoveryMediaVerification.VerifyAsync(Fake(bytes),receipt with { FullByteVerifiedAtUtc=invalid },false,now,default),
                "copy_full_verification_time_invalid");
        var s3=Fake(bytes);var proxy=(S3Fake)(object)s3;
        await RecoveryMediaVerification.VerifyAsync(s3,receipt,false,now,default);
        Require(proxy.RangeReads==1 && proxy.FullReads==0 && proxy.MetadataReads==1,"ordinary_cycle_transferred_full_media");
        await RecoveryMediaVerification.VerifyAsync(s3,receipt,true,now,default);
        Require(proxy.FullReads==1,"drill_skipped_full_read");
        await RecoveryMediaVerification.VerifyAsync(s3,receipt with { FullByteVerifiedAtUtc=now.AddHours(-24) },false,now,default);
        Require(proxy.FullReads==2,"expired_full_receipt_skipped_hash");
        foreach(var fault in new[] { "version","length","retention","lock_mode" })
            await Reject(()=>RecoveryMediaVerification.VerifyAsync(Fake(bytes,fault),receipt,false,now,default),
                "media_verification_retention_identity_invalid");
        await Reject(()=>RecoveryMediaVerification.VerifyAsync(Fake(bytes,"probe_version"),receipt,false,now,default),"media_verification_access_invalid");
        var corrupt=bytes.ToArray();corrupt[0]^=1;
        await Reject(()=>RecoveryMediaVerification.VerifyAsync(Fake(corrupt),receipt,true,now,default),"artifact_checksum_mismatch");
        await Reject(()=>RecoveryMediaVerification.VerifyAsync(Fake(corrupt),receipt with { FullByteVerifiedAtUtc=null },false,now,default),"artifact_checksum_mismatch");
        Require(receipt.FullByteVerifiedAtUtc==now.AddHours(-1),"probe_refreshed_full_verification_time");
    }
    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private static async Task Reject(Func<Task> action,string code)
    {
        try { await action(); }
        catch(RecoveryWorkerException error) when(error.SafeCode==code) { return; }
        throw new InvalidOperationException("Expected media verification rejection: "+code);
    }
    private static IAmazonS3 Fake(byte[] bytes,string? fault=null)
    {
        var instance=DispatchProxy.Create<IAmazonS3,S3Fake>();
        ((S3Fake)(object)instance).Bytes=bytes;((S3Fake)(object)instance).Fault=fault;
        return instance;
    }
    private class S3Fake : DispatchProxy
    {
        internal byte[] Bytes=[];
        internal string? Fault;
        internal int RangeReads,FullReads,MetadataReads;
        protected override object? Invoke(MethodInfo? method,object?[]? arguments)
        {
            if(method?.Name==nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                MetadataReads++;
                var request=(GetObjectMetadataRequest)arguments![0]!;
                Require(request.VersionId=="exact-version","metadata_not_exact_version");
                return Task.FromResult(new GetObjectMetadataResponse
                {
                    VersionId=Fault=="version" ? "swapped" : "exact-version",ContentLength=Bytes.Length+(Fault=="length" ? 1 : 0),
                    ObjectLockMode=Fault=="lock_mode" ? null : ObjectLockMode.Governance,
                    ObjectLockRetainUntilDate=Fault=="retention" ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(90)
                });
            }
            if(method?.Name==nameof(IAmazonS3.GetObjectAsync))
            {
                var request=(GetObjectRequest)arguments![0]!;
                Require(request.VersionId=="exact-version","get_not_exact_version");
                var ranged=request.ByteRange is not null;
                if(ranged) RangeReads++;else FullReads++;
                var body=ranged ? Bytes[..1] : Bytes;
                return Task.FromResult(new GetObjectResponse
                { VersionId=Fault=="probe_version" ? "swapped" : "exact-version",ContentLength=body.Length,ResponseStream=new MemoryStream(body) });
            }
            throw new NotSupportedException(method?.Name);
        }
    }
}
