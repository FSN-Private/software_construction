using System.Security.Cryptography;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

public sealed class AwsKmsEvidenceSigner : IRecoveryEvidenceSigner
{
    private readonly IAmazonKeyManagementService kms;
    private readonly string keyArn;
    private readonly byte[] publicKeySpki;

    private AwsKmsEvidenceSigner(IAmazonKeyManagementService kms, string keyArn, byte[] publicKeySpki, string fingerprint)
    {
        this.kms = kms;
        this.keyArn = keyArn;
        this.publicKeySpki = publicKeySpki;
        PublicKeySha256 = fingerprint;
    }

    public string KeyId => keyArn;
    public string PublicKeySha256 { get; }
    public string PublicKeySpkiBase64 => Convert.ToBase64String(publicKeySpki);
    public RecoverySigningTrust Trust => new(KeyId, PublicKeySpkiBase64, PublicKeySha256);

    public static async Task<AwsKmsEvidenceSigner> CreateAsync(
        IAmazonKeyManagementService kms,
        string keyArn,
        string expectedPublicKeySha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kms);
        RequireText(keyArn, "evidence_signing_key_required");
        RequireSha256(expectedPublicKeySha256, "evidence_signing_key_pin_required");
        var response = await kms.GetPublicKeyAsync(new GetPublicKeyRequest { KeyId = keyArn }, cancellationToken);
        if (!string.Equals(response.KeyId, keyArn, StringComparison.Ordinal) ||
            response.KeyUsage != KeyUsageType.SIGN_VERIFY ||
            response.KeySpec.ToString() is not ("RSA_2048" or "RSA_3072" or "RSA_4096") ||
            response.SigningAlgorithms is null || !response.SigningAlgorithms.Contains(SigningAlgorithmSpec.RSASSA_PSS_SHA_256))
            throw new InvalidDataException("evidence_signing_key_policy_invalid");
        var publicKey = response.PublicKey?.ToArray() ?? [];
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        if (publicKey.Length == 0 || fingerprint != expectedPublicKeySha256)
            throw new InvalidDataException("evidence_signing_key_pin_mismatch");
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        if (bytesRead != publicKey.Length || rsa.KeySize < 2048)
            throw new InvalidDataException("evidence_signing_public_key_invalid");
        return new(kms, keyArn, publicKey, fingerprint);
    }

    public async Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken = default)
    {
        if (sha256Digest.Length != 32) throw new ArgumentException("A SHA-256 digest is required.", nameof(sha256Digest));
        var response = await kms.SignAsync(new SignRequest
        {
            KeyId = keyArn,
            Message = new MemoryStream(sha256Digest.ToArray(), writable: false),
            MessageType = MessageType.DIGEST,
            SigningAlgorithm = SigningAlgorithmSpec.RSASSA_PSS_SHA_256
        }, cancellationToken);
        var signature = response.Signature?.ToArray() ?? [];
        if (!string.Equals(response.KeyId, keyArn, StringComparison.Ordinal) || signature.Length == 0)
            throw new InvalidDataException("evidence_signing_response_invalid");
        return signature;
    }

    private static void RequireText(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(code);
    }

    private static void RequireSha256(string value, string code)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException(code);
    }
}
