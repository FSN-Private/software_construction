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
    ];

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
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
}
