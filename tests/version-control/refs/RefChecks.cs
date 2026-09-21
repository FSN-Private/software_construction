using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;

internal static class RefChecks
{
    public static void Run()
    {
        Check(RefNames.Normalize("  Feature/EDIT  ") == "feature/edit", "Ref names must normalize deterministically.");
        Reject(() => RefNames.Normalize("bad..name"), "Traversal-like ref names must be rejected.");
        Reject(() => RefNames.Normalize("recovery/user-name"), "Reserved recovery names must be rejected.");

        var asset = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var edit = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var graph = new Dictionary<string, GraphCommit>(StringComparer.Ordinal)
        {
            ["a"] = Node("a", [], 0, [Entry(asset, edit, null, "a")]),
            ["b"] = Node("b", ["a"], 1, [Entry(asset, edit, "a", "b")]),
            ["c"] = Node("c", ["a"], 2, [Entry(asset, edit, "a", "c")]),
            ["d"] = Node("d", ["b", "c"], 3, [Entry(asset, edit, "b", "d")]),
        };
        var comparison = GraphQueries.Compare(graph, "b", "c");
        Check(comparison.BaseCommitId == "a" && comparison.Changes is [{ BaseContentSha256: "a", OursContentSha256: "b", TheirsContentSha256: "c" }], "Divergence must expose exact base, ours, and theirs identities.");
        var projection = GraphQueries.Project(graph, "d", asset, false, 0, 100);
        Check(projection.Select(value => value.Id).ToHashSet().SetEquals(["a", "b", "c", "d"]), "Image projections must retain required merge context.");
        graph["b"] = graph["b"] with { Hidden = true };
        Check(!GraphQueries.Project(graph, "d", asset, false, 0, 100).Any(value => value.Id == "b") && GraphQueries.Project(graph, "d", asset, true, 0, 100).Any(value => value.Id == "b"), "Hidden commits must affect presentation only.");
        Console.WriteLine("Ref/compare/projection checks passed.");
    }

    private static GraphCommit Node(string id, IReadOnlyList<string> parents, int seconds, IReadOnlyList<ChangeView> entries) => new(id, parents, "owner", DateTimeOffset.UnixEpoch.AddSeconds(seconds), DateTimeOffset.UnixEpoch.AddSeconds(seconds), id, entries, false);
    private static ChangeView Entry(Guid asset, Guid edit, string? before, string? after) => new(asset, edit, after is null ? "deleted" : before is null ? "added" : "modified", before, after, null, null);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (VersionControlProblem) { return; } throw new InvalidOperationException(message); }
}
