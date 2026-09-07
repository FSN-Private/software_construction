using System.Security.Cryptography;

namespace Platform.Storage;

public sealed record ContentAddress(string Sha256, long Length)
{
    public string AuthoritativeKey => $"sha256/{Sha256}";
    public bool IsValid => Length >= 0 && Sha256.Length == 64 && Sha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async Task<ContentAddress> FromAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long length = 0; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) { hash.AppendData(buffer, 0, read); length += read; }
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    public static async Task<ContentAddress> VerifyAsync(Stream stream, ContentAddress expected, CancellationToken cancellationToken = default)
    {
        if (!expected.IsValid) throw new ArgumentException("Expected SHA-256 and length are invalid.", nameof(expected));
        var actual = await FromAsync(stream, cancellationToken);
        if (actual != expected) throw new InvalidDataException("checksum_mismatch");
        return actual;
    }
}
