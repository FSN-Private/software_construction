using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Api.Contracts;
using Platform.Api.Features.Ingest;
using Platform.Api.Telemetry;
using Platform.Contracts;
using Platform.Storage;
using static IngestChecks;

internal static class HttpIngestChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(db); builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton<ImportService>();
        builder.Services.AddSingleton<TransferGrantIssuer>(); builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton<IAuditWriter, PostgresAuditWriter>(); builder.Services.AddSingleton<OperationIdFactory>();
        await using var app = builder.Build();
        app.UsePlatformTelemetry();
        // This private in-process harness supplies claims. The deployed API only accepts Cognito JWTs.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Owner"].FirstOrDefault() is { } owner)
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner), new Claim("cognito:groups", "owner")], "test"));
            await next(context);
        });
        app.UseMiddleware<IdempotencyMiddleware>();
        IngestEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var anonymous = await client.GetAsync($"/v1/import-batches/{Guid.NewGuid()}");
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "HTTP owner access required");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "http-owner");
        var bytes = File.ReadAllBytes("tests/fixtures/ingest/sample.png");
        var body = new ImportRequest(false, [new("http.png", Hash(bytes), bytes.Length)]);
        var missing = await client.PostAsJsonAsync("/v1/import-batches", body);
        Check(missing.StatusCode == HttpStatusCode.BadRequest, "HTTP idempotency required");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "http-import-00001");
        var created = await client.PostAsJsonAsync("/v1/import-batches", body);
        Check(created.StatusCode == HttpStatusCode.Created, "HTTP import creates descriptors");
        var batch = (await created.Content.ReadFromJsonAsync<BatchStatus>())!;
        var replay = await client.PostAsJsonAsync("/v1/import-batches", body);
        Check((await replay.Content.ReadFromJsonAsync<BatchStatus>())!.Id == batch.Id, "HTTP replay returns original durable batch");
        var conflict = await client.PostAsJsonAsync("/v1/import-batches", new ImportRequest(false, [new("other.png", Hash(bytes), bytes.Length)]));
        Check(conflict.StatusCode == HttpStatusCode.Conflict, "Fingerprint prevents key reuse");
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", "http-transfer-001");
        var noVersion = await client.PostAsync($"/v1/upload-sessions/{batch.Items[0].Id}/transfer", null);
        Check(noVersion.StatusCode == HttpStatusCode.BadRequest, "HTTP lifecycle version required");
        client.DefaultRequestHeaders.Remove("Idempotency-Key"); client.DefaultRequestHeaders.Add("Idempotency-Key", "http-transfer-002");
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", "1");
        var transfer = await client.PostAsync($"/v1/upload-sessions/{batch.Items[0].Id}/transfer", null);
        var descriptor = await transfer.Content.ReadAsStringAsync();
        Check(transfer.IsSuccessStatusCode && !descriptor.Contains("staging/") && !descriptor.Contains("X-Amz"), "Transfer descriptor does not expose object keys or credentials");
        client.DefaultRequestHeaders.Remove("X-Test-Owner"); client.DefaultRequestHeaders.Add("X-Test-Owner", "different-owner");
        var forbidden = await client.GetAsync($"/v1/import-batches/{batch.Id}");
        Check(forbidden.StatusCode == HttpStatusCode.NotFound, "Other owner cannot inspect progress");
        await app.StopAsync();
        Console.WriteLine("HTTP checks passed: authentication, lifecycle versions, durable idempotency/fingerprints, private transfer descriptors, owner isolation.");
    }
}
