using System.Text.Json;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed record StoredCommit(
    string Id,
    string LibraryId,
    byte[] ManifestBytes,
    IReadOnlyList<string> Parents,
    string? AuthorId = null,
    DateTimeOffset? AuthoredAt = null,
    DateTimeOffset? ReceivedAt = null,
    string? Message = null,
    int? ContractVersion = null,
    int? PolicyVersion = null,
    IReadOnlyList<VersionControlEntry>? Entries = null);
public sealed record GraphIntegrityResult(bool Healthy, IReadOnlyList<string> Codes, int CommitsChecked);

public static class GraphIntegrity
{
    public static GraphIntegrityResult Verify(string libraryId, IEnumerable<StoredCommit> source, IReadOnlySet<string>? authoritativeContent = null)
    {
        var commits = source.ToDictionary(commit => commit.Id, StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits.Values)
        {
            if (!string.Equals(commit.LibraryId, libraryId, StringComparison.Ordinal)) codes.Add("cross_library_reference");
            if (!string.Equals(VersionControlManifestCodec.Digest(commit.ManifestBytes), commit.Id, StringComparison.Ordinal)) codes.Add("manifest_digest_mismatch");
            VersionControlManifest? manifest = null;
            try { manifest = VersionControlManifestCodec.Read(commit.ManifestBytes); }
            catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException) { codes.Add("manifest_invalid"); }
            if (manifest is not null && !commit.ManifestBytes.AsSpan().SequenceEqual(VersionControlManifestCodec.Write(manifest))) codes.Add("manifest_noncanonical");
            if (manifest is not null && (!string.Equals(manifest.LibraryId, libraryId, StringComparison.Ordinal) || !manifest.Parents.SequenceEqual(commit.Parents))) codes.Add("manifest_graph_mismatch");
            if (manifest is not null && commit.AuthorId is not null &&
                (!string.Equals(manifest.AuthorId, commit.AuthorId, StringComparison.Ordinal) ||
                 manifest.AuthoredAt != commit.AuthoredAt || manifest.ReceivedAt != commit.ReceivedAt ||
                 !string.Equals(manifest.Message, commit.Message, StringComparison.Ordinal) ||
                 manifest.ContractVersion != commit.ContractVersion || manifest.PolicyVersion != commit.PolicyVersion ||
                 commit.Entries is null || !manifest.Entries.SequenceEqual(commit.Entries))) codes.Add("manifest_normalization_mismatch");
            if (manifest is not null && authoritativeContent is not null && manifest.Entries.SelectMany(entry => new[] { entry.BaseContentSha256, entry.ContentSha256 }).Where(value => value is not null).Any(value => !authoritativeContent.Contains(value!))) codes.Add("missing_content");
            foreach (var parent in commit.Parents)
            {
                if (!commits.TryGetValue(parent, out var parentCommit)) codes.Add("missing_parent");
                else if (!string.Equals(parentCommit.LibraryId, libraryId, StringComparison.Ordinal)) codes.Add("cross_library_reference");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in commits.Keys)
        {
            if (HasCycle(id, commits, visiting, visited)) { codes.Add("graph_cycle"); break; }
        }

        if (codes.Count > 0) VersionControlTelemetry.IntegrityFailures.Add(1, new KeyValuePair<string, object?>("library", "redacted"));
        return new(codes.Count == 0, codes.Order(StringComparer.Ordinal).ToArray(), commits.Count);
    }

    private static bool HasCycle(string id, IReadOnlyDictionary<string, StoredCommit> commits, HashSet<string> visiting, HashSet<string> visited)
    {
        if (visited.Contains(id) || !commits.TryGetValue(id, out var commit)) return false;
        if (!visiting.Add(id)) return true;
        foreach (var parent in commit.Parents)
        {
            if (HasCycle(parent, commits, visiting, visited)) return true;
        }
        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
