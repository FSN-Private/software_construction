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
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/013_rendering_candidate_acceptance.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/014_gallery_metadata.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/015_gallery_search_presentation.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/016_gallery_web_content.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/017_gallery_edit_idempotency.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/018_lineage_merge_content.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/019_tag_audit_link.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/020_lineage_branch_reachability.sql"), default);
            await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/021_export_baskets.sql"), default);
            var recovery = await File.ReadAllTextAsync("apps/api/Database/Migrations/022_backup_recovery.sql");
            await ExecuteAsync(connection, recovery, default);
            await ExecuteAsync(connection, recovery, default);
            await migrate.CommitAsync();
        }
        Check(await ScalarAsync(connection, "SELECT state FROM assets WHERE id=$1 AND owner_id='migration-fixture' AND original_sha256 IS NULL AND lifecycle_version=1", default, id) as string == "prepared", "Old readers retain original columns and identity after expansion");
        Check(await ScalarAsync(connection, "SELECT handoff_id IS NOT NULL FROM assets WHERE id=$1", default, id) is true, "New recovery metadata is added without replacing identity");
        Check(await ScalarAsync(connection, "SELECT to_regclass('render_requests') IS NOT NULL AND to_regclass('render_provenance') IS NOT NULL AND to_regclass('render_intake_control') IS NOT NULL", default) is true, "Rendering schema is an additive migration beside source identity tables");
        Check(await ScalarAsync(connection, "SELECT state FROM rendering_compatibility WHERE matrix_version='rendering-v1'", default) as string == "approved", "Compatibility migration preserves the prior approved matrix until protected promotion");
        Check(await ScalarAsync(connection, "SELECT column_name FROM information_schema.columns WHERE table_name='render_intake_control' AND column_name='acceptance_owner'", default) as string == "acceptance_owner", "Candidate acceptance has an owner-scoped intake control.");
        Check(await ScalarAsync(connection, "SELECT to_regclass('gallery_projection') IS NOT NULL AND to_regclass('gallery_facet_values') IS NOT NULL AND to_regclass('gallery_refresh') IS NOT NULL AND to_regclass('gallery_preferences') IS NOT NULL AND to_regclass('gallery_web_overlay_revisions') IS NOT NULL", default) is true, "Gallery schema expands beside source and version-control state");
        Check(await ScalarAsync(connection, "SELECT to_regclass('lineage_merge_content') IS NOT NULL AND to_regclass('vc_lineage_reachable') IS NOT NULL", default) is true, "Lineage provenance and derived reachability expand beside immutable history");
        Check(await ScalarAsync(connection, "SELECT to_regclass('recovery_points') IS NOT NULL AND to_regclass('recovery_point_components') IS NOT NULL AND to_regclass('recovery_point_objects') IS NOT NULL AND to_regclass('recovery_change_ledger') IS NOT NULL AND to_regclass('media_purge_tombstones') IS NOT NULL", default) is true, "Recovery evidence and purge fences are added without replacing legacy source identity tables");
        Check(await ScalarAsync(connection, "SELECT state FROM assets WHERE id=$1 AND owner_id='migration-fixture' AND original_sha256 IS NULL AND lifecycle_version=1", default, id) as string == "prepared", "Old readers retain original columns and identity after recovery schema expansion");
        await ExecuteAsync(connection, "INSERT INTO gallery_generations(owner_id,library_id,branch_name,generation,schema_version,state) VALUES('migration-fixture','default','main',1,1,'active'),('migration-fixture','default','main',2,1,'building')", default);
        await using (var rollout = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "UPDATE gallery_generations SET state='retired' WHERE owner_id='migration-fixture' AND generation=1; UPDATE gallery_generations SET state='active' WHERE owner_id='migration-fixture' AND generation=2", default);
            await rollout.RollbackAsync();
        }
        Check(await ScalarAsync(connection, "SELECT generation FROM gallery_generations WHERE owner_id='migration-fixture' AND library_id='default' AND branch_name='main' AND state='active'", default) is long active && active == 1, "Gallery generation rollback retains the prior complete index");
        await ExecuteAsync(connection, "DELETE FROM gallery_generations WHERE owner_id='migration-fixture'", default);
        await ExecuteAsync(connection, "DELETE FROM assets WHERE id=$1 AND owner_id='migration-fixture'", default, id);
        Console.WriteLine("Migration rehearsal passed: transactional failure rollback, expansion, old-reader compatibility, stable identity.");
    }
}
