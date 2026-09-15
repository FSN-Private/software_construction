using System.Collections.Concurrent;

namespace Platform.WindowsSync.Remote;

public sealed class SyncDiagnostics
{
    private readonly ConcurrentDictionary<string, (long Count, long Failures, long Milliseconds)> metrics = new(StringComparer.Ordinal);

    public void Record(string operation, TimeSpan duration, bool success) => metrics.AddOrUpdate(operation, _ => (1, success ? 0 : 1, (long)duration.TotalMilliseconds), (_, current) => (current.Count + 1, current.Failures + (success ? 0 : 1), current.Milliseconds + (long)duration.TotalMilliseconds));
    public IReadOnlyDictionary<string, (long Count, long Failures, long Milliseconds)> Snapshot() => new Dictionary<string, (long, long, long)>(metrics, StringComparer.Ordinal);
    public static string SafeEvent(string operationId, string code) => $"operation={Sanitize(operationId)} code={Sanitize(code)}";
    private static string Sanitize(string value) => new(value.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(80).ToArray());
}
