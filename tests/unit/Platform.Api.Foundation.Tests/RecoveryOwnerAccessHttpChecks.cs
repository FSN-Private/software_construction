using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Platform.Api.Features.Exports;
using Platform.Api.Features.Recovery;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryOwnerAccessHttpChecks
{
    internal static async Task RunAsync(NpgsqlDataSource db, IConfiguration configuration, TimeProvider clock,
        RecoveryIdentityApprovalV1 mapping, byte[] mappingSignature, RSA ownerKey, RecoverySigningTrust trust)
    {
        using var jwtKey = RSA.Create(2048);
        using var workerKey = RSA.Create(2048);
        var spki = workerKey.ExportSubjectPublicKeyInfo();
        var workerTrust = new RecoverySigningTrust("http-probe-worker-key", Convert.ToBase64String(spki), RecoveryEvidenceCodec.Hash(spki));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration.AsEnumerable());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recovery:Enabled"] = "true", ["Recovery:LibraryId"] = "main",
            ["Recovery:ObservationTrust:KeyId"] = workerTrust.KeyId,
            ["Recovery:ObservationTrust:PublicKeySpkiBase64"] = workerTrust.PublicKeySpkiBase64,
            ["Recovery:ObservationTrust:PublicKeySha256"] = workerTrust.PublicKeySha256
        });
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<RecoveryOwnerResolver>();
        builder.Services.AddSingleton<RecoveryHealthService>();
        builder.Services.AddTransient<IClaimsTransformation, RecoveryOwnerClaimsTransformation>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ExportAuthorizationResultHandler>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = mapping.Issuer,
                ValidateAudience = true, ValidAudience = mapping.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new RsaSecurityKey(jwtKey),
                ValidateLifetime = true, ClockSkew = TimeSpan.Zero
            };
        });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        RecoveryEndpoints.Map(app);
        await app.StartAsync();
        var endpoint = new Uri(app.Urls.Single());
        string Token(string subject, RSA key)
        {
            var now = DateTimeOffset.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(mapping.Issuer, mapping.Audience,
                [new Claim("sub", subject), new Claim("cognito:groups", "owner"),
                 new Claim("token_use", "id"), new Claim("auth_time", now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                 new Claim("platform:recovery-identity-bound", "true")],
                now.AddSeconds(-1).UtcDateTime, now.AddMinutes(10).UtcDateTime,
                new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
        }
        var ownerToken = Token(mapping.Subject, jwtKey);
        var foreignToken = Token("valid-foreign-subject", jwtKey);
        using var client = new HttpClient { BaseAddress = endpoint };
        async Task<HttpStatusCode> Status(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/recovery/health");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        if (await Status(ownerToken) != HttpStatusCode.OK || await Status(foreignToken) != HttpStatusCode.Forbidden ||
            await Status(Token("valid-foreign-subject", workerKey)) != HttpStatusCode.Unauthorized ||
            await Status(null) != HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Recovery JWT/claims/authorization pipeline failed owner=200 foreign=403 invalid=401");
        // Transport only is redirected to local Kestrel. JWT, transformation,
        // authorization, endpoint and response bytes are the actual API pipeline.
        using var probeClient = new HttpClient(new LocalTransport(endpoint));
        var transcript = await RecoveryOwnerAccessProbe.RunAsync(probeClient,
            new RecoveryApiTask("fixture-task", new Uri("http://10.0.0.1:8080/")), Guid.NewGuid(),
            mapping, ownerToken, foreignToken, default);
        var mappingBytes = RecoveryEvidenceCodec.SerializeCanonical(new RecoveryIdentityApprovalArtifactV1(
            mapping, Convert.ToBase64String(mappingSignature)));
        var hash = new string('a', 64);
        var expected = new RecoveryOwnerAccessExpectationV1(mapping.PreservedOwnerId, "main", hash, hash,
            "http-fixture-revision", hash, hash, hash, "fixture-pool", mapping.Audience, mapping.Issuer);
        var payload = RecoveryEvidenceCodec.SerializeCanonical(new RecoveryCandidateAccessProbeV1(1,
            Guid.NewGuid(), Guid.NewGuid(), "independent_attestation_pending", RecoveryEvidenceCodec.Hash(mappingBytes), transcript, expected));
        var probeBytes = RecoveryEvidenceCodec.SerializeCanonical(new PrivateSignedCheckpointV1(1,
            "recovery_candidate_access_probe", "http-fixture", workerTrust.KeyId, workerTrust.PublicKeySha256,
            RecoveryEvidenceCodec.Hash(payload), Convert.ToBase64String(payload), Convert.ToBase64String(workerKey.SignHash(
                SHA256.HashData(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
        var prepared = RecoveryOwnerAccessCodec.SerializePayload(OfflineOwnerAccessAttestation.Prepare(probeBytes,
            workerTrust, mappingBytes, trust, expected, DateTimeOffset.UtcNow));
        var signature = ownerKey.SignHash(SHA256.HashData(prepared), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        _ = RecoveryOwnerAccessCodec.Verify(RecoveryOwnerAccessCodec.ParseEnvelope(OfflineOwnerAccessAttestation.Assemble(
            prepared, signature, trust, expected, DateTimeOffset.UtcNow)), trust, expected, DateTimeOffset.UtcNow);
        await app.StopAsync();
    }

    private sealed class LocalTransport(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler
        { AllowAutoRedirect = false, UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath != "/v1/recovery/health")
                throw new InvalidOperationException("Unexpected private probe route");
            request.RequestUri = new Uri(endpoint, request.RequestUri.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }
}
