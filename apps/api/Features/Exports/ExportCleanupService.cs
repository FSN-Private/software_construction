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
        var intents = new List<(Guid Id, string Key)>();
        await using (var cmd = Command(db, "SELECT i.id,i.object_key FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.category='package' AND i.state='settled' AND (r.state IN ('cancelling','cancelled','expired','unavailable','failed') OR (r.state IN ('completed','partial') AND i.object_key IS DISTINCT FROM r.package_key))"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) intents.Add((r.GetGuid(0), r.GetString(1))); }
        var bucket = config["Rendering:DerivativeBucket"] ?? throw new ExportProblem("exports_unavailable", 503);
        foreach (var intent in intents)
        {
            // Settlement proves the writer cannot create a later version. Unknown writers stay draining.
            string? keyMarker = null; string? versionMarker = null;
            do
            {
                var versions = await storage.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = intent.Key, KeyMarker = keyMarker, VersionIdMarker = versionMarker }, ct);
                foreach (var version in (versions.Versions ?? []).Where(v => v.Key == intent.Key))
                    await storage.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = intent.Key, VersionId = version.VersionId }, ct);
                keyMarker = versions.IsTruncated == true ? versions.NextKeyMarker : null; versionMarker = versions.NextVersionIdMarker;
            } while (keyMarker is not null);
            string? uploadMarker = null; keyMarker = null;
            do
            {
                var uploads = await storage.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = intent.Key, KeyMarker = keyMarker, UploadIdMarker = uploadMarker }, ct);
                foreach (var upload in (uploads.MultipartUploads ?? []).Where(u => u.Key == intent.Key))
                    await storage.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = intent.Key, UploadId = upload.UploadId }, ct);
                keyMarker = uploads.IsTruncated == true ? uploads.NextKeyMarker : null; uploadMarker = uploads.NextUploadIdMarker;
            } while (keyMarker is not null);
            var remaining = await storage.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = intent.Key }, ct);
            var multipart = await storage.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = intent.Key }, ct);
            if ((remaining.Versions ?? []).Any(v => v.Key == intent.Key) || (multipart.MultipartUploads ?? []).Any(u => u.Key == intent.Key)) continue;
            await ExecuteAsync(db, "UPDATE export_write_intents SET state='cleaned' WHERE id=$1 AND state='settled'", ct, intent.Id);
        }
        await ExecuteAsync(db, "UPDATE export_requests r SET state='cancelled',lease_id=NULL,lease_until=NULL WHERE state='cancelling' AND NOT EXISTS(SELECT 1 FROM export_write_intents i WHERE i.request_id=r.id AND i.state<>'cleaned')", ct);
    }
    private async Task RecoverAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var candidates = new List<(Guid Id, string Category, string? Path, string Host, int Pid, long Start, bool Stopped, string Owner)>();
        // Only a successful completion receipt with its exact version proves the upload terminal.
        await using (var cmd = Command(db, "SELECT i.id,i.category,i.workspace_path,i.writer_host,i.writer_pid,i.writer_start_ticks,i.writer_stopped,r.owner_id FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.state IN ('admitted','unknown') AND (i.category='workspace' OR (NOT i.storage_pending AND i.provider_receipt IS NOT NULL AND i.multipart_id IS NOT NULL AND i.object_version IS NOT NULL AND i.storage_phase='complete'))"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.GetBoolean(6), r.GetString(7)));
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
            if (await ExecuteAsync(db, "UPDATE export_write_intents SET state=$2,writer_stopped=true,settled_at=now() WHERE id=$1 AND state IN ('admitted','unknown') AND (category='workspace' OR (NOT storage_pending AND provider_receipt IS NOT NULL AND multipart_id IS NOT NULL AND object_version IS NOT NULL AND storage_phase='complete'))", ct, value.Id, value.Category == "workspace" ? "cleaned" : "settled") != 0)
                await AuditAsync(db, value.Owner, ExportDatabase.OperationId, "exports.writer_recovered", value.Id, "settled", ct);
            await tx.CommitAsync(ct);
        }
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
