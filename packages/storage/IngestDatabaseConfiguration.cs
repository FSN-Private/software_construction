using Npgsql;

namespace Platform.Storage;

public static class IngestDatabaseConfiguration
{
    public static string? ResolveConnectionString(string? configured, Func<string, string?> setting)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var host = setting("Ingest:Database:Host");
        var database = setting("Ingest:Database:Name");
        var username = setting("Ingest:Database:Username");
        var password = setting("Ingest:Database:Password");
        if (new[] { host, database, username, password }.Any(string.IsNullOrWhiteSpace)) return null;

        var port = int.TryParse(setting("Ingest:Database:Port"), out var configuredPort) ? configuredPort : 5432;
        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
            Timeout = 5,
            CommandTimeout = 30,
        }.ConnectionString;
    }
}
