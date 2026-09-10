using System.Diagnostics.Metrics;
using System.Text.Json;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed record StoredCommit(string Id, string LibraryId, byte[] ManifestBytes, IReadOnlyList<string> Parents);
public sealed record GraphIntegrityResult(bool Healthy, IReadOnlyList<string> Codes, int CommitsChecked);

public static class GraphIntegrity
{
    private static readonly Meter Meter = new("Platform.VersionControl");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("platform.version_control.integrity_failures");

    public static GraphIntegrityResult Verify(string libraryId, IEnumerable<StoredCommit> source)
    {
        var commits = source.ToDictionary(commit => commit.Id, StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits.Values)
        {
            if (!string.Equals(commit.LibraryId, libraryId, StringComparison.Ordinal)) codes.Add("cross_library_reference");
            if (!string.Equals(VersionControlManifestCodec.Digest(commit.ManifestBytes), commit.Id, StringComparison.Ordinal)) codes.Add("manifest_digest_mismatch");
            VersionControlManifest? manifest = null;
            try { manifest = VersionControlManifestCodec.Read(commit.ManifestBytes); }
            catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { codes.Add("manifest_invalid"); }
            if (manifest is not null && (!string.Equals(manifest.LibraryId, libraryId, StringComparison.Ordinal) || !manifest.Parents.SequenceEqual(commit.Parents))) codes.Add("manifest_graph_mismatch");
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

        if (codes.Count > 0) Failures.Add(1, new KeyValuePair<string, object?>("library", "redacted"));
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
