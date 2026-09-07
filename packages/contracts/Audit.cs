using System.Text.RegularExpressions;

namespace Platform.Contracts;

public static partial class Audit
{
    public static string Redact(string value) => Secret().Replace(value, "[redacted]");
    [GeneratedRegex("(?i)(authorization|token|password|x-amz-signature)=[^&\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();
}
