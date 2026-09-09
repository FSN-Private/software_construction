using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Ingest.Worker;
using Platform.Storage;
using Amazon.S3;

if (args is ["--verify-manifest", var trusted, var recovered, var objects])
{
    Environment.ExitCode = await Platform.Ingest.Worker.Verification.ManifestCommand.RunAsync(trusted, recovered, objects, CancellationToken.None);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var connectionString = builder.Configuration.GetConnectionString("Ingest") ?? throw new InvalidOperationException("ConnectionStrings:Ingest is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
builder.Services.AddSingleton(sp => new S3ObjectStore(sp.GetRequiredService<IAmazonS3>(), builder.Configuration["Ingest:Bucket"] ?? throw new InvalidOperationException("Ingest:Bucket is required."), builder.Configuration["Ingest:KmsKeyId"] ?? throw new InvalidOperationException("Ingest:KmsKeyId is required.")));
builder.Services.AddSingleton<BatchProcessor>();
builder.Services.AddHostedService<PublicationRepairWorker>();
await builder.Build().RunAsync();
