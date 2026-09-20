using System.Collections.Concurrent;

namespace Platform.WindowsSync.Provider;

internal static class ProviderMutationTracker
{
    private static readonly ConcurrentDictionary<string, long> Suppressed = new(StringComparer.OrdinalIgnoreCase);

    public static void Suppress(string path) => Suppressed[Path.GetFullPath(path)] = Environment.TickCount64 + 10_000;

    public static bool Consume(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Suppressed.TryRemove(fullPath, out var expires)) return false;
        return Environment.TickCount64 <= expires;
    }
}
