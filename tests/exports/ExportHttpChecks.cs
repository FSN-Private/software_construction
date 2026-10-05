using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Platform.Api.Contracts;
using Platform.Api.Features.Exports;
using Platform.Api.Telemetry;
using Platform.Contracts;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using static Platform.Storage.IngestDb;

internal static class ExportHttpChecks
{
    public static async Task RunAsync(NpgsqlDataSource db)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Configuration["Exports:Enabled"] = "true";
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(new string('k', 64)));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new()
        { ValidIssuer = "export-fixture", ValidAudience = "export-fixture", IssuerSigningKey = key, ValidateIssuerSigningKey = true });
        builder.Services.AddAuthorization();
        builder.Services.AddCors(options => options.AddPolicy("exports-download", policy => ExportConfiguration.DownloadCors(policy, "https://web.example.test")));
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ExportAuthorizationResultHandler>();
        builder.Services.AddSingleton<IAuditWriter>(new PostgresAuditWriter(db, new ConfigurationBuilder().Build()));
        builder.Services.AddSingleton<OperationIdFactory>();
        await using var app = builder.Build();
        app.UsePlatformTelemetry(); app.UseAuthentication(); app.UseCors(); app.UseAuthorization();
        app.MapMethods("/download", ["GET", "HEAD", "POST"], () => Results.Ok()).RequireCors("exports-download");
        // The production route's auth/filter metadata executes through real JWT middleware. No media service is invoked on denial.
        foreach (var path in new[] { "/baskets", "/requests/{id:guid}/manifest", "/requests/{id:guid}/cancel" })
            app.MapMethods("/v1/libraries/{library}/exports" + path, ["GET", "POST"], () => Results.Ok()).WithMetadata(new ExportProtected()).RequireAuthorization().AddEndpointFilter<ExportGuard>();
        app.MapGet("/v1/exports/operations", () => Results.Ok()).WithMetadata(new ExportProtected()).RequireAuthorization();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var origin in new[] { "https://web.example.test", "https://foreign.example.test" })
            {
                using var preflight = new HttpRequestMessage(HttpMethod.Options, "/download");
                preflight.Headers.Add("Origin", origin); preflight.Headers.Add("Access-Control-Request-Method", "GET"); preflight.Headers.Add("Access-Control-Request-Headers", "authorization,range,if-range");
                using var response = await client.SendAsync(preflight);
                var allowed = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
                if (origin == "https://web.example.test" ? allowed != origin || !response.Headers.Contains("Access-Control-Allow-Credentials") : allowed is not null) throw new InvalidOperationException("Dedicated download CORS must allow only the exact owner web origin");
            }
            var operation = "op_" + Guid.NewGuid().ToString("N"); client.DefaultRequestHeaders.Add("X-Operation-Id", operation);
            foreach (var path in new[] { "/baskets", $"/requests/{Guid.NewGuid()}/manifest", $"/requests/{Guid.NewGuid()}/cancel" })
            {
                using var response = await client.GetAsync("/v1/libraries/library/exports" + path);
                if (response.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Anonymous export HTTP denial");
            }
            using var anonymousOperator = await client.GetAsync("/v1/exports/operations");
            if (anonymousOperator.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Anonymous operator HTTP denial");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
            using var invalid = await client.GetAsync("/v1/libraries/library/exports/baskets");
            if (invalid.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Invalid JWT export denial");
            var jwt = new JwtSecurityToken("export-fixture", "export-fixture", [new Claim("sub", "operator-fixture"), new Claim("cognito:groups", "operator")], expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            using var nonOwner = await client.GetAsync("/v1/libraries/library/exports/baskets");
            if (nonOwner.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Non-owner role denied by export guard");
            await using var connection = await db.OpenConnectionAsync();
            if (Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM audit_events WHERE operation_id=$1 AND event_type='exports.access_denied'", default, operation)) != 6 ||
                await ScalarAsync(connection, "SELECT 1 FROM audit_events WHERE operation_id=$1 AND owner_id='operator-fixture' AND event_type='exports.access_denied'", default, operation) is null)
                throw new InvalidOperationException("All JWT/role export denials must have attributable durable audits");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("Export HTTP JWT challenge and non-owner audit checks passed.");
    }
}
