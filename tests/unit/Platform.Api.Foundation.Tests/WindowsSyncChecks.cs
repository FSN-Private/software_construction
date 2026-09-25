using Npgsql;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Platform.Api.Features.Gallery.Metadata;
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
        const string branch = "reader-scope";
        Guid asset;
        Guid edit;
        string assetDigest;
        string editDigest;
        string originalPath;
        long assetLength;
        long editLength;
        long assetVersion;
        long editVersion;
        int editOrdinal;
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using (var command = Command(connection, """
                SELECT a.original_sha256,e.xmp_sha256,oa.byte_length,oe.byte_length
                FROM assets a
                JOIN edit_identities e ON e.asset_id=a.id AND e.state IN ('active','restored')
                JOIN content_objects oa ON oa.sha256=a.original_sha256 AND oa.integrity_state='authoritative'
                JOIN content_objects oe ON oe.sha256=e.xmp_sha256 AND oe.integrity_state='authoritative'
                WHERE a.owner_id=$1 AND a.state IN ('active','restored')
                LIMIT 1
                """, owner))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                IngestChecks.Check(await reader.ReadAsync(), "Windows sync integration requires authoritative original and XMP content.");
                assetDigest = reader.GetString(0).Trim();
                editDigest = reader.GetString(1).Trim();
                assetLength = reader.GetInt64(2);
                editLength = reader.GetInt64(3);
            }

            asset = Guid.NewGuid();
            edit = Guid.NewGuid();
            originalPath = $"windows-sync-fixture/{asset:N}.dng";
            assetVersion = 1;
            editVersion = 1;
            editOrdinal = 0;
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,original_sha256,state,format) VALUES($1,$2,$3,'active','dng'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($4,$1,0,$5,'windows-sync-fixture'); INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active) VALUES($6,$1,$2,$7,$8,true)", default, asset, owner, assetDigest, edit, editDigest, Guid.NewGuid(), originalPath, originalPath.ToUpperInvariant());

            var now = DateTimeOffset.UtcNow;
            var at = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var entries = new VersionControlEntry[]
            {
                new(asset, null, "added", null, assetDigest, originalPath),
                new(asset, edit, "added", null, editDigest, SidecarPath(originalPath, editOrdinal)),
            };
            var manifest = new VersionControlManifest(library, [], owner, at, at, "Windows sync reader scope", entries);
            var bytes = VersionControlManifestCodec.Write(manifest);
            var commit = VersionControlManifestCodec.Digest(bytes);
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$2,$4,$4,$5,1,1,$6)", default, commit, owner, library, at, manifest.Message, bytes);
            await ExecuteAsync(connection, "INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,change_kind,content_sha256,display_path) VALUES($1,0,$2,'added',$3,$4); INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,content_sha256,display_path) VALUES($1,1,$2,$5,'added',$6,$7)", default, commit, asset, assetDigest, originalPath, edit, editDigest, SidecarPath(originalPath, editOrdinal));
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) VALUES($1,$2,$3,$3,'branch',$4)", default, owner, library, branch, commit);
            await ExecuteAsync(connection, "INSERT INTO vc_materialized_state(owner_id,library_id,normalized_ref,source_commit_id,asset_id,content_sha256,display_path,lifecycle_state,rebuilt_at) VALUES($1,$2,$3,$4,$5,$6,$7,'active',$8); INSERT INTO vc_materialized_state(owner_id,library_id,normalized_ref,source_commit_id,asset_id,edit_id,content_sha256,display_path,lifecycle_state,rebuilt_at) VALUES($1,$2,$3,$4,$5,$9,$10,$11,'active',$8)", default, owner, library, branch, commit, asset, assetDigest, originalPath, at, edit, editDigest, SidecarPath(originalPath, editOrdinal));
        }

        var service = new WindowsSyncService(db, storage, new WorkingService(db, storage));
        var lifecycle = new AssetService(db, storage);
        await service.RegisterAsync(owner, library, "reader-device-a", branch, "op_windows_sync_register_a", default);
        await service.RegisterAsync(owner, library, "reader-device-b", branch, "op_windows_sync_register_b", default);
        var pageA = await service.NamespaceAsync(owner, library, "reader-device-a", branch, null, 1000, default);
        var pageB = await service.NamespaceAsync(owner, library, "reader-device-b", branch, null, 1000, default);
        IngestChecks.Check(pageA.Items.Count == 2 && pageA.Items.Sum(item => item.Length) == assetLength + editLength && pageA.Cursor is not null, "PostgreSQL namespace enumeration must return the authoritative asset/edit pair and update its cursor after disposing the reader.");

        var movedPath = $"reader/{Guid.NewGuid():N}.dng";
        await lifecycle.ChangeAsync(owner, asset, false, "move", assetVersion, movedPath, "windows-sync-move", "op_windows_sync_move", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-b", branch, pageB.Cursor, 1000, default));
        pageB = await service.NamespaceAsync(owner, library, "reader-device-b", branch, null, 1000, default);
        IngestChecks.Check(pageB.Items.Select(item => item.Path).Order().SequenceEqual(new[] { movedPath, SidecarPath(movedPath, editOrdinal) }.Order()), "A lifecycle move must converge on the authoritative original and sidecar paths for another device.");

        await lifecycle.ChangeAsync(owner, edit, true, "delete", editVersion, null, "windows-sync-delete-edit", "op_windows_sync_delete_edit", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-b", branch, pageB.Cursor, 1000, default));
        pageB = await service.NamespaceAsync(owner, library, "reader-device-b", branch, null, 1000, default);
        IngestChecks.Check(pageB.Items is [{ EditId: null }], "An authoritative edit delete must remove the sidecar from another device namespace without deleting content.");

        await lifecycle.ChangeAsync(owner, edit, true, "restore", editVersion + 1, null, "windows-sync-restore-edit", "op_windows_sync_restore_edit", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-b", branch, pageB.Cursor, 1000, default));
        pageB = await service.NamespaceAsync(owner, library, "reader-device-b", branch, null, 1000, default);
        IngestChecks.Check(pageB.Items.Count == 2, "An authoritative edit restore must restore the sidecar namespace entry.");

        await lifecycle.ChangeAsync(owner, asset, false, "delete", assetVersion + 1, null, "windows-sync-delete-asset", "op_windows_sync_delete_asset", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-a", branch, pageA.Cursor, 1000, default));
        pageA = await service.NamespaceAsync(owner, library, "reader-device-a", branch, null, 1000, default);
        IngestChecks.Check(pageA.Items.Count == 0, "An authoritative asset delete must hide the original and its sidecars from another device namespace.");

        await lifecycle.ChangeAsync(owner, asset, false, "restore", assetVersion + 2, movedPath, "windows-sync-restore-asset", "op_windows_sync_restore_asset", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-a", branch, pageA.Cursor, 1000, default));
        pageA = await service.NamespaceAsync(owner, library, "reader-device-a", branch, null, 1000, default);
        IngestChecks.Check(pageA.Items.Count == 2 && pageA.Items.Any(item => item.Path == movedPath), "An authoritative asset restore must return the asset group at its current path.");
        var idle = await service.NamespaceAsync(owner, library, "reader-device-a", branch, pageA.Cursor, 1000, default);
        IngestChecks.Check(idle.Items.Count == 0 && idle.Cursor == pageA.Cursor, "An idle namespace poll must retain the terminal branch/lifecycle cursor.");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gallery:LibraryId"] = library }).Build();
        var gallery = new GalleryEditService(db, storage, new WorkingService(db, storage), configuration);
        using var change = JsonDocument.Parse("{\"title\":\"Shared web title\"}");
        var edited = await gallery.PatchAsync(owner, library, asset,
            new(branch, edit, 0, editDigest, change.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value)),
            "op_windows_sync_gallery_edit", default);
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-a", branch, pageA.Cursor, 1000, default));
        await ExpectExpiredAsync(() => service.NamespaceAsync(owner, library, "reader-device-b", branch, pageB.Cursor, 1000, default));
        pageA = await service.NamespaceAsync(owner, library, "reader-device-a", branch, null, 1000, default);
        pageB = await service.NamespaceAsync(owner, library, "reader-device-b", branch, null, 1000, default);
        var webA = pageA.Items.Single(item => item.EditId == edit);
        var webB = pageB.Items.Single(item => item.EditId == edit);
        IngestChecks.Check(webA.WebWorking && webB.WebWorking && webA.Sha256 == edited.XmpSha256 && webB.ContentId == webA.ContentId,
            "A web edit must resolve to the same branch-scoped XMP object on two devices.");
        var downloaded = await service.ContentAsync(owner, library, "reader-device-b", webB.ContentId, "op_windows_sync_web_download", default);
        await using (downloaded.Bytes)
        {
            using var text = new StreamReader(downloaded.Bytes);
            IngestChecks.Check((await text.ReadToEndAsync()).Contains("Shared web title", StringComparison.Ordinal), "Both devices can download the verified web sidecar.");
        }
        await service.RevokeAsync(owner, library, "reader-device-b", "op_windows_sync_revoke_b", default);
        await ExpectProblemAsync(() => service.ContentAsync(owner, library, "reader-device-b", webB.ContentId, "op_windows_sync_denied", default), "device_revoked");
        await ExecuteBranchSwitchAsync(db, service, owner, library, branch, "reader-device-a", webA.ContentId);
    }

    private static async Task ExecuteBranchSwitchAsync(NpgsqlDataSource db, WindowsSyncService service, string owner, string library, string branch, string device, Guid oldContentId)
    {
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id) SELECT owner_id,library_id,'other-branch','other-branch','branch',head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", default, owner, library, branch);
        await service.RegisterAsync(owner, library, device, "other-branch", "op_windows_sync_switch", default);
        await ExpectProblemAsync(() => service.ContentAsync(owner, library, device, oldContentId, "op_windows_sync_old_branch", default), "content_not_authoritative");
    }

    private static async Task ExpectProblemAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (VersionControlProblem problem) when (problem.Code == code) { return; }
        throw new InvalidOperationException("Expected Windows sync error: " + code);
    }

    private static async Task ExpectExpiredAsync(Func<Task> action)
    {
        try { await action(); }
        catch (VersionControlProblem error) when (error.Code == "cursor_expired") { return; }
        throw new InvalidOperationException("Lifecycle-only namespace changes must expire existing device cursors.");
    }

    private static string SidecarPath(string assetPath, int ordinal)
    {
        if (ordinal == 0) return assetPath + ".xmp";
        var slash = assetPath.LastIndexOf('/');
        var dot = assetPath.LastIndexOf('.');
        if (dot <= slash) dot = assetPath.Length;
        return assetPath.Insert(dot, $"_{ordinal:D2}") + ".xmp";
    }
}
