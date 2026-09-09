using Platform.Storage;
using Platform.Storage.Reconstruction;
using System.Text.Json;

namespace Platform.Ingest.Worker.Verification;

public static class ManifestCommand
{
    public static async Task<int> RunAsync(string trustedPath, string recoveredPath, string objectDirectory, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var trusted = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(trustedPath, ct), options) ?? throw new InvalidDataException("invalid_manifest");
        var recovered = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(recoveredPath, ct), options) ?? throw new InvalidDataException("invalid_manifest");
        var observed = new Dictionary<string, ContentAddress>(StringComparer.Ordinal);
        var root = Path.GetFullPath(objectDirectory);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("linked_recovery_file");
            await using var stream = File.OpenRead(path);
            observed[Path.GetRelativePath(root, path).Replace('\\', '/')] = await ContentAddress.FromAsync(stream, ct);
        }
        var errors = ManifestVerifier.Verify(trusted, recovered, observed);
        Console.WriteLine(JsonSerializer.Serialize(new { verified = errors.Count == 0, errors }));
        return errors.Count == 0 ? 0 : 1;
    }
}
