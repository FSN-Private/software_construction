using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using Platform.Contracts;
using Platform.Recovery;

namespace Platform.Api.Features.Recovery;

/// <summary>The exact recovery-owner mapping fields independently approved by the recovery MFA role.</summary>
public sealed record RecoveryIdentityApprovalV1(
    int SchemaVersion,
    Guid MappingId,
    string Issuer,
    string Audience,
    string Subject,
    string PreservedOwnerId,
    string ApprovedBy,
    string ApprovalKeyId,
    DateTimeOffset ApprovedAtUtc);

public sealed class RecoveryOwnerResolver(NpgsqlDataSource source, IConfiguration configuration, TimeProvider? clock = null)
{
    private readonly ConditionalWeakTable<ClaimsPrincipal, VerifiedOwner> verifiedPrincipals = new();

    internal void RememberVerifiedPrincipal(ClaimsPrincipal principal, string owner) =>
        verifiedPrincipals.GetValue(principal, _ => new VerifiedOwner(owner));

    internal bool HasVerifiedPrincipal(ClaimsPrincipal principal) => verifiedPrincipals.TryGetValue(principal, out _);

    public async Task<string?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (verifiedPrincipals.TryGetValue(principal, out var verifiedOwner)) return verifiedOwner.Owner;
        if (!configuration.GetValue<bool>("Recovery:RecoveryOnly"))
            return OwnerAuthorization.TryGetOwner(principal, out var owner) ? owner : null;
        if (principal.Identity?.IsAuthenticated is not true) return null;

        var issuer = principal.FindFirstValue("iss");
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var audiences = principal.FindAll("aud").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) || audiences.Length == 0 ||
            !TryGetApprovalTrust(configuration, out var trust)) return null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT mapping_id,issuer,audience,subject,owner_id,approved_by,approval_key_id,
                   approval_receipt,approved_at_utc
            FROM recovery_identity_mappings
            WHERE issuer=$1 AND subject=$2 AND audience=ANY($3::text[]) AND revoked_at_utc IS NULL
            ORDER BY mapping_id
            LIMIT 2
            """, connection);
        command.Parameters.AddWithValue(issuer);
        command.Parameters.AddWithValue(subject);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = audiences });

        Guid mappingId;
        string bindingIssuer;
        string bindingAudience;
        string bindingSubject;
        string preservedOwner;
        string approvedBy;
        string approvalKeyId;
        byte[] signature;
        DateTimeOffset approvedAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            mappingId = reader.GetGuid(0);
            bindingIssuer = reader.GetString(1);
            bindingAudience = reader.GetString(2);
            bindingSubject = reader.GetString(3);
            preservedOwner = reader.GetString(4);
            approvedBy = reader.GetString(5);
            approvalKeyId = reader.GetString(6);
            signature = (byte[])reader[7];
            approvedAt = reader.GetFieldValue<DateTimeOffset>(8);
            if (await reader.ReadAsync(cancellationToken)) return null;
        }

        if (mappingId == Guid.Empty || approvalKeyId != trust.KeyId || approvedAt > (clock ?? TimeProvider.System).GetUtcNow() ||
            !audiences.Contains(bindingAudience, StringComparer.Ordinal)) return null;

        var approval = new RecoveryIdentityApprovalV1(1, mappingId, bindingIssuer, bindingAudience, bindingSubject,
            preservedOwner, approvedBy, approvalKeyId, approvedAt.ToUniversalTime());
        byte[] bytes;
        try { bytes = RecoveryEvidenceCodec.SerializeCanonical(approval); }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException) { return null; }

        try
        {
            var publicKey = trust.ValidateAndGetPublicKey();
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length ||
                !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) return null;
        }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException)
        {
            return null;
        }

        return OwnerAuthorization.TryGetRecoveredOwner(principal, bindingIssuer, bindingAudience, bindingSubject,
            preservedOwner, out var recoveredOwner) ? recoveredOwner : null;
    }

    private static bool TryGetApprovalTrust(IConfiguration config, out RecoverySigningTrust trust)
    {
        var keyId = config["Recovery:OwnerApprovalTrust:KeyId"];
        var publicKey = config["Recovery:OwnerApprovalTrust:PublicKeySpkiBase64"];
        var fingerprint = config["Recovery:OwnerApprovalTrust:PublicKeySha256"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(fingerprint))
        {
            trust = null!;
            return false;
        }
        trust = new RecoverySigningTrust(keyId, publicKey, fingerprint);
        try { _ = trust.ValidateAndGetPublicKey(); return true; }
        catch (InvalidDataException) { trust = null!; return false; }
    }

    private sealed record VerifiedOwner(string Owner);
}
