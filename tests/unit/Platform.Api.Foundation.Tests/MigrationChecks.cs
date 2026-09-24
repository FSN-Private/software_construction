using Npgsql;
using static IngestChecks;
using static Platform.Storage.IngestDb;

internal static class MigrationChecks
{
    public static async Task ApplyAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/005_ingest_storage.sql"), default);
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,state) VALUES($1,'migration-fixture','prepared')", default, id);
        var expand = await File.ReadAllTextAsync("apps/api/Database/Migrations/005_ingest_workflows.sql");
        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, expand, default);
            await rollback.RollbackAsync();
        }
        Check(await ScalarAsync(connection, "SELECT state FROM assets WHERE id=$1", default, id) as string == "prepared", "Failed schema rollout leaves old identity/state intact");
        await using (var migrate = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, expand, default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/006_xmp_version_control.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/007_windows_cloud_files_sync.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/008_windows_sync_lifecycle_revision.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/008_rendering.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/009_rendering_operations.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/010_rendering_identity.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/011_rendering_events.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/012_rendering_hardened_compatibility.sql"), default);
            await migrate.CommitAsync();
        }
        Check(await ScalarAsync(connection, "SELECT state FROM assets WHERE id=$1 AND owner_id='migration-fixture' AND original_sha256 IS NULL AND lifecycle_version=1", default, id) as string == "prepared", "Old readers retain original columns and identity after expansion");
        Check(await ScalarAsync(connection, "SELECT handoff_id IS NOT NULL FROM assets WHERE id=$1", default, id) is true, "New recovery metadata is added without replacing identity");
        Check(await ScalarAsync(connection, "SELECT to_regclass('render_requests') IS NOT NULL AND to_regclass('render_provenance') IS NOT NULL AND to_regclass('render_intake_control') IS NOT NULL", default) is true, "Rendering schema is an additive migration beside source identity tables");
        Check(await ScalarAsync(connection, "SELECT state FROM rendering_compatibility WHERE matrix_version='rendering-v1'", default) as string == "approved", "Compatibility migration preserves the prior approved matrix until protected promotion");
        await ExecuteAsync(connection, "DELETE FROM assets WHERE id=$1 AND owner_id='migration-fixture'", default, id);
        Console.WriteLine("Migration rehearsal passed: transactional failure rollback, expansion, old-reader compatibility, stable identity.");
    }
}
