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
        var intents = new List<(Guid Id, string Key)>();
        await using (var cmd = Command(db, "SELECT i.id,i.object_key FROM export_write_intents i JOIN export_requests r ON r.id=i.request_id WHERE i.state='settled' AND (r.state IN ('cancelling','cancelled','expired','unavailable','failed') OR (r.state IN ('completed','partial') AND i.object_key IS DISTINCT FROM r.package_key))"))
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
}
