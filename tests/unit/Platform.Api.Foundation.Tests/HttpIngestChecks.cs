using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Api.Contracts;
using Platform.Api.Features.Ingest;
using Platform.Api.Features.Lifecycle;
using Platform.Api.Features.Reconstruction;
using Platform.Api.Features.VersionControl;
using Platform.Api.Features.VersionControl.Working;
using Platform.Ingest.Worker;
using Platform.Api.Telemetry;
using Platform.Contracts;
using Platform.Storage;
using Platform.Storage.Reconstruction;
using static IngestChecks;

internal static class HttpIngestChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(db); builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton<ImportService>(); builder.Services.AddSingleton<AssetService>(); builder.Services.AddSingleton<ManifestService>();
        builder.Services.AddSingleton<TransferGrantIssuer>(); builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton<IAuditWriter, PostgresAuditWriter>(); builder.Services.AddSingleton<OperationIdFactory>();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<WorkingService>();
        await using var app = builder.Build();
        app.UsePlatformTelemetry();
        // This private in-process harness supplies claims. The deployed API only accepts Cognito JWTs.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization == "Bearer rendering-fixture-test-token")
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "rendering-fixture-owner"), new Claim("cognito:groups", "owner")], "test"));
            if (context.Request.Headers["X-Test-Owner"].FirstOrDefault() is { } owner)
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner), new Claim("cognito:groups", "owner")], "test"));
            await next(context);
        });
        app.UseMiddleware<IdempotencyMiddleware>();
        app.UseAuthorization();
        IngestEndpoints.Map(app); AssetEndpoints.Map(app); ManifestEndpoints.Map(app);
        VersionControlEndpoints.Map(app);
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
        await RenderingFixtureBootstrapAsync(db, storage, app.Urls.Single());
        await app.StopAsync();
        Console.WriteLine("HTTP checks passed: authentication, lifecycle versions, durable idempotency/fingerprints, private transfer descriptors, owner isolation.");
    }

    private static async Task RenderingFixtureBootstrapAsync(NpgsqlDataSource db, S3ObjectStore storage, string baseUrl)
    {
        var output = Path.GetTempFileName();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = new Process { StartInfo = new ProcessStartInfo("node")
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true } };
        process.StartInfo.ArgumentList.Add("scripts/bootstrap-rendering-fixtures.mjs");
        process.StartInfo.ArgumentList.Add(baseUrl);
        process.StartInfo.ArgumentList.Add(output);
        process.StartInfo.Environment["DEPLOYMENT_ENVIRONMENT"] = "nonprod";
        process.StartInfo.Environment["INGEST_SMOKE_TOKEN"] = "rendering-fixture-test-token";
        process.Start();
        var errors = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            var worker = new BatchProcessor(db, storage);
            while (!process.HasExited)
            {
                await worker.ProcessNextAsync(deadline.Token);
                await Task.Delay(50, deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token);
            Check(process.ExitCode == 0, "Actual owner HTTP/API/worker fixture bootstrap failed: " + await errors);
            _ = await stdout;
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(output, deadline.Token));
            var fixtures = record.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
            Check(fixtures.Length == 5 && fixtures.Select(item => item.GetProperty("editId").GetGuid()).Distinct().Count() == 5,
                "Real ingest publication creates five distinct owner-scoped rendering edits.");
            await using var query = db.CreateCommand("""
                SELECT count(*) FROM vc_working_changes w JOIN assets a ON a.id=w.asset_id
                  JOIN edit_identities e ON e.id=w.edit_id AND e.asset_id=a.id
                  JOIN content_objects o ON o.sha256=a.original_sha256
                WHERE w.owner_id='rendering-fixture-owner' AND a.owner_id=w.owner_id AND w.library_id='main'
                  AND w.current_content_sha256=e.xmp_sha256 AND o.category='dng' AND o.integrity_state='authoritative'
                """);
            Check(await query.ExecuteScalarAsync(deadline.Token) is 5L,
                "Exact bootstrap DNG/XMP bytes pass real validation, multipart publication, association and working APIs.");
            Console.WriteLine("Rendering fixture bootstrap passed actual owner HTTP, multipart DNG/XMP validation/publication and version-control association.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            File.Delete(output);
        }
    }
}
