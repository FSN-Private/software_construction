using Platform.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton<OperationIdFactory>();

var app = builder.Build();
app.UsePlatformTelemetry();

app.Run();

public partial class Program;
