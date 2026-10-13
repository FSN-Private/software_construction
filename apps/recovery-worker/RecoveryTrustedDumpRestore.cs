using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Platform.Recovery.Worker;

/// <summary>Producer archive SQL is never sent to PostgreSQL. Only pinned schema
/// and COPY data for its exact table/column inventory can enter an isolated clone.</summary>
internal static class RecoveryTrustedDumpRestore
{
    internal static async Task<string> RestoreAsync(RecoveryDrillRunner.RecoveryDatabaseConnection database,
        string databaseName, string dumpPath, string migrationDirectory, string migrationDigest,
        string pgDump, string pgRestore, CancellationToken ct)
    {
        if (!Regex.IsMatch(databaseName, "^recovery_(drill|verify)_[a-z0-9_]{1,46}$"))
            throw new RecoveryWorkerException("trusted_restore_target_invalid");
        var files = Directory.GetFiles(migrationDirectory, "*.sql").Order(StringComparer.Ordinal).ToArray();
        var scripts = new List<byte[]>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            total += bytes.Length;
            if (total > 16 * 1024 * 1024) throw new RecoveryWorkerException("trusted_schema_size_limit");
            digest.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0")); digest.AppendData(bytes);
            scripts.Add(bytes);
        }
        if (files.Length == 0 || Convert.ToHexStringLower(digest.GetHashAndReset()) != migrationDigest)
            throw new RecoveryWorkerException("trusted_schema_release_digest_mismatch");
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var schema = await connection.BeginTransactionAsync(ct))
        {
            await Execute(connection, schema, "CREATE TABLE platform_schema_migrations (id text PRIMARY KEY, sha256 char(64) NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
            foreach (var script in scripts)
                await Execute(connection, schema, new UTF8Encoding(false, true).GetString(script).TrimStart('\uFEFF'), ct);
            await schema.CommitAsync(ct);
        }
        var expected = await ReadToolAsync(pgDump, ["--schema-only", "--no-owner", "--no-acl", "--no-comments", "--dbname", databaseName], database, ct);
        var captured = await ReadToolAsync(pgRestore, ["--schema-only", "--no-owner", "--no-acl", "--no-comments", "--file=-", dumpPath], database, ct);
        if (CanonicalSchema(expected) != CanonicalSchema(captured))
            throw new RecoveryWorkerException("recovery_dump_schema_untrusted");

        await using var transaction = await connection.BeginTransactionAsync(ct);
        var schemaInventorySha256 = await RecoverySnapshotPreservation.ReadInventorySha256Async(connection, transaction, ct);
        var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using (var columns = new NpgsqlCommand("SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name,ordinal_position", connection, transaction))
        await using (var rows = await columns.ExecuteReaderAsync(ct))
        {
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (await rows.ReadAsync(ct))
            {
                if (!grouped.TryGetValue(rows.GetString(0), out var names)) grouped.Add(rows.GetString(0), names = []);
                names.Add(rows.GetString(1));
            }
            foreach (var (name, names) in grouped) tables.Add(name, names.ToArray());
        }
        var foreignKeys = new List<(string Table, string Name, string Definition)>();
        await using (var constraints = new NpgsqlCommand("SELECT c.conrelid::regclass::text,c.conname,pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='public' AND c.contype='f'", connection, transaction))
        await using (var rows = await constraints.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct)) foreignKeys.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        foreach (var table in tables.Keys)
            await Execute(connection, transaction, $"ALTER TABLE public.{Quote(table)} DISABLE TRIGGER USER", ct);
        foreach (var key in foreignKeys)
            await Execute(connection, transaction, $"ALTER TABLE {key.Table} DROP CONSTRAINT {Quote(key.Name)}", ct);
        // Pinned migrations may backfill or seed rows. The snapshot, rather than
        // those migration-time rows, is the authority for this isolated clone's data.
        await Execute(connection, transaction, $"TRUNCATE TABLE {string.Join(',', tables.Keys.Select(table => $"public.{Quote(table)}"))}", ct);

        using var process = StartTool(pgRestore, ["--data-only", "--no-owner", "--no-acl", "--file=-", dumpPath], database);
        var errors = DrainAsync(process.StandardError, ct);
        try
        {
            await LoadCopyStreamAsync(process.StandardOutput, tables, async (table, columns) =>
                await connection.BeginTextImportAsync($"COPY public.{Quote(table)} ({string.Join(',', columns.Select(Quote))}) FROM STDIN", ct), ct);
            await process.WaitForExitAsync(ct);
            await errors;
            if (process.ExitCode != 0) throw new RecoveryWorkerException("postgres_tool_failed");
            foreach (var key in foreignKeys)
                await Execute(connection, transaction, $"ALTER TABLE {key.Table} ADD CONSTRAINT {Quote(key.Name)} {key.Definition}", ct);
            foreach (var table in tables.Keys)
                await Execute(connection, transaction, $"ALTER TABLE public.{Quote(table)} ENABLE TRIGGER USER", ct);
            await transaction.CommitAsync(ct);
            return schemaInventorySha256;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    internal static async Task LoadCopyStreamAsync(TextReader input, IReadOnlyDictionary<string, string[]> tables,
        Func<string, string[], Task<TextWriter>> startCopy, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reader = new BoundedLines(input);
        TextWriter? copy = null;
        try
        {
            string? line;
            while ((line = await reader.ReadAsync(ct)) is not null)
            {
                if (copy is not null)
                {
                    if (line == "\\.") { await copy.DisposeAsync(); copy = null; }
                    else await copy.WriteLineAsync(line.AsMemory(), ct);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("--", StringComparison.Ordinal) ||
                    Regex.IsMatch(line, "^\\\\(?:un)?restrict [A-Za-z0-9]+$") ||
                    Regex.IsMatch(line, "^SET [a-z_]+ = [^;]*;$") ||
                    line == "SELECT pg_catalog.set_config('search_path', '', false);") continue;
                var match = Regex.Match(line, "^COPY public\\.([a-z_][a-z0-9_]*) \\(([^)]*)\\) FROM stdin;$");
                if (!match.Success || !tables.TryGetValue(match.Groups[1].Value, out var expected) || !seen.Add(match.Groups[1].Value))
                    throw new RecoveryWorkerException("recovery_dump_data_command_untrusted");
                var tokens = match.Groups[2].Value.Split(',').Select(item => item.Trim()).ToArray();
                if (tokens.Any(token => !Regex.IsMatch(token, "^(?:[a-z_][a-z0-9_]*|\"[a-z_][a-z0-9_]*\")$")))
                    throw new RecoveryWorkerException("recovery_dump_data_columns_untrusted");
                var columns = tokens.Select(item => item.Trim('"')).ToArray();
                if (!columns.SequenceEqual(expected, StringComparer.Ordinal))
                    throw new RecoveryWorkerException("recovery_dump_data_columns_untrusted");
                copy = await startCopy(match.Groups[1].Value, columns);
            }
            if (copy is not null || seen.Count != tables.Count)
                throw new RecoveryWorkerException("recovery_dump_data_incomplete");
        }
        finally { if (copy is not null) await copy.DisposeAsync(); }
    }

    internal static string CanonicalSchema(string sql)
    {
        var result = new StringBuilder();
        string? dollar = null;
        var quoted = false; var identifier = false;
        foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var outside = dollar is null && !quoted && !identifier;
            if (outside && (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("--", StringComparison.Ordinal) ||
                Regex.IsMatch(line, "^\\\\(?:un)?restrict [A-Za-z0-9]+$"))) continue;
            result.AppendLine(line);
            for (var i = 0; i < line.Length; i++)
            {
                if (dollar is not null)
                { if (line.AsSpan(i).StartsWith(dollar, StringComparison.Ordinal)) { i += dollar.Length - 1; dollar = null; } continue; }
                if (quoted)
                { if (line[i] == '\'') { if (i + 1 < line.Length && line[i + 1] == '\'') i++; else quoted = false; } continue; }
                if (identifier)
                { if (line[i] == '"') { if (i + 1 < line.Length && line[i + 1] == '"') i++; else identifier = false; } continue; }
                if (line[i] == '\'') quoted = true;
                else if (line[i] == '"') identifier = true;
                else if (line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-') break;
                else if (line[i] == '$')
                { var tag = Regex.Match(line[i..], "^\\$(?:[A-Za-z_][A-Za-z0-9_]*)?\\$"); if (tag.Success) { dollar = tag.Value; i += dollar.Length - 1; } }
            }
        }
        return result.ToString();
    }

    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);
    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    { await using var command = new NpgsqlCommand(sql, connection, transaction); await command.ExecuteNonQueryAsync(ct); }
    private static Process StartTool(string tool, IReadOnlyList<string> arguments, RecoveryDrillRunner.RecoveryDatabaseConnection database)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = database.Password; start.Environment["PGHOST"] = database.Host;
        start.Environment["PGPORT"] = database.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = database.Username; start.Environment["PGSSLMODE"] = "verify-full"; start.Environment["PGSSLROOTCERT"] = database.RootCertificatePath;
        return Process.Start(start) ?? throw new RecoveryWorkerException("postgres_tool_start_failed");
    }
    private static async Task<string> ReadToolAsync(string tool, IReadOnlyList<string> arguments, RecoveryDrillRunner.RecoveryDatabaseConnection database, CancellationToken ct)
    {
        using var process = StartTool(tool, arguments, database);
        var errors = DrainAsync(process.StandardError, ct);
        try
        {
            var result = new StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer, ct)) > 0)
            { if (result.Length + count > 16 * 1024 * 1024) throw new RecoveryWorkerException("trusted_schema_size_limit"); result.Append(buffer, 0, count); }
            await process.WaitForExitAsync(ct); await errors;
            if (process.ExitCode != 0) throw new RecoveryWorkerException("postgres_tool_failed");
            return result.ToString();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    private static async Task DrainAsync(TextReader input, CancellationToken ct)
    { var buffer = new char[8192]; while (await input.ReadAsync(buffer, ct) > 0) { } }
    private sealed class BoundedLines(TextReader input)
    {
        private readonly char[] buffer = new char[8192];
        private int position, length;
        private long total;
        internal async Task<string?> ReadAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (position == length)
                { length = await input.ReadAsync(buffer, ct); position = 0; if (length == 0) return line.Length == 0 ? null : Account(line, false); }
                var value = buffer[position++];
                if (line.Length >= 64 * 1024 * 1024)
                    throw new RecoveryWorkerException("recovery_dump_data_size_limit");
                if (value == '\n') return Account(line, true).TrimEnd('\r');
                line.Append(value);
            }
        }
        private string Account(StringBuilder line, bool newline)
        {
            var value = line.ToString();
            var bytes = Encoding.UTF8.GetByteCount(value) + (newline ? 1 : 0);
            total += bytes;
            if (bytes > 64 * 1024 * 1024 || total > 95L * 1024 * 1024 * 1024)
                throw new RecoveryWorkerException("recovery_dump_data_size_limit");
            return value;
        }
    }
}
