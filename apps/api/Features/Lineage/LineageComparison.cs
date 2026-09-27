using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.Rendering;
using Platform.Contracts.Lineage;
using Platform.Contracts.Rendering;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.Lineage;

public static class LineageComparison
{
    public static LineageComparisonView Build(IReadOnlyDictionary<string, GraphCommit> graph, Guid asset, string target, string source)
    {
        var compared = GraphQueries.Compare(graph, target, source);
        return new(target, source, compared.BaseCommitId,
            States(graph, target, asset), States(graph, source, asset),
            compared.Changes.Where(change => change.AssetId == asset).ToArray());
    }

    public static ImageVersionView WithPreviewStatus(ImageVersionView state, RenditionStatusView status)
    {
        if (status.State == RenderState.Completed && status.Provenance is null ||
            status.Provenance is { } provenance &&
            (provenance.SelectedVersion != state.CommitId || provenance.XmpSha256 != state.XmpSha256))
            return state with { PreviewState = "unavailable", PreviewRequestId = null, PreviewProvenance = null };
        return state with { PreviewState = status.State.ToString().ToLowerInvariant(), PreviewRequestId = status.RequestId, PreviewProvenance = status.Provenance };
    }

    private static IReadOnlyList<ImageVersionView> States(IReadOnlyDictionary<string, GraphCommit> graph, string commit, Guid asset) =>
        GraphQueries.State(graph, commit).Values.Where(change => change.AssetId == asset)
            .OrderBy(change => change.EditId)
            .Select(change => new ImageVersionView(commit, asset, change.EditId, change.ContentSha256, change.Path, change.Lifecycle,
                change.EditId.HasValue && change.ContentSha256 is not null ? "not_requested" : "unavailable"))
            .ToArray();
}
