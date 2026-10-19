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
    string CompatibilityMatrix,
    string SourceSha256,
    string XmpSha256)
{
    public string Key
    {
        get
        {
            Validate();
            var canonical = string.Join("\n", ["rendering-v1", OwnerId, LibraryId, AssetId.ToString("D"), EditId.ToString("D"), SelectedVersion, Rendition.ToString(), Profile, RendererRelease, RendererArtifactDigest, CompatibilityMatrix, SourceSha256, XmpSha256]);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerId) || string.IsNullOrWhiteSpace(LibraryId) || AssetId == Guid.Empty || EditId == Guid.Empty || string.IsNullOrWhiteSpace(SelectedVersion) || string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(RendererRelease) || string.IsNullOrWhiteSpace(CompatibilityMatrix) || !IsSha256(SourceSha256) || !IsSha256(XmpSha256))
            throw new ArgumentException("invalid_render_request");
        if (RendererArtifactDigest is not "unapproved" && !IsDigest(RendererArtifactDigest)) throw new ArgumentException("invalid_renderer_artifact_digest");
    }

    private static bool IsDigest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(static c => char.IsAsciiHexDigit(c) && char.IsLower(c) || char.IsDigit(c));
    private static bool IsSha256(string value) => value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c) && (char.IsLower(c) || char.IsDigit(c)));
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

/// <summary>Authenticated worker-to-API boundary; workers never write rendering metadata directly.</summary>
public interface IRenderWorkerCallback
{
    Task<bool> ClaimAsync(Guid requestId, Guid attemptId, CancellationToken cancellationToken = default);
    Task CompleteAsync(Guid requestId, Guid attemptId, string privateObjectKey, string privateObjectVersion, string outputSha256, long byteLength, string mediaType, RenderProvenance provenance, CancellationToken cancellationToken = default);
    Task FailAsync(Guid requestId, Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default);
}

/// <summary>Immutable SQS payload. It contains object versions, never paths, owner credentials, or database credentials.</summary>
public sealed record RenderQueueMessage(Guid RequestId, Guid AttemptId, RenditionRequest Request, RenderInputObject Original, RenderInputObject Xmp, string DerivativeKey, RenderResourcePolicy Policy, string? SourceFormat = null)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || AttemptId == Guid.Empty) throw new ArgumentException("invalid_render_queue_message");
        Request.Validate();
        Original.Validate();
        Xmp.Validate();
        Policy.Validate();
        if (Original.Sha256 == Xmp.Sha256 || DerivativeKey != $"derivatives/{Request.Key}.jpg") throw new ArgumentException("invalid_render_queue_message");
    }
}

public sealed record RenderInputObject(string Key, string Version, string Sha256, long ByteLength)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.StartsWith('/') || Key.Contains('\\') || Key.Split('/').Any(static part => part is "" or "." or "..") || string.IsNullOrWhiteSpace(Version) || !IsSha256(Sha256) || ByteLength < 1)
            throw new ArgumentException("invalid_render_input_object");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IRenderQueueDispatcher
{
    Task EnqueueAsync(RenderQueueMessage message, CancellationToken cancellationToken = default);
}

public sealed record RenderPublication(string ObjectKey, string ObjectVersion, string OutputSha256, long ByteLength, string MediaType)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ObjectKey) || ObjectKey.StartsWith('/') || ObjectKey.Contains('\\') || ObjectKey.Split('/').Any(static segment => segment is "" or "." or "..") || string.IsNullOrWhiteSpace(ObjectVersion) || !IsSha256(OutputSha256) || ByteLength < 1 || MediaType is not ("image/jpeg" or "image/png"))
            throw new ArgumentException("invalid_render_publication");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record RenderRequestView(Guid RequestId, Guid AttemptId, RenderState State, bool CacheHit, RenderProvenance? Provenance, RenderPublication? Publication, string? FailureCode);

/// <summary>Pure request-state invariant used by durable rendering persistence.</summary>
public sealed class RenderRequestLedger
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public RenderRequestView Request(RenditionRequest request)
    {
        request.Validate();
        if (entries.TryGetValue(request.Key, out var existing))
            return existing.View(existing.Attempt.State == RenderState.Completed);

        var entry = new Entry(request, Guid.NewGuid(), new RenderAttempt(Guid.NewGuid(), 1, RenderState.Pending));
        entries.Add(request.Key, entry);
        return entry.View(false);
    }

    public RenderRequestView Complete(Guid requestId, RenderProvenance provenance, RenderPublication publication)
    {
        provenance.Validate();
        publication.Validate();
        var entry = entries.Values.SingleOrDefault(value => value.RequestId == requestId) ?? throw new ArgumentException("render_request_not_found");
        if (entry.Attempt.State != RenderState.Running && entry.Attempt.State != RenderState.Pending) throw new InvalidOperationException("invalid_render_transition");
        if (provenance.SourceSha256 != entry.Request.SourceSha256 || provenance.XmpSha256 != entry.Request.XmpSha256 || provenance.SelectedVersion != entry.Request.SelectedVersion || provenance.RendererRelease != entry.Request.RendererRelease || provenance.RendererArtifactDigest != entry.Request.RendererArtifactDigest || provenance.Profile != entry.Request.Profile || provenance.CompatibilityMatrix != entry.Request.CompatibilityMatrix)
            throw new ArgumentException("render_provenance_mismatch");
        entry.Attempt = entry.Attempt.State == RenderState.Pending ? entry.Attempt.Transition(RenderState.Running).Transition(RenderState.Completed) : entry.Attempt.Transition(RenderState.Completed);
        entry.Provenance = provenance;
        entry.Publication = publication;
        return entry.View(false);
    }

    private sealed class Entry(RenditionRequest request, Guid requestId, RenderAttempt attempt)
    {
        public RenditionRequest Request { get; } = request;
        public Guid RequestId { get; } = requestId;
        public RenderAttempt Attempt { get; set; } = attempt;
        public RenderProvenance? Provenance { get; set; }
        public RenderPublication? Publication { get; set; }
        public RenderRequestView View(bool cacheHit) => new(RequestId, Attempt.Id, Attempt.State, cacheHit, Provenance, Publication, Attempt.FailureCode);
    }
}
