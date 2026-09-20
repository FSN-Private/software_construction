using Npgsql;
using Platform.Api.Features.VersionControl.Working;
using Platform.Api.Features.WindowsSync;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

internal static class WindowsSyncChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        const string owner = "test-owner";
        var library = "windows-sync-" + Guid.NewGuid().ToString("N");
        var branch = "reader-scope";
        Guid asset;
        string digest;
        long length;
        long lifecycleVersion;
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using (var command = Command(connection, "SELECT a.id,a.original_sha256,c.byte_length,a.lifecycle_version FROM assets a JOIN content_objects c ON c.sha256=a.original_sha256 WHERE a.owner_id=$1 AND a.state IN ('active','restored') AND c.integrity_state='authoritative' LIMIT 1", owner))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                IngestChecks.Check(await reader.ReadAsync(), "Windows sync integration requires one authoritative asset.");
                asset = reader.GetGuid(0);
                digest = reader.GetString(1).Trim();
                length = reader.GetInt64(2);
                lifecycleVersion = reader.GetInt64(3);
            }

            var at = DateTimeOffset.UtcNow;
            var manifest = new VersionControlManifest(library, [], owner, at, at, "Windows sync reader scope", [new(asset, null, "added", null, digest, "reader/photo.dng")]);
            var bytes = VersionControlManifestCodec.Write(manifest);
            var commit = VersionControlManifestCodec.Digest(bytes);
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$2,$4,$4,$5,1,1,$6)", default, commit, owner, library, at, manifest.Message, bytes);
            await ExecuteAsync(connection, "INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,change_kind,content_sha256,display_path) VALUES($1,0,$2,'added',$3,$4)", default, commit, asset, digest, "reader/photo.dng");
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES($1,$2,$3,$3,'branch',$4)", default, owner, library, branch, commit);
            await ExecuteAsync(connection, "INSERT INTO vc_materialized_state(owner_id,library_id,normalized_ref,source_commit_id,asset_id,content_sha256,display_path,lifecycle_state,rebuilt_at) VALUES($1,$2,$3,$4,$5,$6,$7,'active',$8)", default, owner, library, branch, commit, asset, digest, "reader/photo.dng", at);
        }

        var service = new WindowsSyncService(db, storage, new WorkingService(db, storage));
        await service.RegisterAsync(owner, library, "reader-device", branch, "op_windows_sync_register", default);
        var page = await service.NamespaceAsync(owner, library, "reader-device", branch, null, 1000, default);
        IngestChecks.Check(page.Items is [{ AssetId: var id, AssetVersion: var version }] && id == asset && version == lifecycleVersion && page.Items[0].Length == length && page.Cursor is not null, "PostgreSQL namespace enumeration must return lifecycle metadata and update the terminal cursor after disposing its reader.");
        var idle = await service.NamespaceAsync(owner, library, "reader-device", branch, page.Cursor, 1000, default);
        IngestChecks.Check(idle.Items.Count == 0 && idle.Cursor == page.Cursor, "An idle namespace poll must retain the terminal head cursor.");
    }
}
