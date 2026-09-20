using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Platform.WindowsSync.Remote;

public sealed record RemoteItem(Guid AssetId, Guid? EditId, Guid ContentId, string Path, string Sha256, long Length, string? Lifecycle = null, long AssetVersion = 1, long? EditVersion = null);
public sealed record RemotePage(IReadOnlyList<RemoteItem> Items, string? Cursor, bool HasMore);
public sealed record RemoteMutation(string OperationId, string State, long Version, string? ContentSha256 = null, string? ConflictCode = null);
public sealed record CheckoutResult(string OperationId, string Branch, long Version, Guid? StashId);
public sealed record DeviceRegistration(string DeviceId, string LibraryId, string Branch, string? Cursor, long Version, long CheckoutVersion, bool Revoked, string OperationId);

public interface IRemoteSyncClient
{
    Task<DeviceRegistration> RegisterDeviceAsync(string library, string branch, CancellationToken cancellationToken);
    Task<DeviceRegistration> RevokeDeviceAsync(string library, CancellationToken cancellationToken);
    Task<RemotePage> EnumerateAsync(string library, string branch, string? cursor, CancellationToken cancellationToken);
    Task<Stream> DownloadAsync(string library, Guid contentId, CancellationToken cancellationToken);
    Task<RemoteMutation> UploadWorkingAsync(string library, string branch, string idempotencyKey, RemoteItem item, string? baseSha256, Stream content, CancellationToken cancellationToken);
    Task<RemoteMutation> ApplyLifecycleAsync(string kind, Guid assetId, Guid? editId, string idempotencyKey, long expectedVersion, string? destination, CancellationToken cancellationToken);
    Task<CheckoutResult> CheckoutAsync(string library, string branch, string dirtyAction, bool confirmDiscard, long expectedVersion, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class RemoteSyncClient(HttpClient http, string deviceId, Func<string> token, Action<string, TimeSpan, bool>? metric = null) : IRemoteSyncClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<DeviceRegistration> RegisterDeviceAsync(string library, string branch, CancellationToken cancellationToken) =>
        SendAsync<DeviceRegistration>(HttpMethod.Post, $"v1/libraries/{Uri.EscapeDataString(library)}/windows-sync/devices", null, new { Branch = branch }, cancellationToken);

    public Task<DeviceRegistration> RevokeDeviceAsync(string library, CancellationToken cancellationToken) =>
        SendAsync<DeviceRegistration>(HttpMethod.Delete, $"v1/libraries/{Uri.EscapeDataString(library)}/windows-sync/devices/current", null, null, cancellationToken);

    public async Task<RemotePage> EnumerateAsync(string library, string branch, string? cursor, CancellationToken cancellationToken)
    {
        var route = $"v1/libraries/{Uri.EscapeDataString(library)}/windows-sync/namespace?branch={Uri.EscapeDataString(branch)}&limit=1000";
        if (cursor is not null) route += "&cursor=" + Uri.EscapeDataString(cursor);
        var result = await SendAsync<RemotePage>(HttpMethod.Get, route, null, null, cancellationToken);
        return result;
    }

    public async Task<Stream> DownloadAsync(string library, Guid contentId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"v1/libraries/{Uri.EscapeDataString(library)}/windows-sync/content/{contentId}");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ProblemAsync(response, cancellationToken);
            response.Dispose();
            throw problem;
        }
        return new OwnedResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    public async Task<RemoteMutation> UploadWorkingAsync(string library, string branch, string idempotencyKey, RemoteItem item, string? baseSha256, Stream content, CancellationToken cancellationToken)
    {
        var edit = item.EditId.HasValue ? "/" + item.EditId.Value : "";
        var route = $"v1/libraries/{Uri.EscapeDataString(library)}/windows-sync/working/{item.AssetId}{edit}?branch={Uri.EscapeDataString(branch)}&path={Uri.EscapeDataString(item.Path)}";
        if (baseSha256 is not null) route += "&baseSha256=" + Uri.EscapeDataString(baseSha256);
        using var request = Request(HttpMethod.Put, route, idempotencyKey);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new("application/rdf+xml");
        request.Content.Headers.ContentLength = content.CanSeek ? content.Length : null;
        request.Headers.Add("X-Content-SHA256", item.Sha256);
        return await SendAsync<RemoteMutation>(request, cancellationToken);
    }

    public async Task<RemoteMutation> ApplyLifecycleAsync(string kind, Guid assetId, Guid? editId, string idempotencyKey, long expectedVersion, string? destination, CancellationToken cancellationToken)
    {
        var method = kind == "move" ? HttpMethod.Patch : kind == "delete" ? HttpMethod.Delete : HttpMethod.Post;
        var target = editId.HasValue && kind != "move" ? $"edits/{editId.Value}" : $"assets/{assetId}";
        var route = kind switch { "move" => $"v1/assets/{assetId}/path", "delete" => $"v1/{target}", "restore" => $"v1/{target}/restore", _ => throw new ArgumentException("invalid_lifecycle_kind", nameof(kind)) };
        using var request = Request(method, route, idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        if (method != HttpMethod.Delete) request.Content = JsonContent.Create(new { Path = destination }, options: Json);
        var result = await SendAsync<LifecycleResponse>(request, cancellationToken);
        return new("remote-lifecycle", result.State, result.LifecycleVersion);
    }

    public async Task<CheckoutResult> CheckoutAsync(string library, string branch, string dirtyAction, bool confirmDiscard, long expectedVersion, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, $"v1/libraries/{Uri.EscapeDataString(library)}/version-control/checkout", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        request.Content = JsonContent.Create(new { Branch = branch, DirtyAction = dirtyAction, ConfirmDiscard = confirmDiscard, ExpectedVersion = expectedVersion }, options: Json);
        var result = await SendAsync<CheckoutResponse>(request, cancellationToken);
        return new(result.OperationId, result.RefName ?? branch, result.Version ?? throw new InvalidDataException("checkout_version_missing"), result.StashId);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, string? idempotencyKey, object? body, CancellationToken cancellationToken)
    {
        using var request = Request(method, route, idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw await ProblemAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new InvalidDataException("empty_remote_response");
            succeeded = true;
            return result;
        }
        finally { metric?.Invoke(request.Method.Method, System.Diagnostics.Stopwatch.GetElapsedTime(started), succeeded); }
    }

    private HttpRequestMessage Request(HttpMethod method, string route, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
        request.Headers.Add("X-Client-Id", deviceId);
        request.Headers.Add("X-Operation-Id", "ws_" + Guid.NewGuid().ToString("N"));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<RemoteSyncException> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string code;
        try { code = (await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken))?.Code ?? "remote_error"; }
        catch (JsonException) { code = "remote_error"; }
        return new(code, response.StatusCode, response.Headers.TryGetValues("X-Operation-Id", out var values) ? values.FirstOrDefault() : null);
    }

    private sealed record Problem(string? Code);
    private sealed record CheckoutResponse(string OperationId, string? RefName, long? Version, Guid? StashId);
    private sealed record LifecycleResponse(Guid Id, string State, long LifecycleVersion, DateTime? DeletedUntil, string? Path);
}

public sealed class RemoteSyncException(string code, HttpStatusCode status, string? operationId) : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
    public string? OperationId { get; } = operationId;
    public bool IsConflict => Status == HttpStatusCode.Conflict;
    public bool IsAuthenticationRequired => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

internal sealed class OwnedResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false; public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
}
