using Platform.Contracts.Lineage;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.Lineage;

public static class LineageProjection
{
    public static LineagePageView Build(Guid asset, string branch, string head, bool showHidden, int offset, int limit,
        IReadOnlyList<CommitView> fetched, IReadOnlyList<RefView> refs, IReadOnlySet<string> hiddenParents)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new VersionControlProblem("invalid_page", 400);
        var labels = refs.Where(reference => reference.CommitId.Length > 0)
            .GroupBy(reference => reference.CommitId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var nodes = fetched.Take(limit).Select(commit =>
        {
            labels.TryGetValue(commit.CommitId, out var matched);
            matched ??= [];
            return new LineageNodeView(commit,
                matched.Where(reference => reference.Kind == "branch").Select(reference => reference.Name).Order(StringComparer.Ordinal).ToArray(),
                matched.Where(reference => reference.Kind == "tag").Select(reference => reference.Name).Order(StringComparer.Ordinal).ToArray(),
                showHidden ? [] : commit.Parents.Where(hiddenParents.Contains).ToArray());
        }).ToArray();
        return new(asset, branch, head, showHidden, offset, fetched.Count > limit ? offset + limit : null, nodes);
    }
}
