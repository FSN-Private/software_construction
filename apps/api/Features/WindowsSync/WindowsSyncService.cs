using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Platform.Api.Features.VersionControl;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Working;
using Platform.Contracts.VersionControl;
using Platform.Contracts.WindowsSync;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.WindowsSync;

public sealed class WindowsSyncService(NpgsqlDataSource dataSource, S3ObjectStore storage, WorkingService working)
{
    private const long MaxXmpBytes = 16L * 1024 * 1024;
    private const string SelectedNamespace = """
        WITH selected AS (
          SELECT s.owner_id,s.library_id,s.normalized_ref,s.asset_id,s.edit_id,
                 COALESCE(w.current_content_sha256,s.content_sha256) AS content_sha256,w.id IS NOT NULL AS web_working
          FROM vc_materialized_state s
          JOIN assets a ON a.id=s.asset_id AND a.owner_id=s.owner_id AND a.state IN ('active','restored')
          LEFT JOIN edit_identities e ON e.id=s.edit_id
          LEFT JOIN vc_working_changes w ON w.owner_id=s.owner_id AND w.library_id=s.library_id AND w.branch_name=s.normalized_ref
            AND w.client_id='__gallery_web__' AND w.asset_id=s.asset_id AND w.edit_id IS NOT DISTINCT FROM s.edit_id AND w.change_kind<>'deleted'
          WHERE s.owner_id=$1 AND s.library_id=$2 AND s.normalized_ref=$3 AND s.content_sha256 IS NOT NULL
            AND (s.edit_id IS NULL OR e.state IN ('active','restored'))
          UNION ALL
          SELECT w.owner_id,w.library_id,w.branch_name,w.asset_id,w.edit_id,w.current_content_sha256,true
          FROM vc_working_changes w
          JOIN assets a ON a.id=w.asset_id AND a.owner_id=w.owner_id AND a.state IN ('active','restored')
          JOIN edit_identities e ON e.id=w.edit_id AND e.state IN ('active','restored')
          WHERE w.owner_id=$1 AND w.library_id=$2 AND w.branch_name=$3 AND w.client_id='__gallery_web__'
            AND w.current_content_sha256 IS NOT NULL AND w.change_kind<>'deleted'
            AND NOT EXISTS(SELECT 1 FROM vc_materialized_state s WHERE s.owner_id=w.owner_id AND s.library_id=w.library_id
              AND s.normalized_ref=w.branch_name AND s.asset_id=w.asset_id AND s.edit_id IS NOT DISTINCT FROM w.edit_id)
        )
        """;

    public async Task<WindowsSyncDeviceView> RegisterAsync(string owner, string library, string device, string branchValue, string operationId, CancellationToken cancellationToken)
    {
        var branch = RefNames.Normalize(branchValue);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, owner, cancellationToken);
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch) is null) throw new VersionControlProblem("ref_not_found", 404);
        await ExecuteAsync(connection, "INSERT INTO windows_sync_devices(owner_id,library_id,device_id,normalized_ref) VALUES($1,$2,$3,$4) ON CONFLICT(owner_id,library_id,device_id) DO UPDATE SET normalized_ref=excluded.normalized_ref,cursor=NULL,version=windows_sync_devices.version+1,revoked=false,updated_at=now()", cancellationToken, owner, library, device, branch);
        await ExecuteAsync(connection, "DELETE FROM windows_sync_device_content WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device);
        var stableId = StableId(device);
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "windows-sync.device.registered", stableId, "accepted", "windows-sync-device", stableId.ToString("D"), [], cancellationToken);
        var result = await DeviceAsync(connection, owner, library, device, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WindowsSyncDeviceView> RevokeAsync(string owner, string library, string device, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, owner, cancellationToken);
        if (await ExecuteAsync(connection, "UPDATE windows_sync_devices SET revoked=true,version=version+1,updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device) != 1) throw new VersionControlProblem("device_not_found", 404);
        await ExecuteAsync(connection, "DELETE FROM windows_sync_device_content WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device);
        var stableId = StableId(device);
        await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "windows-sync.device.revoked", stableId, "accepted", "windows-sync-device", stableId.ToString("D"), [], cancellationToken);
        var result = await DeviceAsync(connection, owner, library, device, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WindowsSyncNamespacePage> NamespaceAsync(string owner, string library, string device, string branchValue, string? cursorValue, int limit, CancellationToken cancellationToken)
    {
        var branch = RefNames.Normalize(branchValue);
        limit = Math.Clamp(limit == 0 ? 1000 : limit, 1, 1000);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureDeviceAsync(connection, owner, library, device, branch, cancellationToken);
        var head = ((string?)await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch))?.Trim() ?? throw new VersionControlProblem("ref_not_found", 404);
        var lifecycleRevision = Convert.ToInt64(await ScalarAsync(connection, "SELECT COALESCE((SELECT revision FROM windows_sync_owner_revisions WHERE owner_id=$1),0)", cancellationToken, owner), System.Globalization.CultureInfo.InvariantCulture);
        var overlayRevision = Convert.ToInt64(await ScalarAsync(connection, "SELECT COALESCE((SELECT revision FROM gallery_web_overlay_revisions WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3),0)", cancellationToken, owner, library, branch), System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{head}\n{lifecycleRevision}\n{overlayRevision}")));
        var cursor = WindowsSyncCursor.Decode(cursorValue);
        if (cursor is not null && cursor.Head != snapshot) throw new VersionControlProblem("cursor_expired");

        await ExecuteAsync(connection, SelectedNamespace + "INSERT INTO windows_sync_content_ids(content_sha256) SELECT DISTINCT content_sha256 FROM selected ON CONFLICT DO NOTHING", cancellationToken, owner, library, branch);
        await ExecuteAsync(connection, SelectedNamespace + "INSERT INTO windows_sync_device_content(owner_id,library_id,device_id,content_id) SELECT $1,$2,$4,i.content_id FROM selected s JOIN windows_sync_content_ids i ON i.content_sha256=s.content_sha256 ON CONFLICT DO NOTHING", cancellationToken, owner, library, branch, device);
        await using var command = Command(connection, SelectedNamespace + """
            SELECT s.asset_id,s.edit_id,i.content_id,p.display_path,s.content_sha256,o.byte_length,COALESCE(e.state,a.state),a.lifecycle_version,e.lifecycle_version,e.duplicate_ordinal,s.web_working,
                   EXISTS(SELECT 1 FROM vc_working_changes local WHERE local.owner_id=$1 AND local.library_id=$2 AND local.client_id=$7 AND local.branch_name=$3
                     AND local.asset_id=s.asset_id AND local.edit_id IS NOT DISTINCT FROM s.edit_id AND local.current_content_sha256 IS DISTINCT FROM s.content_sha256) AS conflict
            FROM selected s
            JOIN assets a ON a.id=s.asset_id
            LEFT JOIN edit_identities e ON e.id=s.edit_id
            JOIN LATERAL(SELECT display_path FROM asset_paths WHERE asset_id=s.asset_id AND active ORDER BY effective_from DESC,id DESC LIMIT 1) p ON true
            JOIN content_objects o ON o.sha256=s.content_sha256 AND o.integrity_state='authoritative'
            JOIN windows_sync_content_ids i ON i.content_sha256=s.content_sha256
            WHERE ($4::uuid IS NULL OR (s.asset_id,COALESCE(s.edit_id,'00000000-0000-0000-0000-000000000000'::uuid)) > ($4,COALESCE($5::uuid,'00000000-0000-0000-0000-000000000000'::uuid)))
            ORDER BY s.asset_id,COALESCE(s.edit_id,'00000000-0000-0000-0000-000000000000'::uuid)
            LIMIT $6
            """, owner, library, branch, cursor?.AssetId, cursor?.EditId, limit + 1, device);
        var result = new List<WindowsSyncNamespaceItem>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var edit = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
                var path = edit.HasValue ? SidecarPath(reader.GetString(3), reader.GetInt32(9)) : reader.GetString(3);
                result.Add(new(reader.GetGuid(0), edit, reader.GetGuid(2), path, reader.GetString(4).Trim(), reader.GetInt64(5), reader.GetString(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetBoolean(10), reader.GetBoolean(11)));
            }
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        var next = result.Count > 0 ? WindowsSyncCursor.Encode(new(snapshot, result[^1].AssetId, result[^1].EditId)) : cursorValue ?? WindowsSyncCursor.Encode(new(snapshot, Guid.Empty, null));
        await ExecuteAsync(connection, "UPDATE windows_sync_devices SET cursor=$4,updated_at=now() WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device, next);
        return new(result, next, hasMore);
    }

    public async Task<WindowsSyncContentDownload> ContentAsync(string owner, string library, string device, Guid contentId, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, owner, cancellationToken);
        var branch = (string?)await ScalarAsync(connection, "SELECT normalized_ref FROM windows_sync_devices WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device) ?? throw new VersionControlProblem("device_not_found", 404);
        await EnsureDeviceAsync(connection, owner, library, device, branch, cancellationToken);
        await using var command = Command(connection, SelectedNamespace + "SELECT c.sha256,c.byte_length,c.storage_version FROM windows_sync_device_content d JOIN windows_sync_content_ids i ON i.content_id=d.content_id JOIN content_objects c ON c.sha256=i.content_sha256 AND c.integrity_state='authoritative' WHERE d.owner_id=$1 AND d.library_id=$2 AND d.device_id=$4 AND d.content_id=$5 AND EXISTS(SELECT 1 FROM selected s WHERE s.content_sha256=c.sha256)", owner, library, branch, device, contentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new VersionControlProblem("content_not_authoritative", 404);
        var address = new ContentAddress(reader.GetString(0).Trim(), reader.GetInt64(1));
        var version = reader.GetString(2);
        await reader.DisposeAsync();
        var bytes = await storage.ReadVerifiedAsync(address.AuthoritativeKey, version, address, cancellationToken);
        try
        {
            await AuditAndRecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
                "windows-sync.content.downloaded", contentId, "accepted", "windows-sync-content", contentId.ToString("D"),
                [address.Sha256], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(bytes, address);
        }
        catch
        {
            await bytes.DisposeAsync();
            throw;
        }
    }

    public async Task<WindowsSyncMutationView> UploadAsync(string owner, string library, string device, string branchValue, Guid asset, Guid? edit, string path, string? baseSha256, string expectedSha256, HttpRequest request, string operationId, CancellationToken cancellationToken)
    {
        var branch = RefNames.Normalize(branchValue);
        var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
        if (request.ContentLength is null or < 1 or > MaxXmpBytes || !path.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase) || !ValidSha256(expectedSha256) || baseSha256 is not null && !ValidSha256(baseSha256) || string.IsNullOrWhiteSpace(idempotencyKey)) throw new VersionControlProblem("invalid_xmp", 400);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", cancellationToken, owner + ":" + library + ":" + idempotencyKey);
        await EnsureDeviceAsync(connection, owner, library, device, branch, cancellationToken);
        var fingerprint = VersionControlIdempotency.Fingerprint(new { device, branch, asset, edit, path, baseSha256, expectedSha256, request.ContentLength });
        var replay = await VersionControlIdempotency.ReadAsync<WindowsSyncMutationView>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) { await transaction.CommitAsync(cancellationToken); return replay; }
        var current = (string?)await ScalarAsync(connection, "SELECT COALESCE((SELECT current_content_sha256 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6),(SELECT current_content_sha256 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id='__gallery_web__' AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6),(SELECT content_sha256 FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6))", cancellationToken, owner, library, device, branch, asset, edit);
        var localChange = await ScalarAsync(connection, "SELECT 1 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6", cancellationToken, owner, library, device, branch, asset, edit) is not null;
        var webSha = (string?)await ScalarAsync(connection, "SELECT current_content_sha256 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id='__gallery_web__' AND branch_name=$3 AND asset_id=$4 AND edit_id IS NOT DISTINCT FROM $5", cancellationToken, owner, library, branch, asset, edit);
        if (!localChange && webSha is not null && !string.Equals(webSha.Trim(), baseSha256, StringComparison.Ordinal))
            throw new VersionControlProblem("working_state_conflict");
        if (string.Equals(current?.Trim(), expectedSha256, StringComparison.Ordinal))
        {
            var existingVersion = Convert.ToInt64(await ScalarAsync(connection, "SELECT version FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6", cancellationToken, owner, library, device, branch, asset, edit), System.Globalization.CultureInfo.InvariantCulture);
            var existing = new WindowsSyncMutationView(operationId, "working", existingVersion, expectedSha256);
            await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, null, existing, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        if (!string.Equals(current?.Trim(), baseSha256, StringComparison.Ordinal)) throw new VersionControlProblem("working_state_conflict");

        await using var bytes = new FileStream(Path.Combine(Path.GetTempPath(), $"windows-sync-{Guid.NewGuid():N}.xmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await request.Body.CopyToAsync(bytes, cancellationToken);
        bytes.Position = 0;
        var address = await ContentAddress.FromAsync(bytes, cancellationToken);
        if (address.Sha256 != expectedSha256 || address.Length != request.ContentLength) throw new VersionControlProblem("checksum_mismatch", 422);
        bytes.Position = 0;
        try
        {
            using var xml = XmlReader.Create(bytes, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmpBytes, CloseInput = false });
            if ((await XDocument.LoadAsync(xml, LoadOptions.None, cancellationToken)).Root is null) throw new XmlException();
        }
        catch (XmlException) { throw new VersionControlProblem("invalid_xmp", 400); }
        bytes.Position = 0;
        await VersionControlAdapters.LockAndCheckRecoveryPurgeDigestsAsync(
            connection, [address.Sha256], cancellationToken);
        var storageVersion = await storage.PublishVerifiedAsync(address, bytes, cancellationToken);

        var batch = StableId("batch:" + idempotencyKey);
        var session = StableId("session:" + idempotencyKey);
        await ExecuteAsync(connection, """
            INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative') ON CONFLICT(sha256) DO UPDATE SET storage_version=COALESCE(content_objects.storage_version,excluded.storage_version),integrity_state='authoritative';
            INSERT INTO import_batches(id,owner_id,idempotency_key,atomic,state) VALUES($5,$6,$7,false,'completed') ON CONFLICT(owner_id,idempotency_key) DO NOTHING;
            INSERT INTO upload_sessions(id,batch_id,owner_id,target_key,expected_sha256,expected_length,state,expires_at,lifecycle_version,logical_path,category,asset_id,edit_id,verified_at,operation_id)
            VALUES($8,$5,$6,'windows-sync/'||$8,$1,$2,'completed',now()+interval '24 hours',1,$9,'xmp',$10,$11,now(),$12) ON CONFLICT(id) DO NOTHING;
            INSERT INTO import_batch_items(id,batch_id,upload_session_id,state) VALUES(gen_random_uuid(),$5,$8,'completed') ON CONFLICT(upload_session_id) DO NOTHING;
            """, cancellationToken, address.Sha256, address.Length, address.AuthoritativeKey, storageVersion, batch, owner, "windows-sync:" + idempotencyKey, session, path, asset, edit, operationId);
        var version = Convert.ToInt64(await ScalarAsync(connection, "SELECT COALESCE((SELECT version FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6),0)", cancellationToken, owner, library, device, branch, asset, edit), System.Globalization.CultureInfo.InvariantCulture);
        var changed = await working.PutInTransactionAsync(connection, transaction, owner, library, device,
            new(asset, edit, branch, "modified", baseSha256, address.Sha256, path, null, version), operationId, cancellationToken);
        var result = new WindowsSyncMutationView(operationId, "working", changed.Version, changed.ContentSha256);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task EnsureDeviceAsync(NpgsqlConnection connection, string owner, string library, string device, string branch, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, "SELECT normalized_ref||':'||revoked::text FROM windows_sync_devices WHERE owner_id=$1 AND library_id=$2 AND device_id=$3", cancellationToken, owner, library, device) as string;
        if (value is null) throw new VersionControlProblem("device_not_found", 404);
        if (value.EndsWith(":true", StringComparison.Ordinal)) throw new VersionControlProblem("device_revoked", 403);
        if (!value.StartsWith(branch + ":", StringComparison.Ordinal)) throw new VersionControlProblem("checkout_conflict");
    }

    private static async Task<WindowsSyncDeviceView> DeviceAsync(NpgsqlConnection connection, string owner, string library, string device, string operationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT d.device_id,d.library_id,d.normalized_ref,d.cursor,d.version,COALESCE(c.version,0),d.revoked FROM windows_sync_devices d LEFT JOIN vc_checkouts c ON c.owner_id=d.owner_id AND c.library_id=d.library_id AND c.client_id=d.device_id WHERE d.owner_id=$1 AND d.library_id=$2 AND d.device_id=$3", owner, library, device);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new VersionControlProblem("device_not_found", 404);
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetBoolean(6), operationId);
    }

    internal static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    internal static string SidecarPath(string assetPath, int ordinal)
    {
        if (ordinal == 0) return assetPath + ".xmp";
        var slash = assetPath.LastIndexOf('/');
        var dot = assetPath.LastIndexOf('.');
        if (dot <= slash) dot = assetPath.Length;
        return assetPath.Insert(dot, $"_{ordinal:D2}") + ".xmp";
    }
    private static bool ValidSha256(string value) => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record WindowsSyncContentDownload(FileStream Bytes, ContentAddress Address);

public sealed record WindowsSyncCursor(string Head, Guid AssetId, Guid? EditId)
{
    public static string Encode(WindowsSyncCursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{value.Head}\n{value.AssetId:D}\n{value.EditId?.ToString("D") ?? "-"}"));
    public static WindowsSyncCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
            if (parts.Length != 3 || parts[0].Length != 64) throw new FormatException();
            return new(parts[0], Guid.Parse(parts[1]), parts[2] == "-" ? null : Guid.Parse(parts[2]));
        }
        catch (Exception error) when (error is FormatException or ArgumentException) { throw new VersionControlProblem("cursor_expired"); }
    }
}
