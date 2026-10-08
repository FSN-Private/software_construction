using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryApiLaunchJournalChecks
{
    internal static async Task RunAsync(RecoveryWorkerConfiguration configuration,Guid run,
        RecoverySigningTrust trust,RSA rsa,PublishedS3Object candidate)
    {
        var settings=new Dictionary<string,string>
        {
            ["RecoveryOnly"]="true",["ApiTaskDefinitionArn"]="arn:aws:ecs:ap-southeast-2:444455556666:task-definition/recovery-api:1",
            ["ApiClusterArn"]="arn:aws:ecs:ap-southeast-2:444455556666:cluster/recovery",
            ["RunnerSubnetIds"]="subnet-aabb",["ApiSecurityGroupId"]="sg-aabb",["ApiImageDigest"]=new string('b',64),
            ["EvidenceKmsKeyId"]="fixture-kms"
        };
        var old=settings.Keys.Append("ReconcileApiTaskArn").ToDictionary(key=>key,key=>Environment.GetEnvironmentVariable("Recovery__"+key));
        foreach(var (key,value) in settings) Environment.SetEnvironmentVariable("Recovery__"+key,value);
        Environment.SetEnvironmentVariable("Recovery__ReconcileApiTaskArn",null);
        try
        {
            using var signer=new Signer(rsa,trust);var s3=DispatchProxy.Create<IAmazonS3,Store>();var store=(Store)(object)s3;
            var point=Guid.NewGuid();var database="recovery_drill_"+run.ToString("N")+"_"+point.ToString("N")[..8];
            var intent=await RecoveryApiLaunchJournal.PrepareAsync(s3,configuration,run,point,database,candidate,signer,trust,null,default);
            Require(await RecoveryApiLaunchJournal.ReadAsync(s3,configuration,run,trust,null,default)==intent,"intent_not_durable");
            using var ecs=new RecoveryApiTaskChecks.EcsFake { FailOnceAfterLaunch=true };
            try { await RecoveryApiTaskRunner.StartAsync(ecs,configuration,run,database,default); }
            catch(InvalidOperationException error) when(error.Message=="fixture_unknown_launch_result") { }
            await RecoveryApiLaunchJournal.ReconcileAsync(s3,ecs,configuration,intent,signer,trust,default);
            Require(ecs.Launches==2 && ecs.Stops==1,"unknown_launch_not_reconciled");
            var beforeSigns=signer.Signs;
            await RecoveryApiLaunchJournal.ReconcileAsync(s3,ecs,configuration,intent,signer,trust,default);
            Require(ecs.Launches==2 && ecs.Stops==1 && signer.Signs==beforeSigns,"known_stopped_task_relaunched_or_resigned");
            foreach(var fault in new[] { "foreign_run","definition" })
            {
                ecs.Fault=fault;
                await Reject(()=>RecoveryApiLaunchJournal.ReconcileAsync(s3,ecs,configuration,intent,signer,trust,default),
                    "api_candidate_cleanup_binding_invalid");
                Require(ecs.Stops==1,"foreign_task_stopped");
            }
            ecs.Fault=null;
            var now=DateTimeOffset.UtcNow;
            Require(RecoveryApiLaunchJournal.CanRetryUnknown(now.AddMinutes(-44),now) &&
                !RecoveryApiLaunchJournal.CanRetryUnknown(now.AddMinutes(-45),now) &&
                !RecoveryApiLaunchJournal.CanRetryUnknown(now.AddSeconds(1),now),"unknown_retry_window_invalid");
            store.Items.Clear();
            await Reject(()=>RecoveryApiLaunchJournal.ReconcileAsync(s3,ecs,configuration,
                intent with { PreparedAtUtc=now.AddHours(-2) },signer,trust,default),"api_candidate_exact_task_reconciliation_required");
            Require(ecs.Launches==2,"expired_token_relaunched_task");
            Environment.SetEnvironmentVariable("Recovery__ReconcileApiTaskArn",ecs.Request!.Cluster.Replace("cluster/","task/",StringComparison.Ordinal)+"/fixture");
            await RecoveryApiLaunchJournal.ReconcileAsync(s3,ecs,configuration,intent with { PreparedAtUtc=now.AddHours(-2) },signer,trust,default);
            Require(ecs.Launches==2 && ecs.Stops==1,"operator_reconciliation_relaunched_task");
            Environment.SetEnvironmentVariable("Recovery__ReconcileApiTaskArn",null);
            using var partial=new RecoveryApiTaskChecks.EcsFake { Fault="partial_launch" };
            try
            {
                await RecoveryApiTaskRunner.StartAsync(partial,configuration,run,database,default,
                    arn=>RecoveryApiLaunchJournal.RecordTaskAsync(s3,configuration,intent,arn,signer,trust,default));
            }
            catch(RecoveryWorkerException error) when(error.SafeCode=="api_candidate_launch_failed") { }
            Require(partial.Stops==1,"partial_launch_not_cleaned");
            await RecoveryApiLaunchJournal.ReconcileAsync(s3,partial,configuration,intent,signer,trust,default);
            Require(partial.Launches==1 && partial.Stops==1,"partial_launch_reconciliation_relaunched");
            store.Items.Clear();store.FailAfterAcceptingPut=true;
            using var lostWrite=new RecoveryApiTaskChecks.EcsFake();
            try
            {
                await RecoveryApiTaskRunner.StartAsync(lostWrite,configuration,run,database,default,
                    arn=>RecoveryApiLaunchJournal.RecordTaskAsync(s3,configuration,intent,arn,signer,trust,default));
            }
            catch(IOException error) when(error.Message=="fixture_accepted_journal_write_timeout") { }
            Require(lostWrite.Stops==1,"unknown_journal_write_left_task_running");
            await RecoveryApiLaunchJournal.ReconcileAsync(s3,lostWrite,configuration,intent,signer,trust,default);
            Require(lostWrite.Launches==1 && lostWrite.Stops==1,"accepted_journal_write_not_replayed");
            var invalid=intent with { DatabaseName="postgres" };
            try { RecoveryApiLaunchJournal.Validate(invalid,configuration,run,null); }
            catch(RecoveryWorkerException error) when(error.SafeCode=="api_launch_journal_invalid") { return; }
            throw new InvalidOperationException("Unrelated database accepted for cleanup.");
        }
        finally { foreach(var (key,value) in old) Environment.SetEnvironmentVariable("Recovery__"+key,value); }
    }
    private static async Task Reject(Func<Task> action,string code)
    {
        try { await action(); }
        catch(RecoveryWorkerException error) when(error.SafeCode==code) { return; }
        throw new InvalidOperationException("Expected journal failure "+code);
    }
    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private sealed class Signer(RSA rsa,RecoverySigningTrust trust) : IRecoveryEvidenceSigner,IDisposable
    {
        public string KeyId=>trust.KeyId;
        public string PublicKeySha256=>trust.PublicKeySha256;
        internal int Signs;
        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest,CancellationToken ct)
        { Signs++;return Task.FromResult(rsa.SignHash(digest.Span,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)); }
        public void Dispose() { }
    }
    private class Store : DispatchProxy
    {
        internal Dictionary<string,byte[]> Items=[];
        internal bool FailAfterAcceptingPut;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method?.Name==nameof(IAmazonS3.GetObjectMetadataAsync))
            {
                var request=(GetObjectMetadataRequest)args![0]!;
                if(!Items.TryGetValue(request.Key,out var bytes)) throw new AmazonS3Exception("missing") { StatusCode=HttpStatusCode.NotFound };
                return Task.FromResult(new GetObjectMetadataResponse { VersionId="exact-version",ContentLength=bytes.Length });
            }
            if(method?.Name==nameof(IAmazonS3.PutObjectAsync))
            {
                var request=(PutObjectRequest)args![0]!;
                Require(request.IfNoneMatch=="*" && request.ServerSideEncryptionKeyManagementServiceKeyId=="fixture-kms","journal_write_boundary_changed");
                if(Items.ContainsKey(request.Key)) throw new AmazonS3Exception("exists") { StatusCode=HttpStatusCode.PreconditionFailed };
                using var body=new MemoryStream();request.InputStream.CopyTo(body);Items.Add(request.Key,body.ToArray());
                if(FailAfterAcceptingPut)
                {
                    FailAfterAcceptingPut=false;
                    throw new IOException("fixture_accepted_journal_write_timeout");
                }
                return Task.FromResult(new PutObjectResponse { VersionId="exact-version" });
            }
            if(method?.Name==nameof(IAmazonS3.GetObjectAsync))
            {
                var request=(GetObjectRequest)args![0]!;Require(request.VersionId=="exact-version","journal_read_not_exact");
                var bytes=Items[request.Key];
                return Task.FromResult(new GetObjectResponse { VersionId="exact-version",ContentLength=bytes.Length,ResponseStream=new MemoryStream(bytes) });
            }
            throw new NotSupportedException(method?.Name);
        }
    }
}
