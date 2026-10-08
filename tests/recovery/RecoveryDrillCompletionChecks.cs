using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryDrillCompletionChecks
{
    internal static async Task RunAsync()
    {
        var values=new Dictionary<string,string>
        {
            ["Environment"]="completion-test",["RecoveryWorkerBuildDigest"]=new string('a',64),
            ["OwnerId"]="owner",["LibraryId"]="library",["DestinationAccountId"]="444455556666",
            ["DestinationRegion"]="ap-southeast-2",["SourceAccountId"]="111122223333",["SourceRegion"]="ap-southeast-1",
            ["ApiBuildDigest"]=new string('b',64),["MigrationDigest"]=new string('c',64),
            ["ConfigurationDigest"]=new string('d',64),["RecoveryTemplateDigest"]=new string('e',64),
            ["DatabaseRootCertificateSha256"]=new string('f',64),["DatabaseSecretArn"]="fixture-admin-secret",
            ["SourceBucket"]="source",["DestinationBucket"]="destination",["EvidenceBucket"]="evidence",["TargetBucket"]="target"
        };
        var old=values.Keys.ToDictionary(key=>key,key=>Environment.GetEnvironmentVariable("Recovery__"+key));
        foreach(var (key,value) in values) Environment.SetEnvironmentVariable("Recovery__"+key,value);
        try
        {
            var configuration=new RecoveryWorkerConfiguration("drill");var run=Guid.NewGuid();
            using var rsa=RSA.Create(2048);var publicKey=rsa.ExportSubjectPublicKeyInfo();
            var trust=new RecoverySigningTrust("fixture-key",Convert.ToBase64String(publicKey),RecoveryEvidenceCodec.Hash(publicKey));
            var candidate="private candidate fixture"u8.ToArray();
            var value=new RecoveryDrillCompletionV1(1,run,Guid.NewGuid(),"owner","library",
                RecoveryDrillCompletion.Binding(configuration),new($"evidence/drills/{run:D}/candidate.v1.json",
                    "candidate-version",RecoveryEvidenceCodec.Hash(candidate),candidate.Length),DateTimeOffset.UtcNow.AddSeconds(-1),
                true,true,"restore_validated_unqualified");
            byte[] Sign(RecoveryDrillCompletionV1 record)
            {
                var payload=RecoveryEvidenceCodec.SerializeCanonical(record);
                return RecoveryEvidenceCodec.SerializeCanonical(new PrivateSignedCheckpointV1(1,"recovery_drill_completed","drill",
                    trust.KeyId,trust.PublicKeySha256,RecoveryEvidenceCodec.Hash(payload),Convert.ToBase64String(payload),
                    Convert.ToBase64String(rsa.SignHash(SHA256.HashData(payload),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))));
            }
            var bytes=Sign(value);
            Require(RecoveryDrillCompletion.Verify(bytes,configuration,run,trust)==value,"signed_completion_not_replayed");
            foreach(var changed in new[] { value with { RunId=Guid.NewGuid() },value with { OwnerId="foreign" },
                value with { BindingSha256=new string('0',64) },value with { ApiCleanupConfirmed=false },
                value with { DatabaseCleanupConfirmed=false },value with { State="qualified" },
                value with { CompletedAtUtc=DateTimeOffset.UtcNow.AddMinutes(1) },
                value with { CandidateEvidence=value.CandidateEvidence with { Key="evidence/foreign" } } })
                Reject(()=>RecoveryDrillCompletion.Verify(Sign(changed),configuration,run,trust));
            Reject(()=>RecoveryDrillCompletion.Verify(bytes,configuration,run,trust,new string('1',64)));
            var corrupt=bytes.ToArray();corrupt[corrupt.Length/2]^=1;
            Reject(()=>RecoveryDrillCompletion.Verify(corrupt,configuration,run,trust));
            foreach(var fault in new string?[] { null,"candidate_hash","version","truncated","oversized" })
            {
                var s3=DispatchProxy.Create<IAmazonS3,S3Fake>();var fake=(S3Fake)(object)s3;
                fake.Envelope=bytes;fake.Candidate=candidate;fake.Fault=fault;
                try
                {
                    var replay=await RecoveryDrillCompletion.ReadAsync(s3,configuration,run,trust,default);
                    Require(fault is null && replay==value && fake.Reads==2,"accepted_invalid_completion_artifact");
                }
                catch(RecoveryWorkerException) when(fault is not null) { }
            }
            var missing=DispatchProxy.Create<IAmazonS3,S3Fake>();((S3Fake)(object)missing).Fault="missing";
            Require(await RecoveryDrillCompletion.ReadAsync(missing,configuration,run,trust,default) is null,"missing_completion_not_new_run");
            await RecoveryApiLaunchJournalChecks.RunAsync(configuration,run,trust,rsa,value.CandidateEvidence);
        }
        finally { foreach(var (key,value) in old) Environment.SetEnvironmentVariable("Recovery__"+key,value); }
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch(RecoveryWorkerException error) when(error.SafeCode=="drill_completion_invalid") { return; }
        throw new InvalidOperationException("Accepted invalid drill completion.");
    }
    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private class S3Fake : DispatchProxy
    {
        internal byte[] Envelope=[],Candidate=[];
        internal string? Fault;
        internal int Reads;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method?.Name==nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                if(Fault=="missing") throw new AmazonS3Exception("fixture missing") { StatusCode=HttpStatusCode.NotFound };
                return Task.FromResult(new GetObjectMetadataResponse { VersionId="completion-version",ContentLength=Envelope.Length });
            }
            if(method?.Name==nameof(IAmazonS3.GetObjectAsync))
            {
                Reads++;var request=(GetObjectRequest)args![0]!;var completion=request.Key.EndsWith("completed.v1.json",StringComparison.Ordinal);
                Require(request.VersionId==(completion ? "completion-version" : "candidate-version"),"completion_get_not_exact");
                var body=(completion ? Envelope : Candidate).ToArray();
                if(Fault=="candidate_hash" && !completion) body[0]^=1;
                var length=body.Length;
                if(Fault=="truncated") body=body[..^1];
                return Task.FromResult(new GetObjectResponse { VersionId=Fault=="version" ? "foreign" : request.VersionId,
                    ContentLength=Fault=="oversized" ? 5*1024*1024 : length,ResponseStream=new MemoryStream(body) });
            }
            throw new NotSupportedException(method?.Name);
        }
    }
}
