using Npgsql;

namespace Platform.Recovery.Worker;

/// <summary>Gives the fixed recovery API login read access only to the validated clone.
/// Validation and identity installation continue to use the isolated administrator.</summary>
internal static class RecoveryCandidateRuntimeDatabase
{
    internal static async Task ProvisionAsync(NpgsqlConnection connection, Guid candidateId,
        string username, string password, CancellationToken cancellationToken)
    {
        if (username != "platform_runtime" || password.Length is < 32 or > 128 ||
            password.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            !connection.Database.StartsWith("recovery_drill_", StringComparison.Ordinal))
            throw new RecoveryWorkerException("candidate_runtime_credentials_invalid");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var candidate = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM recovery_candidate_sessions WHERE candidate_id=$1
              AND state='owner_access_pending' AND expires_at_utc>now()
              AND target_database_name=current_database() AND owner_mapping_approval_sha256 IS NOT NULL)
            """, connection, transaction);
        candidate.Parameters.AddWithValue(candidateId);
        if (await candidate.ExecuteScalarAsync(cancellationToken) is not true)
            throw new RecoveryWorkerException("candidate_runtime_not_ready");
        // PostgreSQL role creation is cluster-wide. A fixed role must never inherit
        // another role's authority, own protected tables or carry elevated attributes.
        await using var inspect = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='platform_runtime'),
              EXISTS(SELECT 1 FROM pg_roles r WHERE r.rolname='platform_runtime' AND
                (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
                 OR r.rolinherit OR EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid)
                 OR EXISTS(SELECT 1 FROM pg_class c WHERE c.relowner=r.oid)
                 OR EXISTS(SELECT 1 FROM pg_database d WHERE d.datdba=r.oid)
                 OR EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspowner=r.oid)))
            """, connection, transaction);
        bool exists;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            exists = reader.GetBoolean(0);
            if (reader.GetBoolean(1)) throw new RecoveryWorkerException("candidate_runtime_role_unsafe");
        }
        // Both identifiers and password have fixed/strictly bounded alphabets. No
        // secret is returned or logged, and no persistent default grants are added.
        var sql = (exists ? "ALTER ROLE" : "CREATE ROLE") +
            " platform_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD '" + password + "';";
        await using (var role = new NpgsqlCommand(sql, connection, transaction))
            await role.ExecuteNonQueryAsync(cancellationToken);
        var tables = CaptureRunner.ProtectedRelationalTableClasses.SelectMany(item => item)
            .Append("content_objects").Distinct(StringComparer.Ordinal).ToArray();
        await using var grants = new NpgsqlCommand(
            "GRANT CONNECT ON DATABASE " + new NpgsqlCommandBuilder().QuoteIdentifier(connection.Database) +
            " TO platform_runtime; GRANT USAGE ON SCHEMA public TO platform_runtime; GRANT SELECT ON " +
            string.Join(',', tables.Select(table => "public." + new NpgsqlCommandBuilder().QuoteIdentifier(table))) +
            " TO platform_runtime; ALTER ROLE platform_runtime SET default_transaction_read_only=on; SET LOCAL ROLE platform_runtime;", connection, transaction);
        await grants.ExecuteNonQueryAsync(cancellationToken);
        // Do not rely solely on grants: inherited PUBLIC write privileges or an old
        // direct write grant must be rejected before this login reaches the API.
        await using var permissions = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
                AND (has_table_privilege(c.oid,'INSERT') OR has_table_privilege(c.oid,'UPDATE')
                  OR has_table_privilege(c.oid,'DELETE') OR has_table_privilege(c.oid,'TRUNCATE')
                  OR has_table_privilege(c.oid,'TRIGGER') OR has_table_privilege(c.oid,'REFERENCES')))
              OR has_schema_privilege('public','CREATE')
            """, connection, transaction);
        if (await permissions.ExecuteScalarAsync(cancellationToken) is not false)
            throw new RecoveryWorkerException("candidate_runtime_role_unsafe");
        await transaction.CommitAsync(cancellationToken);
    }
}
