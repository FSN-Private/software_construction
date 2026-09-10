using System.Text;
using System.Text.RegularExpressions;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl.Refs;

public static partial class RefNames
{
    public static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        if (!Valid().IsMatch(normalized) || normalized.Contains("..", StringComparison.Ordinal) || normalized.StartsWith("recovery/", StringComparison.Ordinal)) throw new VersionControlProblem("invalid_ref_name", 400);
        return normalized;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Valid();
}
