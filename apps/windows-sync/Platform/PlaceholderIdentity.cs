using System.Buffers.Binary;
using System.Text;

namespace Platform.WindowsSync.Platform;

public sealed record PlaceholderIdentity(Guid AssetId, Guid? EditId, Guid ContentId, string Sha256, long Length)
{
    private const byte Version = 1;

    public byte[] Encode()
    {
        if (Sha256 is not { Length: 64 } || !Sha256.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || Length < 0)
            throw new ArgumentException("invalid_placeholder_identity");
        var bytes = new byte[1 + 16 + 1 + 16 + 16 + 32 + 8];
        bytes[0] = Version;
        AssetId.TryWriteBytes(bytes.AsSpan(1, 16));
        bytes[17] = EditId.HasValue ? (byte)1 : (byte)0;
        EditId.GetValueOrDefault().TryWriteBytes(bytes.AsSpan(18, 16));
        ContentId.TryWriteBytes(bytes.AsSpan(34, 16));
        Convert.FromHexString(Sha256).CopyTo(bytes, 50);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(82), Length);
        return bytes;
    }

    public static PlaceholderIdentity Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 90 || bytes[0] != Version || bytes[17] > 1) throw new InvalidDataException("invalid_placeholder_identity");
        var value = new PlaceholderIdentity(
            new Guid(bytes.Slice(1, 16)),
            bytes[17] == 1 ? new Guid(bytes.Slice(18, 16)) : null,
            new Guid(bytes.Slice(34, 16)),
            Convert.ToHexString(bytes.Slice(50, 32)).ToLowerInvariant(),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(82, 8)));
        _ = value.Encode();
        return value;
    }
}

public static class PathMapper
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ToLocalPath(string logicalPath, Guid identity)
    {
        if (string.IsNullOrWhiteSpace(logicalPath) || logicalPath.StartsWith('/') || logicalPath.StartsWith('\\'))
            throw new ArgumentException("unsupported_name", nameof(logicalPath));
        var segments = logicalPath.Replace('\\', '/').Normalize(NormalizationForm.FormC).Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or "..")) throw new ArgumentException("unsupported_name", nameof(logicalPath));
        var mapped = segments.Select(MapSegment).ToArray();
        var path = string.Join('/', mapped);
        if (path.Length > 1024) throw new ArgumentException($"unsupported_name:{identity:N}", nameof(logicalPath));
        return path;
    }

    public static string CollisionKey(string localPath) => localPath.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static string ResolveCollision(string localPath, Guid identity) =>
        Path.ChangeExtension(localPath, null) + "~" + identity.ToString("N")[..8] + Path.GetExtension(localPath);

    private static string MapSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
            if (c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '%') builder.Append('%').Append(((int)c).ToString("X4"));
            else builder.Append(c);
        while (builder.Length > 0 && builder[^1] is ' ' or '.')
        {
            var c = builder[^1];
            builder.Length--;
            builder.Append('%').Append(((int)c).ToString("X4"));
        }
        var value = builder.ToString();
        if (Reserved.Contains(value.Split('.')[0])) value = "~" + value;
        if (value.Length > 240) value = value[..220] + "~" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
        return value;
    }
}
