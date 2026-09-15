using System.Net;
using System.Net.Http.Json;
using Platform.Api.Features.WindowsSync;
using Platform.WindowsSync.Remote;

static class ContractChecks
{
    public static async Task RunAsync()
    {
        var cursor = new WindowsSyncCursor(new string('a', 64), Guid.NewGuid(), Guid.NewGuid());
        TestAssert.That(WindowsSyncCursor.Decode(WindowsSyncCursor.Encode(cursor)) == cursor, "The server cursor must round-trip opaquely.");

        var requests = new List<(HttpMethod Method, string Route, string? Client, string? Idempotency, string? Digest, byte[] Body)>();
        var handler = new RecordingHandler(async request =>
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync();
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.GetValues("X-Client-Id").Single(), request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null, request.Headers.TryGetValues("X-Content-SHA256", out var digests) ? digests.Single() : null, body));
            return request.Method == HttpMethod.Post
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeviceRegistration("device-a", "library-a", "main", null, 1, false, "operation-a")) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new RemoteMutation("operation-b", "working", 1, new string('b', 64))) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://sync.example/") };
        var client = new RemoteSyncClient(http, "device-a", () => "token-a");
        await client.RegisterDeviceAsync("library-a", "main", default);
        var payload = "<xmp/>"u8.ToArray();
        var item = new RemoteItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "project/photo.xmp", new string('b', 64), payload.Length);
        await client.UploadWorkingAsync("library-a", "main", "upload-key", item, new string('a', 64), new MemoryStream(payload), default);
        TestAssert.That(requests[0].Route.EndsWith("/windows-sync/devices", StringComparison.Ordinal) && requests[0].Client == "device-a", "Device registration must use the scoped server contract.");
        TestAssert.That(requests[1].Route.Contains("/windows-sync/working/", StringComparison.Ordinal) && requests[1].Idempotency == "upload-key" && requests[1].Digest == item.Sha256 && requests[1].Body.SequenceEqual(payload), "Working upload must carry the exact XMP bytes, expected-base route, digest, and idempotency identity.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
