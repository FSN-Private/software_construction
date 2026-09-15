using System.Security.Cryptography;
using System.Text;

namespace Platform.WindowsSync.State;

public sealed class CredentialStore(string path)
{
    private static readonly byte[] Entropy = "software-construction/windows-sync/v1"u8.ToArray();

    public void Save(string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".new";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
    }

    public string Load()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete() { if (File.Exists(path)) File.Delete(path); }
}
