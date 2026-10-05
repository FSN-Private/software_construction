using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed class ExportCleanupService(NpgsqlDataSource source, IAmazonS3 storage, IConfiguration config)
{
    public async Task TickAsync(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await RecoverAsync(db, ct);
        await RecoverDownloadAdmissionsAsync(db, ct);
        var intents = new List<(Guid Id, Guid Request, string Owner, string Library, string Key)>();
        await using (var cmd = Command(db, "SELECT i.id,r.id,r.owner_id,r.library_id,i.object_key FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.category='package' AND i.state='settled' AND (r.state IN ('cancelling','cancelled','expired','unavailable','failed') OR (r.state IN ('completed','partial') AND i.object_key IS DISTINCT FROM r.package_key))"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) intents.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4))); }
        var bucket = config["Rendering:DerivativeBucket"] ?? throw new ExportProblem("exports_unavailable", 503);
        foreach (var intent in intents)
        {
            // Settlement proves the writer cannot create a later version. Unknown writers stay draining.
            foreach (var version in await ListVersionsAsync(bucket, intent.Key, ct))
                await storage.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = intent.Key, VersionId = version }, ct);
            foreach (var upload in await ListUploadsAsync(bucket, intent.Key, ct))
                await storage.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = intent.Key, UploadId = upload.UploadId }, ct);
            if ((await ListVersionsAsync(bucket, intent.Key, ct)).Count != 0 || (await ListUploadsAsync(bucket, intent.Key, ct)).Count != 0) continue;
            await using var tx = await db.BeginTransactionAsync(ct);
            await ExportDatabase.LockAsync(db, intent.Owner, intent.Library, ct);
            if (await ExecuteAsync(db, "UPDATE export_write_intents SET state='cleaned',settled_at=COALESCE(settled_at,now()) WHERE id=$1 AND state='settled' AND writer_stopped AND NOT storage_pending", ct, intent.Id) != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, intent.Owner, intent.Library, "exports.writer_cleaned", "export_write_intent", intent.Id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, intent.Request, ct), ct);
            await tx.CommitAsync(ct);
        }
        await CompleteCancellationsAsync(db, ct);
    }
    private async Task RecoverAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var candidates = new List<(Guid Id, Guid Request, string Category, string? Path, string Host, int Pid, long Start, bool Stopped, string Owner, string Library)>();
        // Only a successful completion receipt with its exact version proves the upload terminal.
        await using (var cmd = Command(db, "SELECT i.id,r.id,i.category,i.workspace_path,i.writer_host,i.writer_pid,i.writer_start_ticks,i.writer_stopped,r.owner_id,r.library_id FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.state IN ('admitted','unknown') AND (i.category='workspace' OR (NOT i.storage_pending AND i.provider_receipt IS NOT NULL AND i.multipart_id IS NOT NULL AND i.object_version IS NOT NULL AND i.storage_phase='complete'))"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetBoolean(7), r.GetString(8), r.GetString(9)));
        foreach (var value in candidates)
        {
            // Process identity proves only writer quiescence; it never resolves an ambiguous provider operation.
            if (!value.Stopped && !LocalProcessExited(value.Host, value.Pid, value.Start)) continue;
            if (value.Category == "workspace")
            {
                if (value.Host != Environment.MachineName) continue;
                var root = Path.GetFullPath(config["Exports:WorkspaceRoot"] ?? Path.Combine(Path.GetTempPath(), "platform-exports"));
                var expected = Path.Combine(root, value.Id.ToString("N"));
                if (!string.Equals(Path.GetFullPath(value.Path!), expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ExportProblem("integrity_error");
                RemoveWorkspace(root, value.Id);
            }
            await using var tx = await db.BeginTransactionAsync(ct);
            await ExportDatabase.LockAsync(db, value.Owner, value.Library, ct);
            if (await ExecuteAsync(db, "UPDATE export_write_intents SET state=$2,writer_stopped=true,settled_at=now() WHERE id=$1 AND state IN ('admitted','unknown') AND (category='workspace' OR (NOT storage_pending AND provider_receipt IS NOT NULL AND multipart_id IS NOT NULL AND object_version IS NOT NULL AND storage_phase='complete'))", ct, value.Id, value.Category == "workspace" ? "cleaned" : "settled") != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, value.Owner, value.Library, "exports.writer_recovered", "export_write_intent", value.Id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, value.Request, ct), ct, outcome: value.Category == "workspace" ? "cleaned" : "settled");
            await tx.CommitAsync(ct);
        }
    }

    private async Task RecoverDownloadAdmissionsAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var candidates = new List<(Guid Id, Guid Request, string Owner, string Library, string Host, int Pid, long Start)>();
        await using (var cmd = Command(db, "SELECT id,request_id,owner_id,library_id,process_host,process_pid,process_start_ticks FROM export_download_admissions WHERE closed_at IS NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6)));
        foreach (var item in candidates)
        {
            if (!LocalProcessExited(item.Host, item.Pid, item.Start)) continue;
            await using var tx = await db.BeginTransactionAsync(ct);
            await ExportDatabase.LockAsync(db, item.Owner, item.Library, ct);
            if (await ExecuteAsync(db, "UPDATE export_download_admissions SET closed_at=now(),close_reason='process_exited' WHERE id=$1 AND closed_at IS NULL", ct, item.Id) != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, item.Owner, item.Library, "exports.download_process_exited", "export_download_admission", item.Id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, item.Request, ct), ct, outcome: "reconciled");
            await tx.CommitAsync(ct);
        }
    }

    private static async Task CompleteCancellationsAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var requests = new List<(Guid Id, string Owner, string Library)>();
        await using (var cmd = Command(db, "SELECT r.id,r.owner_id,r.library_id FROM export_requests r WHERE r.state='cancelling' AND NOT EXISTS(SELECT 1 FROM export_write_intents i WHERE i.request_id=r.id AND i.state<>'cleaned')"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) requests.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
        foreach (var request in requests)
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await ExportDatabase.LockAsync(db, request.Owner, request.Library, ct);
            if (await ExecuteAsync(db, "UPDATE export_requests r SET state='cancelled',lease_id=NULL,lease_until=NULL WHERE r.id=$1 AND r.state='cancelling' AND NOT EXISTS(SELECT 1 FROM export_write_intents i WHERE i.request_id=r.id AND i.state<>'cleaned')", ct, request.Id) != 0)
                await ExportDatabase.RecordChangeAsync(db, tx, request.Owner, request.Library, "exports.cancelled", "export_request", request.Id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, request.Id, ct), ct, outcome: "settled");
            await tx.CommitAsync(ct);
        }
    }

    private async Task<List<string>> ListVersionsAsync(string bucket, string key, CancellationToken ct)
    {
        var result = new List<string>(); string? keyMarker = null; string? versionMarker = null;
        do
        {
            var page = await storage.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key, KeyMarker = keyMarker, VersionIdMarker = versionMarker }, ct);
            // S3's versions collection includes ordinary versions and delete markers.
            result.AddRange((page.Versions ?? []).Where(value => value.Key == key).Select(value => value.VersionId));
            if (page.IsTruncated != true) break;
            if (page.NextKeyMarker is null) throw new ExportProblem("cleanup_inventory_incomplete", 503);
            keyMarker = page.NextKeyMarker; versionMarker = page.NextVersionIdMarker;
        } while (true);
        return result;
    }

    private async Task<List<(string Key, string UploadId)>> ListUploadsAsync(string bucket, string key, CancellationToken ct)
    {
        var result = new List<(string Key, string UploadId)>(); string? keyMarker = null; string? uploadMarker = null;
        do
        {
            var page = await storage.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key, KeyMarker = keyMarker, UploadIdMarker = uploadMarker }, ct);
            result.AddRange((page.MultipartUploads ?? []).Where(value => value.Key == key).Select(value => (value.Key, value.UploadId)));
            if (page.IsTruncated != true) break;
            if (page.NextKeyMarker is null) throw new ExportProblem("cleanup_inventory_incomplete", 503);
            keyMarker = page.NextKeyMarker; uploadMarker = page.NextUploadIdMarker;
        } while (true);
        return result;
    }
    internal static void RemoveWorkspace(string root, Guid id)
    {
        var path = Path.Combine(root, id.ToString("N"));
        // Enumerating the accessible parent preserves permission/volume errors; Directory.Exists would hide them.
        if (Directory.GetFileSystemEntries(root, id.ToString("N")).Length != 0)
        {
            try { Directory.Delete(path, true); }
            catch (DirectoryNotFoundException) { /* Another cleanup may have removed the child; verify through its parent below. */ }
        }
        if (Directory.GetFileSystemEntries(root, id.ToString("N")).Length != 0) throw new IOException("Export workspace removal is incomplete.");
    }
    private static bool LocalProcessExited(string host, int pid, long start)
    {
        if (host != Environment.MachineName) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != start;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
