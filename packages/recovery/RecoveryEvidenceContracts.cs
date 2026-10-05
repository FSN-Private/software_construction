using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

public sealed record RecoveryObservationPayloadV1(
    int SchemaVersion,
    Guid ObservationId,
    Guid RecoveryPointId,
    string OwnerId,
    string LibraryId,
    string DataClass,
    string Outcome,
    DateTimeOffset ObservedAtUtc,
    long ExpectedItemCount,
    long ObservedItemCount,
    long ExpectedByteLength,
    long ObservedByteLength,
    string ManifestSha256,
    IReadOnlyList<string> SafeCodes,
    RecoveryAlertDeliveryV1? AlertDelivery)
{
    public const int CurrentSchemaVersion = 1;

    public RecoveryObservationPayloadV1 Canonicalized() => this with
    {
        SafeCodes = SafeCodes.Order(StringComparer.Ordinal).ToArray()
    };

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || ObservationId == Guid.Empty || RecoveryPointId == Guid.Empty)
            throw new InvalidDataException("invalid_observation_identity");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (!RecoveryEvidenceCodec.ObservationClasses.Contains(DataClass, StringComparer.Ordinal)) throw new InvalidDataException("invalid_observation_class");
        if (!RecoveryEvidenceCodec.ObservationOutcomes.Contains(Outcome, StringComparer.Ordinal)) throw new InvalidDataException("invalid_observation_outcome");
        RecoveryValidation.Utc(ObservedAtUtc, nameof(ObservedAtUtc));
        if (ExpectedItemCount < 0 || ObservedItemCount < 0 || ExpectedByteLength < 0 || ObservedByteLength < 0)
            throw new InvalidDataException("invalid_observation_coverage");
        RecoveryValidation.Hash(ManifestSha256, nameof(ManifestSha256));
        RecoveryEvidenceCodec.ValidateSafeCodes(SafeCodes);
        if (Outcome == "healthy" &&
            (ExpectedItemCount != ObservedItemCount || ExpectedByteLength != ObservedByteLength || SafeCodes.Count != 0))
            throw new InvalidDataException("healthy_observation_mismatch");
        AlertDelivery?.Validate();
    }

    public byte[] SerializeCanonical() => RecoveryEvidenceCodec.SerializeCanonical(this);
}

public sealed record RecoveryAlertDeliveryV1(
    Guid AlertId,
    DateTimeOffset ThresholdAtUtc,
    DateTimeOffset? ProviderAcceptedAtUtc,
    DateTimeOffset? DeliveryAtUtc,
    string DeliveryStatus,
    string? DeliveryEvidenceSha256)
{
    public void Validate()
    {
        if (AlertId == Guid.Empty) throw new InvalidDataException("invalid_alert_id");
        RecoveryValidation.Utc(ThresholdAtUtc, nameof(ThresholdAtUtc));
        if (ProviderAcceptedAtUtc is { } accepted) RecoveryValidation.Utc(accepted, nameof(ProviderAcceptedAtUtc));
        if (DeliveryAtUtc is { } delivered) RecoveryValidation.Utc(delivered, nameof(DeliveryAtUtc));
        if (DeliveryStatus is not ("pending" or "delivered" or "failed" or "unknown")) throw new InvalidDataException("invalid_alert_delivery_status");
        if (DeliveryStatus == "delivered")
        {
            if (DeliveryAtUtc is null || ProviderAcceptedAtUtc is null || DeliveryEvidenceSha256 is null)
                throw new InvalidDataException("alert_delivery_proof_required");
            if (ProviderAcceptedAtUtc < ThresholdAtUtc || DeliveryAtUtc < ProviderAcceptedAtUtc)
                throw new InvalidDataException("alert_delivery_timeline_invalid");
            RecoveryValidation.Hash(DeliveryEvidenceSha256, nameof(DeliveryEvidenceSha256));
        }
        else if (DeliveryAtUtc is not null || DeliveryEvidenceSha256 is not null)
            throw new InvalidDataException("unverified_alert_delivery");
    }
}

public sealed record RecoveryObservationEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("unsupported_observation_envelope");
        if (MessageType != "observation") throw new InvalidDataException("invalid_recovery_message_type");
        RecoveryValidation.Text(WorkerId, nameof(WorkerId), 200);
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_observation_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("observation_signature_required");
    }
}

public sealed record RecoverySigningTrust(string KeyId, string PublicKeySpkiBase64, string PublicKeySha256)
{
    public byte[] ValidateAndGetPublicKey()
    {
        RecoveryValidation.Text(KeyId, nameof(KeyId), 512);
        RecoveryValidation.Hash(PublicKeySha256, nameof(PublicKeySha256));
        byte[] key;
        try { key = Convert.FromBase64String(PublicKeySpkiBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_trusted_public_key", exception); }
        var digest = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        if (digest != PublicKeySha256) throw new InvalidDataException("trusted_public_key_fingerprint_mismatch");
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(key, out var consumed);
            if (consumed != key.Length) throw new InvalidDataException("invalid_trusted_public_key");
        }
        catch (CryptographicException exception) { throw new InvalidDataException("invalid_trusted_public_key", exception); }
        return key;
    }
}

public sealed record VerifiedRecoveryObservation(
    RecoveryObservationPayloadV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public sealed record RecoveryEvidenceSignature(string KeyId, string PublicKeySha256, byte[] Signature);

public interface IRecoveryEvidenceSigner
{
    string KeyId { get; }
    string PublicKeySha256 { get; }
    Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken = default);
}

public static class RecoveryEvidenceCodec
{
    public static readonly string[] ObservationClasses =
        ["originals", "xmp", "version_graph", "metadata", "audit", "configuration", "recovery_records"];
    public static readonly string[] ObservationOutcomes =
        ["healthy", "missing", "incomplete", "corrupt", "inaccessible", "pending_retrieval", "failed"];

    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static byte[] SerializeCanonical<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public static byte[] SerializeObservationEnvelope(RecoveryObservationEnvelopeV1 envelope)
    {
        envelope.Validate();
        return SerializeCanonical(envelope);
    }

    public static RecoveryObservationEnvelopeV1 ParseObservationEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryObservationEnvelopeV1 envelope;
        try { envelope = JsonSerializer.Deserialize<RecoveryObservationEnvelopeV1>(bytes, JsonOptions) ?? throw new InvalidDataException("observation_envelope_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("observation_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeCanonical(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("observation_envelope_noncanonical");
        return envelope;
    }

    public static RecoveryObservationPayloadV1 ParseCanonicalObservation(ReadOnlySpan<byte> bytes)
    {
        RecoveryObservationPayloadV1 payload;
        try { payload = JsonSerializer.Deserialize<RecoveryObservationPayloadV1>(bytes, JsonOptions) ?? throw new InvalidDataException("observation_payload_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("observation_payload_invalid_json", exception); }
        payload.Validate();
        if (!payload.SerializeCanonical().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("observation_payload_noncanonical");
        return payload;
    }

    public static VerifiedRecoveryObservation VerifyObservation(RecoveryObservationEnvelopeV1 envelope, RecoverySigningTrust trust)
    {
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var payload = ParseCanonicalObservation(bytes);
        var signature = VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(payload, bytes, Hash(bytes), envelope.WorkerId, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }

    public static RecoveryObservationEnvelopeV1 CreateObservationEnvelope(
        RecoveryObservationPayloadV1 payload,
        string workerId,
        RecoveryEvidenceSignature signature)
    {
        payload.Validate();
        var bytes = payload.SerializeCanonical();
        var envelope = new RecoveryObservationEnvelopeV1(1, "observation", workerId, signature.KeyId, signature.PublicKeySha256,
            Hash(bytes), Convert.ToBase64String(bytes), Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] VerifyDetached(
        ReadOnlySpan<byte> bytes,
        string payloadSha256,
        string signatureBase64,
        string signingKeyId,
        string signingPublicKeySha256,
        RecoverySigningTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var publicKey = trust.ValidateAndGetPublicKey();
        if (signingKeyId != trust.KeyId || signingPublicKeySha256 != trust.PublicKeySha256)
            throw new InvalidDataException("untrusted_evidence_signer");
        var digest = SHA256.HashData(bytes);
        if (Convert.ToHexString(digest).ToLowerInvariant() != payloadSha256)
            throw new InvalidDataException("evidence_payload_digest_mismatch");
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_evidence_signature", exception); }
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (!rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("evidence_signature_invalid");
        return signature;
    }

    public static byte[] VerifyDetachedDigest(
        string payloadSha256,
        string signatureBase64,
        string signingKeyId,
        string signingPublicKeySha256,
        RecoverySigningTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        RecoveryValidation.Hash(payloadSha256, nameof(payloadSha256));
        var publicKey = trust.ValidateAndGetPublicKey();
        if (signingKeyId != trust.KeyId || signingPublicKeySha256 != trust.PublicKeySha256)
            throw new InvalidDataException("untrusted_evidence_signer");
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("invalid_evidence_signature", exception);
        }
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (!rsa.VerifyHash(Convert.FromHexString(payloadSha256), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("evidence_signature_invalid");
        return signature;
    }

    public static void ValidateSafeCodes(IReadOnlyList<string>? codes)
    {
        if (codes is null || codes.Count > 100 || codes.Any(code => code is null) ||
            codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
            throw new InvalidDataException("invalid_safe_codes");
        foreach (var code in codes)
        {
            if (code.Length is < 1 or > 100 || code.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-')))
                throw new InvalidDataException("invalid_safe_code");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new FixedUtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class FixedUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (!DateTimeOffset.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new JsonException("timestamp_not_canonical_utc");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero) throw new JsonException("timestamp_not_utc");
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}
