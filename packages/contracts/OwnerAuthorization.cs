using System.Security.Claims;

namespace Platform.Contracts;

public static class OwnerAuthorization
{
    public static bool TryGetOwner(ClaimsPrincipal principal, out string ownerId)
    {
        ownerId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? string.Empty;
        return principal.Identity?.IsAuthenticated is true
            && principal.Claims.Any(claim => claim.Type == "cognito:groups" && claim.Value == "owner")
            && !string.IsNullOrWhiteSpace(ownerId);
    }

    /// <summary>
    /// Resolves an independently approved recovery binding after the recovery
    /// runtime has loaded it from its private catalog and verified its signed
    /// MFA receipt. The binding arguments must never come from request input.
    /// </summary>
    public static bool TryGetRecoveredOwner(
        ClaimsPrincipal principal,
        string bindingIssuer,
        string bindingAudience,
        string bindingSubject,
        string preservedOwnerId,
        out string ownerId)
    {
        ownerId = string.Empty;
        var tokenSubject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var tokenIssuer = principal.FindFirstValue("iss");
        var tokenAudiences = principal.FindAll("aud").Select(claim => claim.Value);
        if (principal.Identity?.IsAuthenticated is not true
            || !principal.Claims.Any(claim => claim.Type == "cognito:groups" && claim.Value == "owner")
            || string.IsNullOrWhiteSpace(tokenSubject)
            || !string.Equals(tokenSubject, bindingSubject, StringComparison.Ordinal)
            || !string.Equals(tokenIssuer, bindingIssuer, StringComparison.Ordinal)
            || !tokenAudiences.Contains(bindingAudience, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(preservedOwnerId)
            || string.IsNullOrWhiteSpace(bindingIssuer)
            || string.IsNullOrWhiteSpace(bindingAudience)
            || string.IsNullOrWhiteSpace(bindingSubject)) return false;
        ownerId = preservedOwnerId;
        return true;
    }
}
