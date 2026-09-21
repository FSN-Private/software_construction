using System.Text;
using System.Text.RegularExpressions;
using Platform.Contracts.VersionControl;
using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Refs;

public static partial class RefNames
{
    public static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        if (!Valid().IsMatch(normalized) || normalized.Contains("..", StringComparison.Ordinal) || normalized.StartsWith("recovery/", StringComparison.Ordinal)) throw new VersionControlProblem("invalid_ref_name", 400);
        return normalized;
    }

    public static Task LockAsync(NpgsqlConnection connection, string owner, string library, string name, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\nref\n" + name);

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Valid();
}
