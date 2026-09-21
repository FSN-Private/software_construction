using Platform.Api.Telemetry;
using Platform.Api.Contracts;
using Platform.Contracts;
using Platform.Ingest.Worker;
using Platform.Storage;
using Platform.Storage.Paths;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using Npgsql;

var factory = new OperationIdFactory();
var generated = factory.Resolve(null);
Assert(generated.StartsWith("op_", StringComparison.Ordinal), "Generated operation IDs must use the v1 prefix.");
Assert(generated.Length == 35, "Generated operation IDs must be stable-length opaque values.");

const string supplied = "op_valid-operation-id_01";
Assert(factory.Resolve(supplied) == supplied, "Valid caller-supplied IDs must be retained.");
Assert(factory.Resolve("invalid") != "invalid", "Invalid caller-supplied IDs must be replaced.");

var failedRequestMeasurements = 0;
using (var listener = new MeterListener())
{
    listener.InstrumentPublished = (instrument, meterListener) =>
    {
        if (instrument.Meter.Name == "Platform.Api" && instrument.Name == "platform.api.requests")
        {
            meterListener.EnableMeasurementEvents(instrument);
        }
    };
    listener.SetMeasurementEventCallback<long>((_, _, _, _) => failedRequestMeasurements++);
    listener.Start();

    var middleware = new PlatformTelemetryMiddleware(
        _ => throw new InvalidOperationException("Synthetic downstream failure."),
        NullLogger<PlatformTelemetryMiddleware>.Instance,
        factory);
    var context = new DefaultHttpContext();
    await AssertThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context), "Middleware must preserve downstream failures.");
}
Assert(failedRequestMeasurements == 1, "A failed request must still produce one normalized request metric.");

var owner = new ClaimsPrincipal(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, "owner-1"), new Claim("cognito:groups", "owner")], "test"));
Assert(OwnerAuthorization.TryGetOwner(owner, out var ownerId) && ownerId == "owner-1", "Owner authorization must require an authenticated owner subject.");
Assert(!OwnerAuthorization.TryGetOwner(new ClaimsPrincipal(new ClaimsIdentity()), out _), "Anonymous callers must not be owners.");
Assert(Audit.Redact("token=private&name=ok") == "[redacted]&name=ok", "Audit redaction must remove credentials.");
var audit = AuditEvent.Create("owner-1", "op_test", "asset.moved", "accepted");
Assert(audit.Id != Guid.Empty && audit.ReasonCode is null, "Audit records must use opaque identifiers and exclude payloads.");

var missingKeyContext = new DefaultHttpContext();
missingKeyContext.Request.Method = HttpMethods.Post;
missingKeyContext.Response.Body = new MemoryStream();
missingKeyContext.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
var invoked = false;
await new IdempotencyMiddleware(_ => { invoked = true; return Task.CompletedTask; }).InvokeAsync(missingKeyContext);
Assert(!invoked && missingKeyContext.Response.StatusCode == StatusCodes.Status400BadRequest, "Mutations without an idempotency key must be rejected.");

await using var validStream = new MemoryStream(Encoding.UTF8.GetBytes("immutable bytes"));
var address = await ContentAddress.FromAsync(validStream);
Assert(address.IsValid && address.Length == 15 && address.AuthoritativeKey == $"sha256/{address.Sha256}", "Content addresses must be immutable SHA-256 plus length identities.");
await using var mismatchStream = new MemoryStream(Encoding.UTF8.GetBytes("different bytes"));
await AssertThrowsAsync<InvalidDataException>(() => ContentAddress.VerifyAsync(mismatchStream, address), "Checksum mismatch must prevent publication.");
Assert(LibraryPath.Normalize("folder\\photo.dng") == "folder/photo.dng", "Display paths must normalize separators.");
Assert(LibraryPath.CollisionKey("Folder/photo.dng") == LibraryPath.CollisionKey("folder/PHOTO.dng"), "Collision keys must be case insensitive.");
AssertThrows(() => LibraryPath.Normalize("folder/aux"), "Windows reserved device names must be rejected.");
AssertThrows(() => LibraryPath.Normalize("folder/CON.photo.jpg"), "Windows device names must be rejected before every extension.");
AssertThrows(() => LibraryPath.Normalize("folder/a\u0001.jpg"), "Windows control characters must be rejected on every host.");

var databaseSettings = new Dictionary<string, string?>
{
    ["Ingest:Database:Host"] = "database.internal",
    ["Ingest:Database:Port"] = "5433",
    ["Ingest:Database:Name"] = "platform",
    ["Ingest:Database:Username"] = "platform_owner",
    ["Ingest:Database:Password"] = "test-only-password",
};
var generatedConnection = new NpgsqlConnectionStringBuilder(IngestDatabaseConfiguration.ResolveConnectionString(null, key => databaseSettings.GetValueOrDefault(key)));
Assert(generatedConnection.Host == "database.internal" && generatedConnection.Port == 5433 && generatedConnection.Database == "platform" && generatedConnection.Username == "platform_owner" && generatedConnection.Password == "test-only-password" && generatedConnection.SslMode == SslMode.Require, "ECS database fields must produce an encrypted PostgreSQL connection string in memory.");
Assert(IngestDatabaseConfiguration.ResolveConnectionString("Host=explicit", _ => null) == "Host=explicit", "An explicit local connection string must take precedence.");
Assert(IngestDatabaseConfiguration.ResolveConnectionString(null, _ => null) is null, "Incomplete database settings must keep ingest disabled.");
RenderingChecks.Run();

try { await IngestChecks.RunAsync(); }
catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
Console.WriteLine("Platform API telemetry foundation tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertThrows(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException(message);
}
