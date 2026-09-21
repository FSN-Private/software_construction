using Platform.Contracts.Rendering;
using Platform.Render.Worker;

internal static class RenderingChecks
{
    public static void Run()
    {
        var request = new RenditionRequest("owner-1", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "current", RenditionKind.Thumbnail512, "render-v1", "5.6.1", "unapproved", "rendering-v1");
        var same = request with { };
        var historical = request with { SelectedVersion = "commit-123" };
        Check(request.Key == same.Key, "Identical render-affecting inputs must have one canonical key.");
        Check(request.Key != historical.Key, "Selected XMP version must affect the rendition key.");
        Check(request.Key.Length == 64 && request.Key.All(char.IsAsciiHexDigit), "Rendition keys must be SHA-256 values.");

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

        new RenderResourcePolicy(30, 1024, 2048, 2).Validate();
        AssertThrows(() => new RenderResourcePolicy(0, 1024, 2048, 2).Validate(), "Render policies must bound execution time.");
        new RenderProvenance(new string('a', 64), new string('b', 64), "current", "5.6.1", "unapproved", "render-v1", "rendering-v1", DateTimeOffset.UtcNow).Validate();

        var root = Path.Combine(Path.GetTempPath(), "platform-render-tests", Guid.NewGuid().ToString("N"));
        RenderWorkspace? workspace = null;
        try
        {
            workspace = RenderWorkspace.Create(root);
            Check(workspace.PrivatePath("output.jpg").StartsWith(workspace.Path, StringComparison.Ordinal), "Worker paths must remain inside a private workspace.");
            AssertThrows(() => workspace.PrivatePath("../outside.jpg"), "Workspace path traversal must be rejected.");
            AssertThrows(() => new RendererCommand("darktable-cli", [], workspace.Path).Validate(), "Renderer execution must require an explicit absolute executable path.");
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
