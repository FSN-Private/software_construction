using Platform.Api.Telemetry;
using Platform.Api.Contracts;
using Platform.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Npgsql;
using Platform.Storage;
using Amazon.S3;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton<OperationIdFactory>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:CognitoAuthority"];
    options.Audience = builder.Configuration["Authentication:CognitoClientId"];
    options.RequireHttpsMetadata = true;
});
builder.Services.AddAuthorization();
var ingestConnection = builder.Configuration.GetConnectionString("Ingest");
if (!string.IsNullOrWhiteSpace(ingestConnection))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(ingestConnection));
    builder.Services.AddSingleton<TransferGrantIssuer>();
    builder.Services.AddSingleton<IdempotencyStore>();
}
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
builder.Services.AddSingleton(sp => new S3ObjectStore(sp.GetRequiredService<IAmazonS3>(), builder.Configuration["Ingest:Bucket"] ?? throw new InvalidOperationException("Ingest:Bucket is required."), builder.Configuration["Ingest:KmsKeyId"] ?? throw new InvalidOperationException("Ingest:KmsKeyId is required.")));

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var result = Results.Problem(
        type: "https://example.invalid/problems/internal-error",
        title: "Request could not be completed",
        statusCode: StatusCodes.Status500InternalServerError,
        extensions: ProblemExtensions(context, "internal-error"));
    await result.ExecuteAsync(context);
}));
app.UsePlatformTelemetry();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapGet("/live", () => Results.Ok(new { status = "live", contractVersion = "v1" }));
app.MapGet("/ready", (HttpContext context, ILogger<Program> logger) => RequireOwner(context, logger, () => string.IsNullOrWhiteSpace(ingestConnection)
    ? Results.Problem(type: "https://example.invalid/problems/ingest_unavailable", title: "Ingest is not configured", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: ProblemExtensions(context, "ingest_unavailable"))
    : Results.Ok(new
{
    status = "ready",
    operationId = OperationId(context),
    releaseId = ReleaseId(),
}))); 
app.MapGet("/v1/session", (HttpContext context, ILogger<Program> logger) => RequireOwner(context, logger, () => Results.Ok(new
{
    subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"),
    role = "owner",
    operationId = OperationId(context),
})));
app.MapGet("/v1/version", (HttpContext context, ILogger<Program> logger) => RequireOwner(context, logger, () => Results.Ok(new
{
    releaseId = ReleaseId(),
    sourceRevision = Environment.GetEnvironmentVariable("PLATFORM_SOURCE_REVISION") ?? new string('0', 40),
    manifestDigest = Environment.GetEnvironmentVariable("PLATFORM_MANIFEST_DIGEST") ?? $"sha256:{new string('0', 64)}",
    operationId = OperationId(context),
})));
app.MapPost("/v1/upload-sessions/{id:guid}/transfer", async (Guid id, HttpContext context, IServiceProvider services, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!OwnerAuthorization.TryGetOwner(context.User, out var ownerId)) return RequireOwner(context, logger, () => Results.Empty);
    if (!RequestContracts.TryGetLifecycleVersion(context, out var version)) return RequestContracts.Problem(context, StatusCodes.Status400BadRequest, "lifecycle_version_required", "Lifecycle version required");
    var issuer = services.GetService<TransferGrantIssuer>();
    if (issuer is null) return RequestContracts.Problem(context, StatusCodes.Status503ServiceUnavailable, "ingest_unavailable", "Ingest is not configured");
    S3ObjectStore storage;
    try { storage = services.GetRequiredService<S3ObjectStore>(); }
    catch (InvalidOperationException) { return RequestContracts.Problem(context, StatusCodes.Status503ServiceUnavailable, "ingest_unavailable", "Ingest is not configured"); }
    try
    {
        var grant = await issuer.IssueAsync(id, ownerId, version, DateTime.UtcNow, cancellationToken);
        var transfer = storage.CreateStagingUploadUrl(grant, DateTime.UtcNow);
        context.Items["platform.idempotency.reissue"] = true;
        return Results.Ok(new { sessionId = grant.SessionId, expiresAt = grant.ExpiresAtUtc, url = transfer.Url, headers = transfer.RequiredHeaders });
    }
    catch (InvalidOperationException) { return RequestContracts.Problem(context, StatusCodes.Status403Forbidden, "upload_session_not_authorized", "Upload session is not available"); }
});

app.Run();

static IResult RequireOwner(HttpContext context, ILogger logger, Func<IResult> success)
{
    if (OwnerAuthorization.TryGetOwner(context.User, out _)) return success();
    logger.LogWarning("Authorization denied {AuditEventType} {OperationId} {Actor} {Outcome} {ReasonCode}", "authorization.denied", OperationId(context), "anonymous", "denied", "unauthenticated");
    return Results.Problem(
            type: "https://example.invalid/problems/unauthenticated",
            title: "Authentication required",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: ProblemExtensions(context, "unauthenticated"));
}

static Dictionary<string, object?> ProblemExtensions(HttpContext context, string code) => new()
{
    ["code"] = code,
    ["operationId"] = OperationId(context),
};

static string OperationId(HttpContext context) => context.Items[PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable";

static string ReleaseId() => Environment.GetEnvironmentVariable("PLATFORM_RELEASE_ID") ?? "v0.0.0-dev";

public partial class Program;
