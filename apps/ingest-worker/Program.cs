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

if (args is ["--migrate"])
{
    var migrationBuilder = Host.CreateApplicationBuilder(args);
    var migrationConnection = IngestDatabaseConfiguration.ResolveConnectionString(migrationBuilder.Configuration.GetConnectionString("Ingest"), key => migrationBuilder.Configuration[key]) ?? throw new InvalidOperationException("Ingest database configuration is required.");
    await using var migrationDataSource = NpgsqlDataSource.Create(migrationConnection);
    await DatabaseMigrator.ApplyAsync(migrationDataSource, CancellationToken.None);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var connectionString = IngestDatabaseConfiguration.ResolveConnectionString(builder.Configuration.GetConnectionString("Ingest"), key => builder.Configuration[key]) ?? throw new InvalidOperationException("Ingest database configuration is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
builder.Services.AddSingleton(sp => new S3ObjectStore(sp.GetRequiredService<IAmazonS3>(), builder.Configuration["Ingest:Bucket"] ?? throw new InvalidOperationException("Ingest:Bucket is required."), builder.Configuration["Ingest:KmsKeyId"] ?? throw new InvalidOperationException("Ingest:KmsKeyId is required.")));
builder.Services.AddSingleton<BatchProcessor>();
builder.Services.AddHostedService<PublicationRepairWorker>();
await builder.Build().RunAsync();
