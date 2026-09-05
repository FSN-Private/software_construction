using Platform.Api.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;

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

app.MapGet("/live", () => Results.Ok(new { status = "live", contractVersion = "v1" }));
app.MapGet("/ready", (HttpContext context) => RequireOwner(context, () => Results.Ok(new
{
    status = "ready",
    operationId = OperationId(context),
    releaseId = ReleaseId(),
})));
app.MapGet("/v1/session", (HttpContext context) => RequireOwner(context, () => Results.Ok(new
{
    subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"),
    role = "owner",
    operationId = OperationId(context),
})));
app.MapGet("/v1/version", (HttpContext context) => RequireOwner(context, () => Results.Ok(new
{
    releaseId = ReleaseId(),
    sourceRevision = Environment.GetEnvironmentVariable("PLATFORM_SOURCE_REVISION") ?? new string('0', 40),
    manifestDigest = Environment.GetEnvironmentVariable("PLATFORM_MANIFEST_DIGEST") ?? $"sha256:{new string('0', 64)}",
    operationId = OperationId(context),
})));

app.Run();

static IResult RequireOwner(HttpContext context, Func<IResult> success) =>
    context.User.Identity?.IsAuthenticated is true && context.User.Claims.Any(claim => claim.Type == "cognito:groups" && claim.Value == "owner")
        ? success()
        : Results.Problem(
            type: "https://example.invalid/problems/unauthenticated",
            title: "Authentication required",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: ProblemExtensions(context, "unauthenticated"));

static Dictionary<string, object?> ProblemExtensions(HttpContext context, string code) => new()
{
    ["code"] = code,
    ["operationId"] = OperationId(context),
};

static string OperationId(HttpContext context) => context.Items[PlatformTelemetry.OperationIdItemKey]?.ToString() ?? "op_unavailable";

static string ReleaseId() => Environment.GetEnvironmentVariable("PLATFORM_RELEASE_ID") ?? "v0.0.0-dev";

public partial class Program;
