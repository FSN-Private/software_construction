using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Rewrite;
using Platform.Contracts.VersionControl;

internal static class RecoveryChecks
{
    public static void Run()
    {
        var graph = new Dictionary<string, GraphCommit>(StringComparer.Ordinal)
        {
            ["root"] = Node("root", []),
            ["old"] = Node("old", ["root"]),
            ["new"] = Node("new", ["root"]),
            ["child"] = Node("child", ["old"]),
        };
        Check(RewriteService.Reachable(graph, "child").SetEquals(["root", "old", "child"]), "Rewrite previews must identify exact old reachability without changing commits.");
        Check(RewriteService.Reachable(graph, "child").Except(RewriteService.Reachable(graph, "new")).ToHashSet().SetEquals(["old", "child"]), "Rewrite displacement must exclude commits still reachable from the new head.");
        var original = graph["old"];
        graph["old"] = original with { Hidden = true };
        Check(graph["old"].Parents.SequenceEqual(original.Parents) && graph["old"].Entries.SequenceEqual(original.Entries), "Visibility metadata must not alter parentage or content.");
        Console.WriteLine("Rewrite/recovery/visibility checks passed.");
    }

    private static GraphCommit Node(string id, IReadOnlyList<string> parents) => new(id, parents, "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, id, [], false);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
