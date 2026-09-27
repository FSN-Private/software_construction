using System.Globalization;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl;

public static class RecentAuthentication
{
    public static void Require(HttpContext context, TimeProvider? clock = null)
    {
        // JWT bearer authentication has already verified the signature, issuer and audience.
        // A client-supplied header is never accepted as proof of recent authentication.
        var value = context.User.FindFirst("auth_time")?.Value;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw new VersionControlProblem("recent_authentication_required", 403);
        DateTimeOffset authenticatedAt;
        try { authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw new VersionControlProblem("recent_authentication_required", 403); }
        var age = (clock ?? TimeProvider.System).GetUtcNow() - authenticatedAt;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
            throw new VersionControlProblem("recent_authentication_required", 403);
    }
}
