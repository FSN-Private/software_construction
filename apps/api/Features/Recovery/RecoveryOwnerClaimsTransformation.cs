using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Platform.Api.Features.Recovery;

/// <summary>
/// Recovery-only transformation runs after JWT issuer/audience/signature validation and before
/// owner-scoped handlers. It rewrites the principal only after independently signed mapping checks.
/// </summary>
public sealed class RecoveryOwnerClaimsTransformation(
    RecoveryOwnerResolver resolver,
    IConfiguration configuration,
    ILogger<RecoveryOwnerClaimsTransformation> logger) : IClaimsTransformation
{
    private const string SourceSubjectClaim = "platform:recovery-source-subject";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (!configuration.GetValue<bool>("Recovery:RecoveryOnly") || principal.Identity?.IsAuthenticated is not true)
            return principal;
        if (resolver.HasVerifiedPrincipal(principal)) return principal;

        var sourceSubject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? string.Empty;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var preservedOwner = await resolver.ResolveAsync(principal, timeout.Token);
            if (string.IsNullOrWhiteSpace(preservedOwner)) return new ClaimsPrincipal(new ClaimsIdentity());

            var identities = principal.Identities.Select(identity =>
            {
                var claims = identity.Claims.Where(claim =>
                    claim.Type != ClaimTypes.NameIdentifier &&
                    claim.Type != "sub" &&
                    !claim.Type.StartsWith("platform:recovery-", StringComparison.Ordinal)).ToList();
                claims.Add(new Claim(SourceSubjectClaim, sourceSubject));
                claims.Add(new Claim(ClaimTypes.NameIdentifier, preservedOwner));
                return new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
            }).ToArray();
            var mappedPrincipal = new ClaimsPrincipal(identities);
            resolver.RememberVerifiedPrincipal(mappedPrincipal, preservedOwner);
            return mappedPrincipal;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Recovery identity mapping lookup timed out {RecoveryCode}", "identity_unavailable");
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
        catch (NpgsqlException)
        {
            logger.LogWarning("Recovery identity mapping lookup failed {RecoveryCode}", "identity_unavailable");
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
