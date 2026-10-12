using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.ECS;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryApiLaunchIntentV1(int SchemaVersion, Guid RunId, Guid PointId,
    string DatabaseName, string BindingSha256, DateTimeOffset PreparedAtUtc, PublishedS3Object CandidateEvidence);
internal sealed record RecoveryApiLaunchedTaskV1(int SchemaVersion, string IntentSha256, string TaskArn);

internal static class RecoveryApiLaunchJournal
{
    internal static async Task<RecoveryApiLaunchIntentV1?> ReadAsync(IAmazonS3 s3,
        RecoveryWorkerConfiguration configuration, Guid runId, RecoverySigningTrust trust,
        string? approvalSha256,CancellationToken ct)
    {
        var bytes=await ReadOptionalAsync(s3,configuration,Key(runId,"api-launch"),ct);
        if(bytes is null) return null;
        var intent=Verify<RecoveryApiLaunchIntentV1>(bytes,"recovery_api_launch_intent",trust);
        Validate(intent,configuration,runId,approvalSha256);
        return intent;
    }

    internal static async Task<RecoveryApiLaunchIntentV1> PrepareAsync(IAmazonS3 s3,
        RecoveryWorkerConfiguration configuration,Guid runId,Guid pointId,string databaseName,
        PublishedS3Object candidate,IRecoveryEvidenceSigner signer,RecoverySigningTrust trust,string? approvalSha256,CancellationToken ct)
    {
        var intent=new RecoveryApiLaunchIntentV1(1,runId,pointId,databaseName,
            RecoveryDrillCompletion.Binding(configuration,approvalSha256),DateTimeOffset.UtcNow,candidate);
        Validate(intent,configuration,runId,approvalSha256);
        await PersistAsync(s3,configuration,Key(runId,"api-launch"),"recovery_api_launch_intent",intent,signer,trust,ct);
        return intent;
    }

    internal static Task RecordTaskAsync(IAmazonS3 s3,RecoveryWorkerConfiguration configuration,
        RecoveryApiLaunchIntentV1 intent,string taskArn,IRecoveryEvidenceSigner signer,RecoverySigningTrust trust,CancellationToken ct)=>
        PersistAsync(s3,configuration,Key(intent.RunId,"api-task"),"recovery_api_launched_task",
            new RecoveryApiLaunchedTaskV1(1,RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(intent)),taskArn),signer,trust,ct);

    internal static async Task ReconcileAsync(IAmazonS3 s3,IAmazonECS ecs,
        RecoveryWorkerConfiguration configuration,RecoveryApiLaunchIntentV1 intent,
        IRecoveryEvidenceSigner signer,RecoverySigningTrust trust,CancellationToken ct)
    {
        var recorded=await ReadOptionalAsync(s3,configuration,Key(intent.RunId,"api-task"),ct);
        string? taskArn=null;
        if(recorded is not null)
        {
            var task=Verify<RecoveryApiLaunchedTaskV1>(recorded,"recovery_api_launched_task",trust);
            if(task.SchemaVersion!=1 || string.IsNullOrWhiteSpace(task.TaskArn) ||
                task.IntentSha256!=RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(intent)))
                throw new RecoveryWorkerException("api_launch_journal_invalid");
            taskArn=task.TaskArn;
        }
        else if(configuration.Optional("ReconcileApiTaskArn") is { } operatorArn) taskArn=operatorArn;
        else
        {
            // ECS retains tokens only for task lifetime + one hour, capped at 24h.
            // A stopped-immediately task therefore has the shortest retry window.
            if(!CanRetryUnknown(intent.PreparedAtUtc,DateTimeOffset.UtcNow))
                throw new RecoveryWorkerException("api_candidate_exact_task_reconciliation_required");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                var task=await RecoveryApiTaskRunner.StartAsync(ecs,configuration,intent.RunId,intent.DatabaseName,
                    deadline.Token,async arn=>
                    {
                        taskArn=arn;
                        await RecordTaskAsync(s3,configuration,intent,arn,signer,trust,deadline.Token);
                    });
                taskArn=task.TaskArn;
            }
            catch(RecoveryWorkerException error) when(error.SafeCode=="api_candidate_stopped_before_ready" && taskArn is not null) { }
        }
        if(string.IsNullOrWhiteSpace(taskArn)) throw new RecoveryWorkerException("api_candidate_stop_unconfirmed");
        await RecoveryApiTaskRunner.StopOwnedAsync(ecs,configuration,intent.RunId,taskArn,ct);
    }

    internal static bool CanRetryUnknown(DateTimeOffset preparedAtUtc,DateTimeOffset nowUtc)=>
        preparedAtUtc.Offset==TimeSpan.Zero && nowUtc.Offset==TimeSpan.Zero && preparedAtUtc<=nowUtc &&
        nowUtc-preparedAtUtc<TimeSpan.FromMinutes(45);

    internal static void Validate(RecoveryApiLaunchIntentV1 intent,RecoveryWorkerConfiguration configuration,
        Guid runId,string? approvalSha256)
    {
        if(intent.SchemaVersion!=1 || runId==Guid.Empty || intent.RunId!=runId || intent.PointId==Guid.Empty ||
            intent.DatabaseName!="recovery_drill_"+runId.ToString("N")+"_"+intent.PointId.ToString("N")[..8] ||
            intent.BindingSha256!=RecoveryDrillCompletion.Binding(configuration,approvalSha256) ||
            intent.PreparedAtUtc.Offset!=TimeSpan.Zero || intent.PreparedAtUtc>DateTimeOffset.UtcNow ||
            intent.CandidateEvidence is null || intent.CandidateEvidence.Key!=$"evidence/drills/{runId:D}/candidate.v1.json" ||
            string.IsNullOrWhiteSpace(intent.CandidateEvidence.VersionId) || intent.CandidateEvidence.VersionId=="null" ||
            intent.CandidateEvidence.ByteLength is <1 or >4*1024*1024 ||
            intent.CandidateEvidence.Sha256 is not { Length:64 } || intent.CandidateEvidence.Sha256.Any(c=>!char.IsAsciiHexDigit(c)))
            throw new RecoveryWorkerException("api_launch_journal_invalid");
    }

    internal static T Verify<T>(byte[] bytes,string type,RecoverySigningTrust trust)
    {
        try
        {
            if(bytes.Length is <1 or >64*1024) throw new InvalidDataException();
            var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var envelope=JsonSerializer.Deserialize<PrivateSignedCheckpointV1>(bytes,json) ?? throw new InvalidDataException();
            if(envelope.SchemaVersion!=1 || envelope.MessageType!=type ||
                !RecoveryEvidenceCodec.SerializeCanonical(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException();
            var payload=Convert.FromBase64String(envelope.CanonicalPayloadBase64);
            _=RecoveryEvidenceCodec.VerifyDetached(payload,envelope.PayloadSha256,envelope.SignatureBase64,
                envelope.SigningKeyId,envelope.SigningPublicKeySha256,trust);
            var value=JsonSerializer.Deserialize<T>(payload,json) ?? throw new InvalidDataException();
            if(!RecoveryEvidenceCodec.SerializeCanonical(value).AsSpan().SequenceEqual(payload)) throw new InvalidDataException();
            return value;
        }
        catch(Exception error) when(error is JsonException or InvalidDataException or FormatException or ArgumentException or CryptographicException)
        { throw new RecoveryWorkerException("api_launch_journal_invalid"); }
    }

    internal static async Task PersistAsync<T>(IAmazonS3 s3,RecoveryWorkerConfiguration configuration,
        string key,string type,T value,IRecoveryEvidenceSigner signer,RecoverySigningTrust trust,CancellationToken ct)
    {
        var payload=RecoveryEvidenceCodec.SerializeCanonical(value);
        var existing=await ReadOptionalAsync(s3,configuration,key,ct);
        if(existing is not null)
        {
            if(!RecoveryEvidenceCodec.SerializeCanonical(Verify<T>(existing,type,trust)).AsSpan().SequenceEqual(payload))
                throw new RecoveryWorkerException("api_launch_journal_replay_mismatch");
            return;
        }
        var signature=await signer.SignDigestAsync(SHA256.HashData(payload),ct);
        var envelope=new PrivateSignedCheckpointV1(1,type,configuration.WorkerId,signer.KeyId,signer.PublicKeySha256,
            RecoveryEvidenceCodec.Hash(payload),Convert.ToBase64String(payload),Convert.ToBase64String(signature));
        await S3ArtifactWriter.PutBytesAsync(s3,configuration.Required("EvidenceBucket"),key,
            RecoveryEvidenceCodec.SerializeCanonical(envelope),configuration.Required("EvidenceKmsKeyId"),ct);
    }
    private static string Key(Guid runId,string name)=>$"evidence/drills/{runId:D}/{name}.v1.json";
    internal static async Task<byte[]?> ReadOptionalAsync(IAmazonS3 s3,RecoveryWorkerConfiguration configuration,string key,CancellationToken ct)
    {
        try { return await RecoveryDrillCompletion.ReadExactAsync(s3,configuration.Required("EvidenceBucket"),key,null,64*1024,ct); }
        catch(AmazonS3Exception error) when(error.StatusCode==HttpStatusCode.NotFound || error.ErrorCode=="NoSuchKey") { return null; }
    }
}
