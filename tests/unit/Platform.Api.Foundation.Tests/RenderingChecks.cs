using Platform.Contracts.Rendering;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.Rendering;
using Platform.Render.Worker;

internal static class RenderingChecks
{
    public static void Run()
    {
        var request = new RenditionRequest("owner-1", "library-1", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "current", RenditionKind.Thumbnail512, "render-v1", "5.6.1", "unapproved", "rendering-v1");
        var same = request with { };
        var historical = request with { SelectedVersion = "commit-123" };
        Check(request.Key == same.Key, "Identical render-affecting inputs must have one canonical key.");
        Check(request.Key != historical.Key, "Selected XMP version must affect the rendition key.");
        Check(request.Key != (request with { LibraryId = "library-2" }).Key, "Library scope must affect the rendition key.");
        Check(request.Key.Length == 64 && request.Key.All(char.IsAsciiHexDigit), "Rendition keys must be SHA-256 values.");
        var queueMessage = new RenderQueueMessage(Guid.NewGuid(), Guid.NewGuid(), request, new("sha256/" + new string('a', 64), "source-v1", new string('a', 64), 1), new("sha256/" + new string('b', 64), "xmp-v1", new string('b', 64), 1), $"derivatives/{request.Key}.jpg", new(30, 1024, 2048, 1));
        queueMessage.Validate();
        AssertThrows(() => (queueMessage with { DerivativeKey = "derivatives/wrong.jpg" }).Validate(), "Queue work must retain its canonical private derivative key.");

        var pending = new RenderAttempt(Guid.NewGuid(), 1, RenderState.Pending);
        var running = pending.Transition(RenderState.Running);
        var completed = running.Transition(RenderState.Completed);
        Check(completed.State == RenderState.Completed && completed.FailureCode is null, "Completed attempts must not carry failure detail.");
        Check(!RenderAttempt.CanTransition(RenderState.Completed, RenderState.Pending), "Completed renditions must not be silently requeued.");
        Check(running.Transition(RenderState.Retryable, "renderer_timeout").FailureCode == "renderer_timeout", "Retryable attempts must retain a safe category.");
        AssertThrows(() => running.Transition(RenderState.Failed, "C:\\private\\token"), "Failure messages must be restricted to safe codes.");

        var unavailable = SelectedVersionResolution.Unavailable("version_resolution_unavailable");
        Check(!unavailable.IsAvailable && unavailable.XmpSha256 is null, "Missing version resolution must not substitute a current XMP hash.");
        Check(!RenderFailureCodes.IsSafe("C:\\private\\token"), "Raw paths must never be accepted as render failure codes.");

        var selectedVersion = new string('c', 64);
        var historicalState = new Dictionary<string, ChangeView>
        {
            [$"{request.AssetId:D}/{request.EditId:D}"] = new(request.AssetId, request.EditId, "modified", null, new string('d', 64), null, null),
        };
        var historicalResolution = VersionControlSelectedVersionResolver.FromState(selectedVersion, selectedVersion, historicalState, request.AssetId, request.EditId);
        Check(historicalResolution.IsAvailable && historicalResolution.XmpSha256 == new string('d', 64), "Historical resolution must return only the exact selected commit's XMP identity.");
        Check(!VersionControlSelectedVersionResolver.FromState(selectedVersion, new string('e', 64), historicalState, request.AssetId, request.EditId).IsAvailable, "A resolver must not substitute another commit for the selected version.");

        new RenderResourcePolicy(30, 1024, 2048, 2).Validate();
        AssertThrows(() => new RenderResourcePolicy(0, 1024, 2048, 2).Validate(), "Render policies must bound execution time.");
        new RenderProvenance(new string('a', 64), new string('b', 64), "current", "5.6.1", "unapproved", "render-v1", "rendering-v1", DateTimeOffset.UtcNow).Validate();

        var ledger = new RenderRequestLedger();
        var first = ledger.Request(request);
        var duplicate = ledger.Request(request);
        Check(first.State == RenderState.Pending && !first.CacheHit, "A first current-preview request must create pending work.");
        Check(first.RequestId == duplicate.RequestId && first.AttemptId == duplicate.AttemptId && !duplicate.CacheHit, "Duplicate pending requests must coalesce into one attempt.");
        Check(ledger.Request(request with { OwnerId = "owner-2" }).RequestId != first.RequestId, "Owner scope must prevent cross-owner cache reuse.");
        var provenance = new RenderProvenance(new string('a', 64), new string('b', 64), "current", "5.6.1", "unapproved", "render-v1", "rendering-v1", DateTimeOffset.UtcNow);
        var publication = new RenderPublication("private/owner-1/rendition.jpg", "version-1", new string('e', 64), 32, "image/jpeg");
        AssertThrows(() => new RenderPublication("C:\\private\\output.jpg", "version-1", new string('e', 64), 32, "image/jpeg").Validate(), "Derivative publication metadata must reject filesystem paths.");
        AssertThrows(() => ledger.Complete(first.RequestId, provenance with { SelectedVersion = "different" }, publication), "Completion must reject provenance that differs from the request.");
        var completedRequest = ledger.Complete(first.RequestId, provenance, publication);
        Check(completedRequest.State == RenderState.Completed && completedRequest.Provenance == provenance && completedRequest.Publication == publication, "A completed derivative and matching provenance must become visible together.");
        Check(ledger.Request(request).CacheHit, "An exact completed request must be a cache hit.");

        var root = Path.Combine(Path.GetTempPath(), "platform-render-tests", Guid.NewGuid().ToString("N"));
        RenderWorkspace? workspace = null;
        try
        {
            workspace = RenderWorkspace.Create(root);
            Check(workspace.PrivatePath("output.jpg").StartsWith(workspace.Path, StringComparison.Ordinal), "Worker paths must remain inside a private workspace.");
            AssertThrows(() => workspace.PrivatePath("../outside.jpg"), "Workspace path traversal must be rejected.");
            AssertThrows(() => new RendererCommand("darktable-cli", [], workspace.Path).Validate(), "Renderer execution must require an explicit absolute executable path.");
            var command = new DarktableCommandFactory().Create(new RenderJob(Guid.NewGuid(), Guid.NewGuid(), request, new(30, 1024, 2048, 1), queueMessage), new(workspace.PrivatePath("original"), workspace.PrivatePath("edit.xmp"), new string('a', 64), new string('b', 64)), workspace);
            Check(command.Arguments.SequenceEqual([workspace.PrivatePath("original"), workspace.PrivatePath("edit.xmp"), workspace.PrivatePath("output.jpg"), "--width", "512", "--height", "512", "--library", ":memory:"]), "Darktable must receive square rendition bounds and an in-memory library.");
        }
        finally
        {
            if (workspace is not null) workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Check(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Worker cleanup must remove temporary material.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
}
