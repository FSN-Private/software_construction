using Platform.Contracts.Rendering;

namespace Platform.Api.Features.Rendering;

/// <summary>Authenticated worker-to-API boundary; the worker never writes rendering metadata directly.</summary>
public interface IRenderWorkerCallback
{
    Task CompleteAsync(Guid requestId, Guid attemptId, string privateObjectKey, string privateObjectVersion, string outputSha256, long byteLength, string mediaType, RenderProvenance provenance, CancellationToken cancellationToken = default);
    Task FailAsync(Guid requestId, Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default);
}
