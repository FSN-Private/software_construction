using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Ingest.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Ingest") ?? throw new InvalidOperationException("ConnectionStrings:Ingest is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PublicationOutbox>();
builder.Services.AddHostedService<PublicationRepairWorker>();
await builder.Build().RunAsync();
