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

        var requests = new List<(HttpMethod Method, string Route, string? Client, string? Idempotency, string? Version, string? Digest, byte[] Body)>();
        var handler = new RecordingHandler(async request =>
        {
            await Task.Yield();
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync();
            var route = request.RequestUri!.PathAndQuery;
            requests.Add((request.Method, route, request.Headers.GetValues("X-Client-Id").Single(), request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null, request.Headers.TryGetValues("If-Match", out var versions) ? versions.Single() : null, request.Headers.TryGetValues("X-Content-SHA256", out var digests) ? digests.Single() : null, body));
            if (route.EndsWith("/windows-sync/devices", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeviceRegistration("device-a", "library-a", "main", null, 1, 0, false, "operation-a")) };
            if (route.EndsWith("/version-control/checkout", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { operationId = "operation-c", refName = "other", version = 3L, stashId = (Guid?)null }) };
            if (request.Method == HttpMethod.Delete)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = Guid.NewGuid(), state = "recoverable-deleted", lifecycleVersion = 5L, deletedUntil = DateTime.UtcNow.AddDays(90), path = (string?)null }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new RemoteMutation("operation-b", "working", 1, new string('b', 64))) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://sync.example/") };
        var client = new RemoteSyncClient(http, "device-a", () => "token-a");
        await client.RegisterDeviceAsync("library-a", "main", default);
        var payload = "<xmp/>"u8.ToArray();
        var item = new RemoteItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "project/photo.xmp", new string('b', 64), payload.Length);
        await client.UploadWorkingAsync("library-a", "main", "upload-key", item, new string('a', 64), new MemoryStream(payload), default);
        var lifecycle = await client.ApplyLifecycleAsync("delete", item.AssetId, item.EditId, "delete-key", 4, null, default);
        var checkout = await client.CheckoutAsync("library-a", "other", "block", false, 2, "checkout-key", default);
        TestAssert.That(requests[0].Route.EndsWith("/windows-sync/devices", StringComparison.Ordinal) && requests[0].Client == "device-a", "Device registration must use the scoped server contract.");
        TestAssert.That(requests[1].Route.Contains("/windows-sync/working/", StringComparison.Ordinal) && requests[1].Idempotency == "upload-key" && requests[1].Digest == item.Sha256 && requests[1].Body.SequenceEqual(payload), "Working upload must carry the exact XMP bytes, expected-base route, digest, and idempotency identity.");
        TestAssert.That(requests[2] is { Method: var method, Idempotency: "delete-key", Version: "\"4\"" } && method == HttpMethod.Delete && requests[2].Route.Contains($"/edits/{item.EditId}", StringComparison.Ordinal) && lifecycle.Version == 5, "Edit lifecycle calls must use the edit route, lifecycle version, and server-returned next version.");
        TestAssert.That(requests[3].Route.EndsWith("/version-control/checkout", StringComparison.Ordinal) && requests[3].Version == "\"2\"" && checkout is { Branch: "other", Version: 3 }, "Checkout must use the version-control response ref name and authoritative checkout version.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
