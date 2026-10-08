using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.ECS;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryCandidateAccessInputs(byte[] Approval, string OwnerToken, string ForeignToken);

internal static class RecoveryCandidateAccessRunner
{
    internal static async Task<RecoveryCandidateAccessInputs?> ReadInputsAsync(
        RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var names = new[] { "OwnerIdentityApprovalPath", "OwnerProbeTokenPath", "ForeignProbeTokenPath" };
        var paths = names.Select(configuration.Optional).ToArray();
        if (paths.All(path => path is null)) return null;
        if (paths.Any(string.IsNullOrWhiteSpace)) throw new RecoveryWorkerException("candidate_probe_inputs_incomplete");
        var bytes = new List<byte[]>();
        foreach (var path in paths)
        {
            await using var file = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > 64 * 1024) throw new RecoveryWorkerException("candidate_probe_input_size_limit");
            var value = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(value, cancellationToken);
            if (file.ReadByte() != -1) throw new RecoveryWorkerException("candidate_probe_input_size_limit");
            bytes.Add(value);
        }
        var utf8 = new UTF8Encoding(false, true);
        return new(bytes[0], utf8.GetString(bytes[1]).Trim(), utf8.GetString(bytes[2]).Trim());
    }

    internal static async Task<RecoveryIdentityApprovalArtifactV1> PrepareAsync(
        IAmazonSecretsManager secrets, NpgsqlConnection connection, RecoveryWorkerConfiguration configuration,
        Guid candidateId, RecoveryCandidateAccessInputs inputs, CancellationToken cancellationToken)
    {
        var trust = new RecoverySigningTrust(configuration.Required("OwnerApprovalTrust__KeyId"),
            configuration.Required("OwnerApprovalTrust__PublicKeySpkiBase64"),
            configuration.RequiredHash("OwnerApprovalTrust__PublicKeySha256"));
        var artifact = RecoveryCandidateIdentity.VerifyApproval(inputs.Approval, trust,
            configuration.Required("OwnerId"), configuration.Required("OwnerUserPoolIssuer"),
            configuration.Required("OwnerUserPoolClientId"), DateTimeOffset.UtcNow);
        await RecoveryCandidateIdentity.InstallAsync(connection, candidateId, inputs.Approval, trust,
            configuration, cancellationToken);
        var secretArn = configuration.Required("RuntimeDatabaseSecretArn");
        var prefix = "arn:aws:secretsmanager:" + configuration.RequiredRegion("DestinationRegion") + ":" +
            configuration.RequiredAccountId("DestinationAccountId") + ":secret:";
        if (!secretArn.StartsWith(prefix, StringComparison.Ordinal) || secretArn.Length <= prefix.Length)
            throw new RecoveryWorkerException("candidate_runtime_secret_binding_invalid");
        var secret = await secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn }, cancellationToken);
        if (secret.SecretString is null || secret.SecretString.Length > 4096)
            throw new RecoveryWorkerException("candidate_runtime_secret_invalid");
        using var json = JsonDocument.Parse(secret.SecretString);
        var username = json.RootElement.GetProperty("username").GetString();
        var password = json.RootElement.GetProperty("password").GetString();
        if (username is null || password is null) throw new RecoveryWorkerException("candidate_runtime_secret_invalid");
        await RecoveryCandidateRuntimeDatabase.ProvisionAsync(connection, candidateId, username, password, cancellationToken);
        return artifact;
    }

    internal static async Task PersistTranscriptAsync(IAmazonS3 s3, RecoveryWorkerConfiguration configuration,
        Guid runId, Guid pointId, RecoveryIdentityApprovalArtifactV1 mapping,
        RecoveryOwnerAccessTranscriptV1 transcript, IRecoveryEvidenceSigner signer, CancellationToken cancellationToken)
    {
        var expectation = new RecoveryOwnerAccessExpectationV1(configuration.Required("OwnerId"),
            configuration.Required("LibraryId"), configuration.RequiredHash("ConfigurationDigest"),
            configuration.RequiredHash("ApiBuildDigest"), configuration.Required("SourceRevision"),
            configuration.RequiredHash("MigrationDigest"), configuration.WorkerBuildDigest,
            configuration.RequiredHash("RecoveryTemplateDigest"), configuration.Required("OwnerUserPoolId"),
            configuration.Required("OwnerUserPoolClientId"), configuration.Required("OwnerUserPoolIssuer"));
        expectation.Validate();
        transcript.Validate(expectation.OwnerId, DateTimeOffset.UtcNow);
        var payload = RecoveryEvidenceCodec.SerializeCanonical(new RecoveryCandidateAccessProbeV1(1,
            runId, pointId, "independent_attestation_pending",
            RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(mapping)), transcript, expectation));
        var signature = await signer.SignDigestAsync(SHA256.HashData(payload), cancellationToken);
        var envelope = new PrivateSignedCheckpointV1(1, "recovery_candidate_access_probe", configuration.WorkerId,
            signer.KeyId, signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(payload),
            Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        // This worker signature records observations, never substitutes for the
        // independently MFA-signed owner-access attestation or a healthy catalog.
        await S3ArtifactWriter.PutBytesAsync(s3, configuration.Required("EvidenceBucket"),
            $"evidence/drills/{runId:D}/access-probes/{transcript.ProbeId:D}.v1.json",
            RecoveryEvidenceCodec.SerializeCanonical(envelope), configuration.Required("EvidenceKmsKeyId"), cancellationToken);
    }
}
