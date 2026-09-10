using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Platform.Contracts.VersionControl;

public sealed record VersionControlEntry(
    Guid AssetId,
    Guid? EditId,
    string Change,
    string? BaseContentSha256,
    string? ContentSha256,
    string? Path = null,
    string? Lifecycle = null);

public sealed record VersionControlManifest(
    string LibraryId,
    IReadOnlyList<string> Parents,
    string AuthorId,
    DateTimeOffset AuthoredAt,
    DateTimeOffset ReceivedAt,
    string Message,
    IReadOnlyList<VersionControlEntry> Entries,
    int PolicyVersion = 1,
    int ContractVersion = 1);

public static class VersionControlManifestCodec
{
    public const int CurrentVersion = 1;

    public static byte[] Write(VersionControlManifest manifest)
    {
        Validate(manifest);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", manifest.ContractVersion);
            writer.WriteString("libraryId", manifest.LibraryId);
            writer.WriteStartArray("parents");
            foreach (var parent in manifest.Parents) writer.WriteStringValue(parent);
            writer.WriteEndArray();
            writer.WriteString("authorId", manifest.AuthorId);
            writer.WriteString("authoredAt", Utc(manifest.AuthoredAt));
            writer.WriteString("receivedAt", Utc(manifest.ReceivedAt));
            writer.WriteString("message", manifest.Message);
            writer.WriteNumber("policyVersion", manifest.PolicyVersion);
            writer.WriteStartArray("entries");
            foreach (var entry in manifest.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("assetId", entry.AssetId);
                if (entry.EditId is { } editId) writer.WriteString("editId", editId); else writer.WriteNull("editId");
                writer.WriteString("change", entry.Change);
                WriteNullable(writer, "baseContentSha256", entry.BaseContentSha256);
                WriteNullable(writer, "contentSha256", entry.ContentSha256);
                WriteNullable(writer, "path", entry.Path);
                WriteNullable(writer, "lifecycle", entry.Lifecycle);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static VersionControlManifest Read(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        var version = root.GetProperty("contractVersion").GetInt32();
        if (version != CurrentVersion) throw new NotSupportedException($"Unsupported version-control manifest version {version}.");
        var manifest = new VersionControlManifest(
            root.GetProperty("libraryId").GetString()!,
            root.GetProperty("parents").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            root.GetProperty("authorId").GetString()!,
            root.GetProperty("authoredAt").GetDateTimeOffset(),
            root.GetProperty("receivedAt").GetDateTimeOffset(),
            root.GetProperty("message").GetString()!,
            root.GetProperty("entries").EnumerateArray().Select(ReadEntry).ToArray(),
            root.GetProperty("policyVersion").GetInt32(),
            version);
        Validate(manifest);
        return manifest;
    }

    public static string Digest(ReadOnlySpan<byte> canonicalBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));

    public static string Digest(VersionControlManifest manifest) => Digest(Write(manifest));

    private static VersionControlEntry ReadEntry(JsonElement entry) => new(
        entry.GetProperty("assetId").GetGuid(),
        entry.GetProperty("editId").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("editId").GetGuid(),
        entry.GetProperty("change").GetString()!,
        NullableString(entry, "baseContentSha256"),
        NullableString(entry, "contentSha256"),
        NullableString(entry, "path"),
        NullableString(entry, "lifecycle"));

    private static string? NullableString(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetString();

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void Validate(VersionControlManifest manifest)
    {
        if (manifest.ContractVersion != CurrentVersion) throw new NotSupportedException($"Unsupported version-control manifest version {manifest.ContractVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.LibraryId) || string.IsNullOrWhiteSpace(manifest.AuthorId) || string.IsNullOrWhiteSpace(manifest.Message)) throw new ArgumentException("Library, author, and message are required.");
        if (manifest.PolicyVersion < 1 || manifest.Entries.Count == 0 || manifest.Parents.Count > 2) throw new ArgumentException("Manifest policy, entries, or parents are invalid.");
        if (manifest.Parents.Any(value => !IsDigest(value))) throw new ArgumentException("Parent commit identities must be lowercase SHA-256 values.");
        if (manifest.Parents.Distinct(StringComparer.Ordinal).Count() != manifest.Parents.Count) throw new ArgumentException("Commit parents must be distinct.");
        foreach (var entry in manifest.Entries)
        {
            if (entry.Change is not ("added" or "modified" or "deleted" or "conflicted")) throw new ArgumentException("Unsupported change state.");
            if (entry.BaseContentSha256 is not null && !IsDigest(entry.BaseContentSha256) || entry.ContentSha256 is not null && !IsDigest(entry.ContentSha256)) throw new ArgumentException("XMP identities must be lowercase SHA-256 values.");
            if (entry.Change != "deleted" && entry.ContentSha256 is null) throw new ArgumentException("Non-deleted entries require current content.");
        }
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
