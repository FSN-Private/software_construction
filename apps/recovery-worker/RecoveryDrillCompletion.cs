using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryDrillCompletionV1(int SchemaVersion, Guid RunId, Guid PointId,
    string OwnerId, string LibraryId, string BindingSha256, PublishedS3Object CandidateEvidence,
    DateTimeOffset CompletedAtUtc, bool ApiCleanupConfirmed, bool DatabaseCleanupConfirmed,
    string State);

/// <summary>A terminal, private signed receipt makes completed retries independent
/// of the ephemeral clone and mutable discovery/control records.</summary>
internal static class RecoveryDrillCompletion
{
    internal static async Task<RecoveryDrillCompletionV1?> ReadAsync(IAmazonS3 s3,
        RecoveryWorkerConfiguration configuration, Guid runId, RecoverySigningTrust trust,
        CancellationToken ct, string? approvalSha256 = null)
    {
        byte[] bytes;
        try { bytes=await ReadExactAsync(s3,configuration.Required("EvidenceBucket"),Key(runId),null,64*1024,ct); }
        catch(AmazonS3Exception error) when(error.StatusCode==HttpStatusCode.NotFound || error.ErrorCode=="NoSuchKey")
        { return null; }
        var result=Verify(bytes,configuration,runId,trust,approvalSha256);
        var candidate=await ReadExactAsync(s3,configuration.Required("EvidenceBucket"),
            result.CandidateEvidence.Key,result.CandidateEvidence.VersionId,4*1024*1024,ct);
        if(candidate.LongLength!=result.CandidateEvidence.ByteLength ||
            RecoveryEvidenceCodec.Hash(candidate)!=result.CandidateEvidence.Sha256)
            throw new RecoveryWorkerException("drill_completion_candidate_mismatch");
        return result;
    }

    internal static async Task<PublishedS3Object> PersistAsync(IAmazonS3 s3,
        RecoveryWorkerConfiguration configuration, Guid runId, Guid pointId,
        PublishedS3Object candidate, IRecoveryEvidenceSigner signer, CancellationToken ct, string? approvalSha256 = null,
        string state = "restore_validated_unqualified")
    {
        var completion=new RecoveryDrillCompletionV1(1,runId,pointId,configuration.Required("OwnerId"),
            configuration.Required("LibraryId"),Binding(configuration,approvalSha256),candidate,DateTimeOffset.UtcNow,
            true,true,state);
        var payload=RecoveryEvidenceCodec.SerializeCanonical(completion);
        var signature=await signer.SignDigestAsync(SHA256.HashData(payload),ct);
        var envelope=new PrivateSignedCheckpointV1(1,"recovery_drill_completed",configuration.WorkerId,
            signer.KeyId,signer.PublicKeySha256,RecoveryEvidenceCodec.Hash(payload),
            Convert.ToBase64String(payload),Convert.ToBase64String(signature));
        return await S3ArtifactWriter.PutBytesAsync(s3,configuration.Required("EvidenceBucket"),Key(runId),
            RecoveryEvidenceCodec.SerializeCanonical(envelope),configuration.Required("EvidenceKmsKeyId"),ct);
    }

    internal static RecoveryDrillCompletionV1 Verify(byte[] bytes,RecoveryWorkerConfiguration configuration,
        Guid runId,RecoverySigningTrust trust,string? approvalSha256 = null)
    {
        try
        {
            if(bytes.Length is < 1 or > 64*1024) throw new InvalidDataException();
            var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var envelope=JsonSerializer.Deserialize<PrivateSignedCheckpointV1>(bytes,json) ?? throw new InvalidDataException();
            if(envelope.SchemaVersion!=1 || envelope.MessageType!="recovery_drill_completed" ||
                !RecoveryEvidenceCodec.SerializeCanonical(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException();
            var payload=Convert.FromBase64String(envelope.CanonicalPayloadBase64);
            _=RecoveryEvidenceCodec.VerifyDetached(payload,envelope.PayloadSha256,envelope.SignatureBase64,
                envelope.SigningKeyId,envelope.SigningPublicKeySha256,trust);
            var result=JsonSerializer.Deserialize<RecoveryDrillCompletionV1>(payload,json) ?? throw new InvalidDataException();
            if(!RecoveryEvidenceCodec.SerializeCanonical(result).AsSpan().SequenceEqual(payload) ||
                result.SchemaVersion!=1 || result.RunId!=runId || result.PointId==Guid.Empty ||
                result.OwnerId!=configuration.Required("OwnerId") || result.LibraryId!=configuration.Required("LibraryId") ||
                result.BindingSha256!=Binding(configuration,approvalSha256) || !result.ApiCleanupConfirmed || !result.DatabaseCleanupConfirmed ||
                result.State is not ("restore_validated_unqualified" or "interrupted_cleaned_unqualified") || result.CompletedAtUtc.Offset!=TimeSpan.Zero ||
                result.CompletedAtUtc>DateTimeOffset.UtcNow || result.CandidateEvidence is null ||
                result.CandidateEvidence.Key!=$"evidence/drills/{runId:D}/candidate.v1.json" ||
                string.IsNullOrWhiteSpace(result.CandidateEvidence.VersionId) || result.CandidateEvidence.VersionId=="null" ||
                result.CandidateEvidence.ByteLength is < 1 or > 4*1024*1024 ||
                result.CandidateEvidence.Sha256.Length!=64 || result.CandidateEvidence.Sha256.Any(c=>!char.IsAsciiHexDigit(c)))
                throw new InvalidDataException();
            return result;
        }
        catch(Exception error) when(error is JsonException or InvalidDataException or FormatException or ArgumentException or NullReferenceException or CryptographicException)
        { throw new RecoveryWorkerException("drill_completion_invalid"); }
    }

    internal static string Binding(RecoveryWorkerConfiguration configuration,string? approvalSha256 = null)=>RecoveryEvidenceCodec.Hash(
        RecoveryEvidenceCodec.SerializeCanonical(new
        {
            owner=configuration.Required("OwnerId"),library=configuration.Required("LibraryId"),
            recoveryAccount=configuration.RequiredAccountId("DestinationAccountId"),region=configuration.RequiredRegion("DestinationRegion"),
            sourceAccount=configuration.RequiredAccountId("SourceAccountId"),sourceRegion=configuration.RequiredRegion("SourceRegion"),
            worker=configuration.WorkerBuildDigest,release=configuration.RequiredHash("ApiBuildDigest"),
            migrations=configuration.RequiredHash("MigrationDigest"),configuration=configuration.RequiredHash("ConfigurationDigest"),
            template=configuration.RequiredHash("RecoveryTemplateDigest"),
            sourceBucket=configuration.Required("SourceBucket"),destinationBucket=configuration.Required("DestinationBucket"),
            evidenceBucket=configuration.Required("EvidenceBucket"),targetBucket=configuration.Required("TargetBucket"),
            databaseSecret=configuration.Required("DatabaseSecretArn"),certificate=configuration.RequiredHash("DatabaseRootCertificateSha256"),
            ownerApprovalSha256=approvalSha256,
            apiTask=configuration.Optional("ApiTaskDefinitionArn"),apiImage=configuration.Optional("ApiImageDigest"),
            apiCluster=configuration.Optional("ApiClusterArn"),apiSubnets=configuration.Optional("RunnerSubnetIds"),
            apiSecurityGroup=configuration.Optional("ApiSecurityGroupId"),runtimeSecret=configuration.Optional("RuntimeDatabaseSecretArn")
        }));

    private static string Key(Guid runId)=>$"evidence/drills/{runId:D}/completed.v1.json";
    internal static async Task<byte[]> ReadExactAsync(IAmazonS3 s3,string bucket,string key,string? version,int limit,CancellationToken ct)
    {
        if(version is null)
        {
            var metadata=await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName=bucket,Key=key },ct);
            if(metadata.ContentLength is < 1 || metadata.ContentLength>limit ||
                string.IsNullOrWhiteSpace(metadata.VersionId) || metadata.VersionId=="null")
                throw new RecoveryWorkerException("drill_completion_invalid");
            version=metadata.VersionId;
        }
        using var response=await s3.GetObjectAsync(new GetObjectRequest { BucketName=bucket,Key=key,VersionId=version },ct);
        if(response.VersionId!=version || response.ContentLength is < 1 || response.ContentLength>limit)
            throw new RecoveryWorkerException("drill_completion_invalid");
        using var body=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await response.ResponseStream.ReadAsync(buffer,ct))>0)
        {
            if(body.Length+count>response.ContentLength) throw new RecoveryWorkerException("drill_completion_invalid");
            body.Write(buffer,0,count);
        }
        if(body.Length!=response.ContentLength) throw new RecoveryWorkerException("drill_completion_invalid");
        return body.ToArray();
    }
}
