using Platform.Api.Telemetry;
using Platform.Api.Contracts;
using Platform.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Npgsql;
using Platform.Storage;
using Amazon.S3;
using Amazon.SQS;
using Platform.Api.Features.Ingest;
using Platform.Api.Features.VersionControl;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Working;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Operations;
using Platform.Api.Features.VersionControl.Rewrite;
using Platform.Api.Features.VersionControl.Visibility;
using Platform.Api.Features.VersionControl.MergePublishing;
using Platform.Api.Features.VersionControl.Projection;
using Platform.Api.Features.WindowsSync;
using Platform.Api.Features.Gallery.Metadata;
using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.Gallery;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
// Request URLs, model-binding values, and exception detail may contain private paths.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
builder.Services.AddSingleton<OperationIdFactory>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:CognitoAuthority"];
    options.Audience = builder.Configuration["Authentication:CognitoClientId"];
    options.RequireHttpsMetadata = true;
});
builder.Services.AddAuthorization();
builder.Services.AddDataProtection();
var ingestConnection = IngestDatabaseConfiguration.ResolveConnectionString(builder.Configuration.GetConnectionString("Ingest"), key => builder.Configuration[key]);
if (!string.IsNullOrWhiteSpace(ingestConnection) && !string.IsNullOrWhiteSpace(builder.Configuration["Ingest:Bucket"]) && !string.IsNullOrWhiteSpace(builder.Configuration["Ingest:KmsKeyId"]))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(ingestConnection));
    builder.Services.AddSingleton<TransferGrantIssuer>();
    builder.Services.AddSingleton<ImportService>();
    builder.Services.AddSingleton<AssetService>();
    builder.Services.AddSingleton<Platform.Storage.Reconstruction.ManifestService>();
    builder.Services.AddSingleton<IdempotencyStore>();
    builder.Services.AddSingleton<IAuditWriter, PostgresAuditWriter>();
    builder.Services.AddSingleton(sp => new IntegrityService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<S3ObjectStore>()));
    builder.Services.AddHostedService<IntegrityVerificationWorker>();
    builder.Services.AddSingleton(sp => new WorkingService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<S3ObjectStore>()));
    builder.Services.AddSingleton(sp => new CommitService(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<S3ObjectStore>()));
    builder.Services.AddSingleton<RefService>();
    builder.Services.AddSingleton<QueryService>();
    builder.Services.AddSingleton<Platform.Contracts.Rendering.ISelectedVersionResolver, Platform.Api.Features.Rendering.VersionControlSelectedVersionResolver>();
    builder.Services.AddSingleton<Platform.Api.Features.Rendering.RenderingService>();
    builder.Services.AddSingleton<Platform.Api.Features.Rendering.RenderOperationsService>();
    builder.Services.AddHostedService<Platform.Api.Features.Rendering.RenderRecoveryWorker>();
    builder.Services.AddSingleton<Platform.Contracts.Rendering.IRenderWorkerCallback, Platform.Api.Features.Rendering.RenderWorkerCallbacks>();
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Rendering:DerivativeBucket"])) builder.Services.AddSingleton<Platform.Api.Features.Rendering.IPrivateDerivativeLocator, Platform.Api.Features.Rendering.S3PrivateDerivativeLocator>();
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Rendering:QueueUrl"])) builder.Services.AddSingleton<Platform.Contracts.Rendering.IRenderQueueDispatcher, Platform.Api.Features.Rendering.SqsRenderQueueDispatcher>();
    builder.Services.AddSingleton<OperationService>();
    builder.Services.AddSingleton<RewriteService>();
    builder.Services.AddSingleton<VisibilityService>();
    builder.Services.AddSingleton<MergeService>();
    builder.Services.AddSingleton<ProjectionService>();
    builder.Services.AddSingleton<WindowsSyncService>();
    builder.Services.AddSingleton<GalleryRefreshService>();
    builder.Services.AddHostedService<GalleryRefreshWorker>();
    builder.Services.AddSingleton<GalleryBrowseService>();
    builder.Services.AddSingleton<GalleryDetailService>();
    builder.Services.AddSingleton<GalleryPreferenceService>();
    builder.Services.AddSingleton<GalleryEditService>();
}
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
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
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value?.Contains("/gallery", StringComparison.Ordinal) == true)
        context.Response.Headers.CacheControl = "private, no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();
IngestEndpoints.Map(app);
Platform.Api.Features.Lifecycle.AssetEndpoints.Map(app);
Platform.Api.Features.Reconstruction.ManifestEndpoints.Map(app);
VersionControlEndpoints.Map(app);
WindowsSyncEndpoints.Map(app);
Platform.Api.Features.Rendering.RenderingEndpoints.Map(app);
GalleryEndpoints.Map(app);

app.MapGet("/live", () => Results.Ok(new { status = "live", contractVersion = "v1" }));
app.MapGet("/ready", async (HttpContext context, ILogger<Program> logger, IServiceProvider services, CancellationToken cancellationToken) =>
{
    if (!OwnerAuthorization.TryGetOwner(context.User, out _)) return RequireOwner(context, logger, () => Results.Empty);
    if (string.IsNullOrWhiteSpace(ingestConnection) || string.IsNullOrWhiteSpace(builder.Configuration["Ingest:Bucket"]) || string.IsNullOrWhiteSpace(builder.Configuration["Ingest:KmsKeyId"]))
        return Results.Problem(type: "https://example.invalid/problems/ingest_unavailable", title: "Ingest is not configured", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: ProblemExtensions(context, "ingest_unavailable"));
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(timeout.Token);
        await services.GetRequiredService<S3ObjectStore>().CheckReadyAsync(timeout.Token);
        return Results.Ok(new
        {
            status = "ready",
            operationId = OperationId(context),
            releaseId = ReleaseId(),
        });
    }
    catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException or AmazonS3Exception or InvalidOperationException)
    {
        logger.LogWarning("Readiness dependency check failed {OperationId}", OperationId(context));
        return Results.Problem(type: "https://example.invalid/problems/ingest_unavailable", title: "Ingest is unavailable", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: ProblemExtensions(context, "ingest_unavailable"));
    }
});
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
