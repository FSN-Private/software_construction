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
}
