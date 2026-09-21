using System.Diagnostics;
using Platform.Contracts.Rendering;

namespace Platform.Render.Worker;

public sealed record RenderJob(Guid RequestId, Guid AttemptId, RenditionRequest Request, RenderResourcePolicy Policy);

public sealed record ResolvedRenderInput(string OriginalPath, string XmpPath, string SourceSha256, string XmpSha256);

public interface IImmutableRenderSourceResolver
{
    Task<ResolvedRenderInput?> ResolveAsync(RenderJob job, RenderWorkspace workspace, CancellationToken cancellationToken = default);
}

public interface IPrivateDerivativeStore
{
    Task PublishAsync(RenderJob job, string outputPath, CancellationToken cancellationToken = default);
}

public interface IRenderQueue
{
    Task CompleteAsync(Guid attemptId, CancellationToken cancellationToken = default);
    Task FailAsync(Guid attemptId, string safeFailureCode, CancellationToken cancellationToken = default);
}

public sealed class RenderWorkspace : IAsyncDisposable
{
    private readonly string root;
    private bool disposed;

    private RenderWorkspace(string root, string path)
    {
        this.root = root;
        Path = path;
    }

    public string Path { get; }

    public static RenderWorkspace Create(string workspaceRoot)
    {
        var root = System.IO.Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new RenderWorkspace(root, path);
    }

    public string PrivatePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || System.IO.Path.GetFileName(fileName) != fileName) throw new ArgumentException("invalid_workspace_filename");
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, fileName));
        if (!candidate.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("invalid_workspace_filename");
        return candidate;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            var expectedRoot = root + System.IO.Path.DirectorySeparatorChar;
            if (!Path.StartsWith(expectedRoot, StringComparison.Ordinal)) throw new InvalidOperationException("invalid_workspace_root");
            Directory.Delete(Path, true);
            disposed = true;
        }
        return ValueTask.CompletedTask;
    }
}

public sealed record RendererCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) || !System.IO.Path.IsPathFullyQualified(Executable) || string.IsNullOrWhiteSpace(WorkingDirectory) || !System.IO.Path.IsPathFullyQualified(WorkingDirectory) || Arguments.Any(static value => string.IsNullOrWhiteSpace(value) || value.Contains('\0')))
            throw new ArgumentException("invalid_renderer_command");
    }
}

public sealed record RendererExecutionResult(bool Succeeded, string? SafeFailureCode)
{
    public static RendererExecutionResult Success() => new(true, null);
    public static RendererExecutionResult Failed(string code) => new(false, RenderFailureCodes.IsSafe(code) ? code : throw new ArgumentException("invalid_render_failure_code"));
}

public sealed class ConstrainedRendererExecutor
{
    public async Task<RendererExecutionResult> ExecuteAsync(RendererCommand command, RenderResourcePolicy policy, CancellationToken cancellationToken = default)
    {
        command.Validate();
        policy.Validate();
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory,
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["TZ"] = "UTC";

        using var process = new Process { StartInfo = start };
        if (!process.Start()) return RendererExecutionResult.Failed("renderer_timeout");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(policy.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode == 0 ? RendererExecutionResult.Success() : RendererExecutionResult.Failed("output_invalid");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            return RendererExecutionResult.Failed("renderer_timeout");
        }
    }
}
