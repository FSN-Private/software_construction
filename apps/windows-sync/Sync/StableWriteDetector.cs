using System.Security.Cryptography;

namespace Platform.WindowsSync.Sync;

public sealed record StableFile(string Sha256, long Length, DateTime LastWriteUtc);

public static class StableWriteDetector
{
    public static async Task<StableFile> WaitAsync(string path, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var first = Snapshot(path);
                await Task.Delay(interval, cancellationToken);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var second = new FileInfo(path);
                if (first.Length != second.Length || first.LastWriteTimeUtc != second.LastWriteTimeUtc) continue;
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                var final = Snapshot(path);
                if (second.Length == final.Length && second.LastWriteTimeUtc == final.LastWriteTimeUtc) return new(sha, final.Length, final.LastWriteTimeUtc);
            }
            catch (IOException) { }
            await Task.Delay(interval, cancellationToken);
        }
        throw new IOException("stable_write_timeout");
    }

    private static FileInfo Snapshot(string path) { var value = new FileInfo(path); value.Refresh(); return value; }
}
