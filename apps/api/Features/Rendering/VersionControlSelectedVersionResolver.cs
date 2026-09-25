using Platform.Api.Features.VersionControl.Query;
using Platform.Contracts.Rendering;
using Platform.Contracts.VersionControl;

namespace Platform.Api.Features.Rendering;

public sealed class VersionControlSelectedVersionResolver(QueryService queries) : ISelectedVersionResolver
{
    public async Task<SelectedVersionResolution> ResolveAsync(string ownerId, string libraryId, Guid assetId, Guid editId, string selectedVersion, CancellationToken cancellationToken = default)
    {
        if (!IsCommitId(selectedVersion)) return SelectedVersionResolution.Unavailable("selected_version_unavailable");
        try
        {
            var (head, state) = await queries.StateAsync(ownerId, libraryId, selectedVersion, cancellationToken);
            return FromState(head, selectedVersion, state, assetId, editId);
        }
        catch (VersionControlProblem error) when (error.Code is "commit_not_found" or "graph_corrupt")
        {
            return SelectedVersionResolution.Unavailable(error.Code == "commit_not_found" ? "selected_version_unavailable" : "version_resolution_unavailable");
        }
    }

    public static SelectedVersionResolution FromState(string resolvedCommitId, string selectedVersion, IReadOnlyDictionary<string, ChangeView> state, Guid assetId, Guid editId)
    {
        if (!string.Equals(resolvedCommitId, selectedVersion, StringComparison.Ordinal) || !state.TryGetValue($"{assetId:D}/{editId:D}", out var change) || change.Change == "deleted" || !IsSha256(change.ContentSha256))
            return SelectedVersionResolution.Unavailable("selected_version_unavailable");
        return new SelectedVersionResolution(true, resolvedCommitId, change.ContentSha256, null);
    }

    private static bool IsCommitId(string value) => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
