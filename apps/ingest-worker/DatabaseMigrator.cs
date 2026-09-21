using System.Reflection;
using System.Security.Cryptography;
using Npgsql;

namespace Platform.Ingest.Worker;

public static class DatabaseMigrator
{
    private static readonly string[] Migrations =
    [
        "005_ingest_storage.sql",
        "005_ingest_workflows.sql",
        "008_rendering.sql",
    ];

    public static Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) => ApplyAsync(dataSource, null, null, cancellationToken);

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string? runtimeUsername, string? runtimePassword, CancellationToken cancellationToken)
    {
        if ((runtimeUsername is null) != (runtimePassword is null)) throw new InvalidOperationException("Runtime database credentials must be supplied together.");
        if (runtimeUsername is not null && !System.Text.RegularExpressions.Regex.IsMatch(runtimeUsername, "^[a-z][a-z0-9_]{0,62}$")) throw new InvalidOperationException("Runtime database username is invalid.");
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(5005); CREATE TABLE IF NOT EXISTS platform_schema_migrations (id text PRIMARY KEY, sha256 char(64) NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", cancellationToken);
            foreach (var file in Migrations)
            {
                var sql = Read(file);
                var checksum = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));
                await using var check = new NpgsqlCommand("SELECT sha256 FROM platform_schema_migrations WHERE id=$1", connection);
                check.Parameters.AddWithValue(file);
                var applied = (string?)await check.ExecuteScalarAsync(cancellationToken);
                if (applied is not null)
                {
                    if (!string.Equals(applied, checksum, StringComparison.Ordinal)) throw new InvalidOperationException($"Applied migration {file} does not match the release artifact.");
                    continue;
                }

                await ExecuteAsync(connection, sql, cancellationToken);
                await using var record = new NpgsqlCommand("INSERT INTO platform_schema_migrations(id,sha256) VALUES($1,$2)", connection);
                record.Parameters.AddWithValue(file);
                record.Parameters.AddWithValue(checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            if (runtimeUsername is not null) await ProvisionRuntimeRoleAsync(connection, runtimeUsername, runtimePassword!, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static string Read(string file)
    {
        var name = $"Platform.Ingest.Worker.Database.Migrations.{file}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded migration {file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ProvisionRuntimeRoleAsync(NpgsqlConnection connection, string username, string password, CancellationToken cancellationToken)
    {
        await using var schemaCommand = new NpgsqlCommand("SELECT current_schema()", connection);
        var schema = (string)(await schemaCommand.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("PostgreSQL did not resolve the migration schema."));
        await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)", connection);
        exists.Parameters.AddWithValue(username);
        var verb = (bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false) ? "ALTER" : "CREATE";
        await ExecuteFormattedAsync(connection, $"{verb} ROLE %I LOGIN PASSWORD %L", cancellationToken, username, password);
        await ExecuteFormattedAsync(connection, "GRANT CONNECT ON DATABASE %I TO %I", cancellationToken, connection.Database, username);
        await ExecuteFormattedAsync(connection, "GRANT USAGE ON SCHEMA %I TO %I", cancellationToken, schema, username);
        await ExecuteFormattedAsync(connection, "REVOKE CREATE ON SCHEMA %I FROM PUBLIC", cancellationToken, schema);
        await ExecuteFormattedAsync(connection, "REVOKE CREATE ON SCHEMA %I FROM %I", cancellationToken, schema, username);
        await ExecuteFormattedAsync(connection, "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I", cancellationToken, schema, username);
        await ExecuteFormattedAsync(connection, "REVOKE ALL PRIVILEGES ON TABLE %I.platform_schema_migrations FROM %I", cancellationToken, schema, username);
        await ExecuteFormattedAsync(connection, "GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I", cancellationToken, schema, username);
        await ExecuteFormattedAsync(connection, "ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I", cancellationToken, connection.UserName!, schema, username);
        await ExecuteFormattedAsync(connection, "ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I", cancellationToken, connection.UserName!, schema, username);
    }

    private static async Task ExecuteFormattedAsync(NpgsqlConnection connection, string format, CancellationToken cancellationToken, params string[] values)
    {
        var placeholders = string.Join(",", values.Select((_, index) => $"${index + 2}"));
        await using var formatter = new NpgsqlCommand($"SELECT format($1,{placeholders})", connection);
        formatter.Parameters.AddWithValue(format);
        foreach (var value in values) formatter.Parameters.AddWithValue(value);
        var sql = (string)(await formatter.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("PostgreSQL did not format the role statement."));
        await ExecuteAsync(connection, sql, cancellationToken);
    }
}
