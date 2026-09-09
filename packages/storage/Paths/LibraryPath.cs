using System.Text;

namespace Platform.Storage.Paths;

public static class LibraryPath
{
    private static readonly char[] Invalid = Enumerable.Range(0, 32).Select(static value => (char)value).Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToArray();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.StartsWith('\\') || value.Length > 1024) throw new ArgumentException("invalid_windows_path");
        var path = value.Replace('\\', '/').Normalize(NormalizationForm.FormC).Trim('/');
        if (string.IsNullOrWhiteSpace(path) || path.Split('/').Any(p => p is "." or ".." || p.Length == 0 || p.Length > 255 || p.IndexOfAny(Invalid) >= 0 || p.EndsWith(' ') || p.EndsWith('.') || Reserved.Contains(p.Split('.')[0]) || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(COM|LPT)[¹²³](\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new ArgumentException("invalid_windows_path");
        return path;
    }
    public static string CollisionKey(string value) => Normalize(value).ToUpperInvariant();
}
