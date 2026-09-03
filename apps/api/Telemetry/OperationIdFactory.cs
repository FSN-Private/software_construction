using System.Text.RegularExpressions;

namespace Platform.Api.Telemetry;

public sealed partial class OperationIdFactory
{
    private const string Prefix = "op_";

    public string Resolve(string? suppliedValue) =>
        suppliedValue is not null && OperationIdPattern().IsMatch(suppliedValue)
            ? suppliedValue
            : $"{Prefix}{Guid.NewGuid():N}";

    [GeneratedRegex("^op_[A-Za-z0-9_-]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdPattern();
}
