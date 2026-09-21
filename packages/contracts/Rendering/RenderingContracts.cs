using System.Security.Cryptography;
using System.Text;

namespace Platform.Contracts.Rendering;

public enum RenditionKind { Thumbnail512, Preview2048 }

public enum RenderState { Pending, Running, Completed, Unavailable, Failed, Retryable, Cancelled }

public sealed record RenditionRequest(
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid EditId,
    string SelectedVersion,
    RenditionKind Rendition,
    string Profile,
    string RendererRelease,
    string RendererArtifactDigest,
    string CompatibilityMatrix)
{
    public string Key
    {
        get
        {
            Validate();
            var canonical = string.Join("\n", ["rendering-v1", OwnerId, LibraryId, AssetId.ToString("D"), EditId.ToString("D"), SelectedVersion, Rendition.ToString(), Profile, RendererRelease, RendererArtifactDigest, CompatibilityMatrix]);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerId) || string.IsNullOrWhiteSpace(LibraryId) || AssetId == Guid.Empty || EditId == Guid.Empty || string.IsNullOrWhiteSpace(SelectedVersion) || string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(RendererRelease) || string.IsNullOrWhiteSpace(CompatibilityMatrix))
            throw new ArgumentException("invalid_render_request");
        if (RendererArtifactDigest is not "unapproved" && !IsDigest(RendererArtifactDigest)) throw new ArgumentException("invalid_renderer_artifact_digest");
    }

    private static bool IsDigest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(static c => char.IsAsciiHexDigit(c) && char.IsLower(c) || char.IsDigit(c));
}

public sealed record RenderProvenance(
    string SourceSha256,
    string XmpSha256,
    string SelectedVersion,
    string RendererRelease,
    string RendererArtifactDigest,
    string Profile,
    string CompatibilityMatrix,
    DateTimeOffset CompletedAtUtc)
{
    public void Validate()
    {
        if (!IsSha256(SourceSha256) || !IsSha256(XmpSha256) || string.IsNullOrWhiteSpace(SelectedVersion) || string.IsNullOrWhiteSpace(RendererRelease) || string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(CompatibilityMatrix) || CompletedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("invalid_render_provenance");
        if (RendererArtifactDigest is not "unapproved" && !RendererArtifactDigest.StartsWith("sha256:", StringComparison.Ordinal)) throw new ArgumentException("invalid_render_provenance");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c) && (char.IsLower(c) || char.IsDigit(c)));
}

public sealed record RenderAttempt(Guid Id, int Number, RenderState State, string? FailureCode = null)
{
    public RenderAttempt Transition(RenderState next, string? failureCode = null)
    {
        if (!CanTransition(State, next)) throw new InvalidOperationException("invalid_render_transition");
        if (next is RenderState.Failed or RenderState.Retryable or RenderState.Unavailable && !RenderFailureCodes.IsSafe(failureCode)) throw new ArgumentException("invalid_render_failure_code");
        if (next is RenderState.Completed or RenderState.Pending or RenderState.Running && failureCode is not null) throw new ArgumentException("invalid_render_failure_code");
        return this with { State = next, FailureCode = failureCode };
    }

    public static bool CanTransition(RenderState from, RenderState to) => (from, to) switch
    {
        (RenderState.Pending, RenderState.Running or RenderState.Unavailable or RenderState.Cancelled) => true,
        (RenderState.Running, RenderState.Completed or RenderState.Failed or RenderState.Retryable or RenderState.Cancelled) => true,
        (RenderState.Failed or RenderState.Retryable or RenderState.Unavailable, RenderState.Pending) => true,
        _ => false,
    };
}

public static class RenderFailureCodes
{
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "source_unavailable", "selected_version_unavailable", "version_resolution_unavailable", "incompatible_renderer",
        "malformed_xmp", "unsupported_source", "resource_limit", "renderer_timeout", "output_invalid", "storage_unavailable",
    };

    public static bool IsSafe(string? value) => value is not null && Values.Contains(value);
}

public sealed record RenderResourcePolicy(int TimeoutSeconds, int MemoryMiB, int EphemeralStorageMiB, int MaxConcurrentRenders)
{
    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 1800 || MemoryMiB is < 256 or > 30720 || EphemeralStorageMiB is < 1024 or > 204800 || MaxConcurrentRenders is < 1 or > 1000)
            throw new ArgumentException("invalid_render_resource_policy");
    }
}

public sealed record SelectedVersionResolution(bool IsAvailable, string? VersionId, string? XmpSha256, string? FailureCode)
{
    public static SelectedVersionResolution Current(string xmpSha256) => new(true, "current", xmpSha256, null);
    public static SelectedVersionResolution Unavailable(string code) => new(false, null, null, RenderFailureCodes.IsSafe(code) ? code : throw new ArgumentException("invalid_render_failure_code"));
}

public interface ISelectedVersionResolver
{
    Task<SelectedVersionResolution> ResolveAsync(string ownerId, string libraryId, Guid assetId, Guid editId, string selectedVersion, CancellationToken cancellationToken = default);
}
