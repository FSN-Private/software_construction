using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Features.Recovery;
using Platform.Recovery;
using static IngestChecks;

internal static class RecoveryPurgeHttpChecks
{
    public static async Task<RecoveryPurgeProposalView> RunAsync(NpgsqlDataSource db,
        IReadOnlyDictionary<string, string?> configuration, TimeProvider clock, Guid assetId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<RecoveryPurgeService>();
        builder.Services.AddSingleton<RecoveryHealthService>();
        builder.Services.AddSingleton<RecoveryOwnerResolver>();
        builder.Services.AddAuthentication("recovery-purge-test")
            .AddScheme<AuthenticationSchemeOptions, RecoveryPurgeTestAuthenticationHandler>("recovery-purge-test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        RecoveryEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var anonymous = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized,
            "The purge proposal route requires authenticated owner authorization.");

        client.DefaultRequestHeaders.Add("X-Test-Owner", "purge-proposal-owner");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        var created = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(created.StatusCode == HttpStatusCode.Created,
            "A recently authenticated owner can submit a purge proposal over HTTP.");
        var proposal = await created.Content.ReadFromJsonAsync<RecoveryPurgeProposalView>()
            ?? throw new InvalidOperationException("Expected the durable purge proposal response.");

        var read = await client.GetAsync($"/v1/recovery/purge/proposals/{proposal.Proposal.OperationId:D}");
        Check(read.StatusCode == HttpStatusCode.OK,
            "The owner can read the canonical proposal status over HTTP.");
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "foreign-purge-owner");
        var foreign = await client.GetAsync($"/v1/recovery/purge/proposals/{proposal.Proposal.OperationId:D}");
        Check(foreign.StatusCode == HttpStatusCode.NotFound,
            "A foreign owner cannot discover another owner’s purge proposal.");

        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "purge-proposal-owner");
        client.DefaultRequestHeaders.Remove("X-Test-Auth-Time");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.AddMinutes(-6)
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var stale = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(stale.StatusCode == HttpStatusCode.Forbidden,
            "The purge proposal route rejects stale authentication before persisting another proposal.");

        client.DefaultRequestHeaders.Remove("X-Test-Auth-Time");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.AddMinutes(1)
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var future = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(future.StatusCode == HttpStatusCode.Forbidden,
            "The purge proposal route rejects future authentication timestamps.");

        await app.StopAsync();
        return proposal;
    }
}

internal sealed class RecoveryPurgeTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var owner = Request.Headers["X-Test-Owner"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(owner)) return Task.FromResult(AuthenticateResult.NoResult());
        var authTime = Request.Headers["X-Test-Auth-Time"].FirstOrDefault() ?? "0";
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, owner),
            new Claim("cognito:groups", "owner"),
            new Claim("auth_time", authTime)
        ], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
