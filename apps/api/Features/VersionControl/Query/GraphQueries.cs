using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.VersionControl.Query;

public sealed record GraphCommit(string Id, IReadOnlyList<string> Parents, string Author, DateTimeOffset AuthoredAt, DateTimeOffset ReceivedAt, string Message, IReadOnlyList<ChangeView> Entries, bool Hidden);

public static class GraphQueries
{
    public static IReadOnlyList<GraphCommit> Project(IReadOnlyDictionary<string, GraphCommit> graph, string head, Guid? asset, bool showHidden, int offset, int limit)
    {
        if (!graph.ContainsKey(head)) throw new VersionControlProblem("graph_corrupt");
        var reachable = new List<GraphCommit>();
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(head);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!graph.TryGetValue(id, out var commit)) throw new VersionControlProblem("graph_corrupt");
            reachable.Add(commit);
            foreach (var parent in commit.Parents) queue.Enqueue(parent);
        }
        var included = asset is null ? reachable.Select(commit => commit.Id).ToHashSet(StringComparer.Ordinal) : PathsToMatchingAssets(graph, head, asset.Value);
        var ordered = reachable.Where(commit => included.Contains(commit.Id) && (showHidden || !commit.Hidden));
        return ordered.OrderByDescending(commit => commit.ReceivedAt).ThenBy(commit => commit.Id, StringComparer.Ordinal).Skip(offset).Take(Math.Clamp(limit, 1, 1000)).ToArray();
    }

    public static CompareView Compare(IReadOnlyDictionary<string, GraphCommit> graph, string ours, string theirs)
    {
        if (!graph.ContainsKey(ours) || !graph.ContainsKey(theirs)) throw new VersionControlProblem("commit_not_found", 404);
        var baseId = MergeBase(graph, ours, theirs);
        var baseState = baseId is null ? new Dictionary<string, ChangeView>() : State(graph, baseId);
        var oursState = State(graph, ours);
        var theirsState = State(graph, theirs);
        var changes = new List<DivergenceChangeView>();
        foreach (var key in baseState.Keys.Union(oursState.Keys).Union(theirsState.Keys).Order(StringComparer.Ordinal))
        {
            baseState.TryGetValue(key, out var baseEntry);
            oursState.TryGetValue(key, out var oursEntry);
            theirsState.TryGetValue(key, out var theirsEntry);
            if (Same(oursEntry, theirsEntry)) continue;
            var representative = oursEntry ?? theirsEntry ?? baseEntry!;
            changes.Add(new(representative.AssetId, representative.EditId, baseEntry?.ContentSha256, oursEntry?.ContentSha256, theirsEntry?.ContentSha256, baseEntry?.Path, oursEntry?.Path, theirsEntry?.Path, baseEntry?.Lifecycle, oursEntry?.Lifecycle, theirsEntry?.Lifecycle));
        }
        return new(baseId, ours, theirs, changes);
    }

    public static Dictionary<string, ChangeView> State(IReadOnlyDictionary<string, GraphCommit> graph, string head)
    {
        var chain = new List<GraphCommit>();
        var current = head;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            if (!graph.TryGetValue(current, out var commit)) throw new VersionControlProblem("graph_corrupt");
            chain.Add(commit);
            if (commit.Parents.Count == 0) break;
            current = commit.Parents[0];
        }
        var state = new Dictionary<string, ChangeView>(StringComparer.Ordinal);
        foreach (var commit in chain.AsEnumerable().Reverse())
        {
            foreach (var entry in commit.Entries)
            {
                var key = Key(entry);
                if (entry.Change == "deleted") state.Remove(key); else state[key] = entry;
            }
        }
        return state;
    }

    private static string? MergeBase(IReadOnlyDictionary<string, GraphCommit> graph, string ours, string theirs)
    {
        var oursAncestors = Ancestors(graph, ours);
        return Ancestors(graph, theirs).Where(pair => oursAncestors.ContainsKey(pair.Key)).OrderBy(pair => pair.Value + oursAncestors[pair.Key]).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault();
    }

    private static Dictionary<string, int> Ancestors(IReadOnlyDictionary<string, GraphCommit> graph, string head)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((head, 0));
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (result.TryGetValue(item.Id, out var depth) && depth <= item.Depth) continue;
            result[item.Id] = item.Depth;
            if (!graph.TryGetValue(item.Id, out var commit)) throw new VersionControlProblem("graph_corrupt");
            foreach (var parent in commit.Parents) queue.Enqueue((parent, item.Depth + 1));
        }
        return result;
    }

    private static HashSet<string> PathsToMatchingAssets(IReadOnlyDictionary<string, GraphCommit> graph, string head, Guid asset)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(head);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!reachable.Add(id)) continue;
            if (!graph.TryGetValue(id, out var commit)) throw new VersionControlProblem("graph_corrupt");
            foreach (var parent in commit.Parents)
            {
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(id);
                pending.Push(parent);
            }
        }
        var included = reachable.Where(id => graph[id].Entries.Any(entry => entry.AssetId == asset)).ToHashSet(StringComparer.Ordinal);
        var connecting = new Queue<string>(included);
        while (connecting.Count > 0)
        {
            var id = connecting.Dequeue();
            if (!children.TryGetValue(id, out var descendants)) continue;
            foreach (var child in descendants) if (included.Add(child)) connecting.Enqueue(child);
        }
        return included;
    }

    internal static string Key(ChangeView entry) => $"{entry.AssetId:D}/{entry.EditId?.ToString("D") ?? "source"}";
    private static bool Same(ChangeView? left, ChangeView? right) => left?.ContentSha256 == right?.ContentSha256 && left?.Path == right?.Path && left?.Lifecycle == right?.Lifecycle;
}
