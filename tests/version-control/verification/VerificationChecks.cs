using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts;
using Platform.Contracts.VersionControl;

internal static class VerificationChecks
{
    public static void Run()
    {
        var asset = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var content = new string('a', 64);
        var manifest = new VersionControlManifest("library", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "root", [new(asset, null, "added", null, content)]);
        var bytes = VersionControlManifestCodec.Write(manifest);
        var id = VersionControlManifestCodec.Digest(bytes);
        var stored = new StoredCommit(id, "library", bytes, []);
        Check(GraphIntegrity.Verify("library", [stored], new HashSet<string> { content }).Healthy, "Complete graph evidence must verify.");
        Check(GraphIntegrity.Verify("library", [stored], new HashSet<string>()).Codes.Contains("missing_content"), "Missing XMP content must quarantine graph refs.");
        Check(GraphIntegrity.Verify("another-library", [stored]).Codes.Contains("cross_library_reference"), "Cross-library commits must be rejected.");
        Check(GraphIntegrity.Verify("library", [stored with { ManifestBytes = [.. bytes, 0] }]).Codes.Contains("manifest_digest_mismatch"), "Altered manifests must be detected.");
        var noncanonical = new byte[] { (byte)' ' }.Concat(bytes).ToArray();
        Check(GraphIntegrity.Verify("library", [stored with { Id = VersionControlManifestCodec.Digest(noncanonical), ManifestBytes = noncanonical }]).Codes.Contains("manifest_noncanonical"), "Semantically valid non-canonical manifests must be rejected.");
        var cycle = new[] { new StoredCommit("a", "library", bytes, ["b"]), new StoredCommit("b", "library", bytes, ["a"]) };
        Check(GraphIntegrity.Verify("library", cycle).Codes.Contains("graph_cycle"), "Cycles must be detected without unbounded traversal.");

        var graph = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        string? parent = null;
        for (var index = 0; index < 10_000; index++)
        {
            var node = index.ToString("D5", CultureInfo.InvariantCulture);
            graph[node] = new(node, parent is null ? [] : [parent], "owner", DateTimeOffset.UnixEpoch.AddSeconds(index), DateTimeOffset.UnixEpoch.AddSeconds(index), node, index == 0 ? [new(asset, null, "added", null, content, null, null)] : [], false);
            parent = node;
        }
        var timer = Stopwatch.StartNew();
        var page = GraphQueries.Project(graph, parent!, asset, false, 0, 100);
        timer.Stop();
        Check(page.Count == 100 && timer.Elapsed < TimeSpan.FromSeconds(2), "A 10,000-node image projection first page must complete within two seconds.");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(RefNames.Normalize("MAIN") == "main" && VersionControlManifestCodec.Digest(manifest) == id, "Names and hashes must be locale invariant.");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim("cognito:groups", "owner")], "test"));
        Check(OwnerAuthorization.TryGetOwner(owner, out var ownerId) && ownerId == "owner", "Only authenticated owners may enter version-control services.");
        Check(!OwnerAuthorization.TryGetOwner(new ClaimsPrincipal(), out _), "Anonymous graph access must fail closed.");
        Check(Audit.Redact("token=secret&commit=" + id) == "[redacted]&commit=" + id, "Audit redaction must remove credentials while retaining safe commit identity.");

        if (Environment.GetEnvironmentVariable("VERSION_CONTROL_CAPACITY") == "1") MillionEntryCapacity();
        else Console.WriteLine("Set VERSION_CONTROL_CAPACITY=1 for the one-million-entry model check.");
        Console.WriteLine($"Verification checks passed; 10k projection first page {timer.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static void MillionEntryCapacity()
    {
        var graph = new Dictionary<string, GraphCommit>(StringComparer.Ordinal);
        string? parent = null;
        for (var revision = 0; revision < 10; revision++)
        {
            var entries = new ChangeView[100_000];
            for (var assetNumber = 0; assetNumber < entries.Length; assetNumber++)
            {
                var asset = new Guid(assetNumber, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
                entries[assetNumber] = new(asset, null, revision == 0 ? "added" : "modified", revision == 0 ? null : (revision - 1).ToString("x64"), revision.ToString("x64"), null, null);
            }
            var id = "capacity-" + revision;
            graph[id] = new(id, parent is null ? [] : [parent], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, id, entries, false);
            parent = id;
        }
        var timer = Stopwatch.StartNew();
        var state = GraphQueries.State(graph, parent!);
        timer.Stop();
        Check(state.Count == 100_000, "One million commit entries across 100,000 assets must rebuild to the exact current state.");
        Console.WriteLine($"One-million-entry model check passed in {timer.Elapsed.TotalSeconds:F2} s.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
