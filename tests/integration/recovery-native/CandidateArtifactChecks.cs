using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class CandidateArtifactChecks
{
    internal static async Task RunAsync(NpgsqlConnection connection, RecoveryWorkerConfiguration configuration,
        Guid candidateId, VerifiedCopiedPoint point, IReadOnlyList<RecoveryMaterializedObjectV1> receipts,
        RSA rsa, RecoverySigningTrust trust, CancellationToken ct)
    {
        var oldKey = Environment.GetEnvironmentVariable("Recovery__EvidenceKmsKeyId");
        Environment.SetEnvironmentVariable("Recovery__EvidenceKmsKeyId", "fixture-evidence-kms");
        try
        {
            using var s3 = new EvidenceFake();
            var signer = new Signer(rsa, trust);
            var first = await RecoveryCandidateEvidence.PersistAsync(s3,connection,configuration,candidateId,
                point,receipts,signer,trust,ct);
            var second = await RecoveryCandidateEvidence.PersistAsync(s3,connection,configuration,candidateId,
                point,receipts,signer,trust,ct);
            if (first != second || signer.Calls != 1)
                throw new InvalidOperationException("Candidate evidence replay must preserve original signature and version.");
            var bytes = s3.Objects[first.Key];
            var envelope = JsonSerializer.Deserialize<PrivateSignedCheckpointV1>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            _ = RecoveryEvidenceCodec.VerifyDetached(Convert.FromBase64String(envelope.CanonicalPayloadBase64),
                envelope.PayloadSha256,envelope.SignatureBase64,envelope.SigningKeyId,envelope.SigningPublicKeySha256,trust);
            try { _ = WorkerArtifactCodec.VerifyCheckpoint(bytes,trust); }
            catch (InvalidDataException)
            {
                s3.Objects[first.Key] = RecoveryEvidenceCodec.SerializeCanonical(envelope with { MessageType="recovery_copy_checkpoint" });
                try
                {
                    await RecoveryCandidateEvidence.PersistAsync(s3,connection,configuration,candidateId,
                        point,receipts,signer,trust,ct);
                }
                catch (RecoveryWorkerException exception) when(exception.SafeCode=="candidate_evidence_replay_mismatch") { return; }
                throw new InvalidOperationException("Candidate artifact accepted a forged message type.");
            }
            throw new InvalidOperationException("An unqualified candidate was accepted as a copied point.");
        }
        finally { Environment.SetEnvironmentVariable("Recovery__EvidenceKmsKeyId",oldKey); }
    }

    private sealed class Signer(RSA rsa, RecoverySigningTrust trust) : IRecoveryEvidenceSigner
    {
        public string KeyId => trust.KeyId;
        public string PublicKeySha256 => trust.PublicKeySha256;
        internal int Calls { get; private set; }
        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest,CancellationToken cancellationToken=default)
        {
            Calls++;
            return Task.FromResult(rsa.SignHash(digest.Span,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        }
    }
    private sealed class EvidenceFake() : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL="http://127.0.0.1:1",ForcePathStyle=true })
    {
        internal Dictionary<string,byte[]> Objects { get; } = [];
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
            CancellationToken cancellationToken=default)
        {
            if (!Objects.TryGetValue(request.Key,out var bytes)) throw new AmazonS3Exception("fixture missing") { StatusCode=HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectMetadataResponse { VersionId="evidence-v1",ContentLength=bytes.Length });
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request,CancellationToken cancellationToken=default) =>
            Task.FromResult(new GetObjectResponse { VersionId="evidence-v1",ContentLength=Objects[request.Key].Length,
                ResponseStream=new MemoryStream(Objects[request.Key]) });
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request,CancellationToken cancellationToken=default)
        {
            if (request.BucketName!="recovery-evidence" || request.ServerSideEncryptionKeyManagementServiceKeyId!="fixture-evidence-kms" ||
                request.IfNoneMatch!="*") throw new InvalidOperationException("Evidence write escaped configured retention bucket.");
            using var stream=new MemoryStream();
            await request.InputStream.CopyToAsync(stream,cancellationToken);
            var bytes=stream.ToArray();
            if (Objects.TryGetValue(request.Key,out var previous) && !previous.AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Immutable receipt chunk changed on retry.");
            Objects[request.Key]=bytes;
            return new() { VersionId="evidence-v1" };
        }
    }
}
