using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Contracts.Exports;
using Platform.Contracts.Rendering;
using Platform.Api.Features.Rendering;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;
public sealed class ExportPackageService(NpgsqlDataSource source, IAmazonS3 storage, IConfiguration configuration, RenderingService rendering, ExportSelectionService selections, ExportInvalidationService writes)
{
    public async Task<bool> PrepareAsync(string owner, string library, Guid id, Guid lease, long generation, CancellationToken ct)
    {
        var request = await new ExportPreparationService(source, new ExportBasketService(source, selections), rendering).GetAsync(owner, library, id, ct);
        var preset = ExportPreset.Get(request.Snapshot.PresetId);
        var results = request.Outcomes.ToDictionary(r => r.ItemId);
        var publications = new Dictionary<Guid, (RenderInputObject Input, RenderProvenance Provenance)>();
        if (preset.Format != "original")
        {
            foreach (var pin in request.Snapshot.Items.Where(pin => results[pin.ItemId].Status == "pending"))
            {
                try
                {
                    var status = await rendering.RequestPinnedAsync(owner, library, pin, request.Snapshot.Renderer ?? throw new ExportProblem("incompatible_renderer"), ct);
                    if (status.State == RenderState.Retryable)
                    {
                        await using var retryDb = await source.OpenConnectionAsync(ct);
                        if (Convert.ToInt32(await ScalarAsync(retryDb, "SELECT count(*) FROM render_attempts WHERE request_id=$1", ct, status.RequestId)) < 3) { await rendering.RetryAsync(owner, library, status.RequestId, ct); return false; }
                        results[pin.ItemId] = Failed(pin, "failed", "renderer_timeout"); continue;
                    }
                    if (status.State is RenderState.Pending or RenderState.Running) return false;
                    if (status.State != RenderState.Completed || status.Provenance is null) { results[pin.ItemId] = Failed(pin, "failed", SafeReason(status.FailureCode)); continue; }
                    await using var db = await source.OpenConnectionAsync(ct);
                    await using var cmd = Command(db, "SELECT d.object_key,d.object_version,d.output_sha256,d.byte_length FROM renditions d JOIN render_requests r ON r.id=d.request_id WHERE r.id=$1 AND r.owner_id=$2 AND r.library_id=$3 AND r.state='completed'", status.RequestId, owner, library);
                    await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new ExportProblem("output_invalid");
                    publications.Add(pin.ItemId, (new(r.GetString(0), r.GetString(1), r.GetString(2).Trim(), r.GetInt64(3)), status.Provenance));
                }
                catch (RenderingProblem error) { if (error.Code == "rendering_paused") return false; results[pin.ItemId] = Failed(pin, "failed", SafeReason(error.Code)); }
                catch (ExportProblem error) { results[pin.ItemId] = Failed(pin, "unavailable", SafeReason(error.Code)); }
            }
        }
        var root = configuration["Exports:WorkspaceRoot"] ?? Path.Combine(Path.GetTempPath(), "platform-exports");
        Directory.CreateDirectory(root); var workspace = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        var zipPath = Path.Combine(workspace, "package.zip"); var max = ExportConfiguration.Limits(configuration).PackageBytes;
        if (max < 1 || max > new ExportLimits().PackageBytes) throw new ExportProblem("invalid_export_configuration", 503);
        try
        {
            long copied = 0;
            await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var pin in request.Snapshot.Items)
                {
                    if (results[pin.ItemId].Status != "pending") continue;
                    try
                    {
                        await CheckLeaseAsync(owner, library, id, lease, generation, ct);
                        var reason = await selections.AvailabilityAsync(owner, library, pin, preset.Format == "original", ct); if (reason is not null) throw new ExportProblem(reason);
                        var input = preset.Format == "original" ? pin.Original : publications[pin.ItemId].Input;
                        if (input.ByteLength > max - copied) throw new ExportProblem("resource_limit");
                        var file = Path.Combine(workspace, "input");
                        await DownloadVerifiedAsync(preset.Format == "original" ? Required("Ingest:Bucket") : Required("Rendering:DerivativeBucket"), input, file, max - copied, ct);
                        ExportTransform? transform = null;
                        if (preset.Format != "original")
                        {
                            if (input.ByteLength > 64 * 1024 * 1024) throw new ExportProblem("resource_limit");
                            var transformed = JpegMetadata.Transform(await File.ReadAllBytesAsync(file, ct), preset.Metadata == "retain-v1");
                            await File.WriteAllBytesAsync(file, transformed, ct); transform = new(preset.Metadata, input.Sha256, ExportIntegrity.Hash(transformed));
                        }
                        await using var bytes = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                        var sha = Convert.ToHexString(await SHA256.HashDataAsync(bytes, ct)).ToLowerInvariant(); bytes.Position = 0;
                        var filename = ExportNames.For(pin, preset); var entry = zip.CreateEntry(filename, CompressionLevel.NoCompression);
                        await using (var output = entry.Open()) await bytes.CopyToAsync(output, ct);
                        copied += bytes.Length;
                        results[pin.ItemId] = new(pin.ItemId, "completed", null, filename, bytes.Length, sha, preset.Format == "original" ? null : publications[pin.ItemId].Provenance, transform);
                    }
                    catch (ExportProblem error) { results[pin.ItemId] = Failed(pin, error.Code is "source_unavailable" or "selected_version_unavailable" or "permanently_unavailable" ? "unavailable" : "failed", SafeReason(error.Code)); }
                    catch (AmazonS3Exception) { results[pin.ItemId] = Failed(pin, "failed", "storage_unavailable"); }
                }
                var manifest = new ExportManifest(1, request.Snapshot, request.Snapshot.Items.Select(pin => results[pin.ItemId]).ToArray()); manifest.Validate();
                await using (var output = zip.CreateEntry("manifest.json").Open()) await JsonSerializer.SerializeAsync(output, ExportPublic.Manifest(manifest), ExportDatabase.Json, ct);
                await using (var output = zip.CreateEntry("failures.json").Open()) await JsonSerializer.SerializeAsync(output, manifest.Outcomes.Where(o => o.Status != "completed").ToArray(), ExportDatabase.Json, ct);
            }
            var finalManifest = new ExportManifest(1, request.Snapshot, request.Snapshot.Items.Select(pin => results[pin.ItemId]).ToArray()); finalManifest.Validate();
            await using var package = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (package.Length > max) throw new ExportProblem("resource_limit");
            var completed = finalManifest.Outcomes.Count(o => o.Status == "completed");
            if (completed == 0) { await PublishAsync(owner, library, id, lease, generation, finalManifest, null, null, null, null, "failed", ct); return true; }
            var checksum = Convert.ToHexString(await SHA256.HashDataAsync(package, ct)).ToLowerInvariant(); package.Position = 0;
            var intent = await writes.AdmitWriteAsync(owner, library, id, generation, lease, ct);
            var key = $"exports/{id:N}/{intent:N}/package.zip"; string? version = null;
            try
            {
                // Stream multipart uploads to support packages exceeding S3's single-PUT limit.
                version = await UploadAsync(intent, lease, key, package, ct);
                await writes.SettleWriteAsync(intent, lease, version, true, CancellationToken.None);
            }
            catch
            {
                await writes.SettleWriteAsync(intent, lease, version, false, CancellationToken.None);
                throw;
            }
            await PublishAsync(owner, library, id, lease, generation, finalManifest, key, version, checksum, package.Length, completed == results.Count ? "completed" : "partial", ct);
            return true;
        }
        finally
        {
            // This path is generated solely beneath the configured workspace root.
            Directory.Delete(workspace, true);
        }
    }
    private async Task<string> UploadAsync(Guid intent, Guid writer, string key, Stream bytes, CancellationToken ct)
    {
        var bucket = Required("Rendering:DerivativeBucket");
        var upload = await storage.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ContentType = "application/zip", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = Required("Ingest:KmsKeyId") }, ct);
        await using (var db = await source.OpenConnectionAsync(ct)) await ExecuteAsync(db, "UPDATE export_write_intents SET multipart_id=$3 WHERE id=$1 AND writer_id=$2", ct, intent, writer, upload.UploadId);
        var parts = new List<PartETag>(); var buffer = new byte[8 * 1024 * 1024]; var number = 1;
        while (true)
        {
            var count = 0; while (count < buffer.Length) { var read = await bytes.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) break; count += read; }
            if (count == 0) break;
            using var part = new MemoryStream(buffer, 0, count, writable: false);
            var result = await storage.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = upload.UploadId, PartNumber = number, InputStream = part, PartSize = count }, ct);
            parts.Add(new PartETag(number++, result.ETag));
        }
        var complete = await storage.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = parts }, ct);
        return complete.VersionId ?? throw new ExportProblem("storage_unavailable");
    }
    private async Task PublishAsync(string owner, string library, Guid id, Guid lease, long generation, ExportManifest manifest, string? key, string? version, string? sha, long? length, string state, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        var availability = await ExportSelectionService.AvailabilityBatchAsync(db, owner, library, manifest.Snapshot.Items, manifest.Snapshot.PresetId == "originals-v1", ct);
        foreach (var pin in manifest.Snapshot.Items)
        {
            var reason = availability[pin.ItemId];
            if (reason == "permanently_unavailable" || reason is not null && manifest.Outcomes.Any(o => o.ItemId == pin.ItemId && o.Status == "completed")) throw new ExportProblem(reason!);
        }
        if (await ExecuteAsync(db, "UPDATE export_requests SET state=$6,manifest=$7::jsonb,package_key=$8,package_version=$9,package_sha256=$10,package_bytes=$11,completed_at=now(),expires_at=now()+interval '24 hours',lease_id=NULL,lease_until=NULL WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND lease_id=$4 AND generation=$5 AND state='preparing' AND lease_until>now()", ct, id, owner, library, lease, generation, state, ExportDatabase.Encode(manifest), key, version, sha, length) == 0) throw new ExportProblem("cancelled");
        foreach (var item in manifest.Outcomes)
        {
            await ExecuteAsync(db, "UPDATE export_results SET outcome=$3::jsonb WHERE request_id=$1 AND item_id=$2", ct, id, item.ItemId, ExportDatabase.Encode(item));
            await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.item_settled", item.ItemId, item.Status, ct, item.Reason);
        }
        await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.package_prepared", id, state, ct); await tx.CommitAsync(ct);
    }
    private async Task CheckLeaseAsync(string owner, string library, Guid id, Guid lease, long generation, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        if (await ExecuteAsync(db, "UPDATE export_requests SET lease_until=now()+interval '2 minutes' WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND lease_id=$4 AND generation=$5 AND state='preparing' AND lease_until>now()", ct, id, owner, library, lease, generation) == 0) throw new ExportProblem("cancelled");
    }
    private async Task DownloadVerifiedAsync(string bucket, RenderInputObject input, string file, long limit, CancellationToken ct)
    {
        using var result = await storage.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = input.Key, VersionId = input.Version }, ct);
        if (result.ContentLength != input.ByteLength || result.ContentLength > limit) throw new ExportProblem("integrity_error");
        await using var output = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous); var buffer = new byte[81920]; long count = 0;
        while (true) { var read = await result.ResponseStream.ReadAsync(buffer, ct); if (read == 0) break; count += read; if (count > input.ByteLength) throw new ExportProblem("integrity_error"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        output.Position = 0; var sha = Convert.ToHexString(await SHA256.HashDataAsync(output, ct)).ToLowerInvariant(); if (count != input.ByteLength || sha != input.Sha256) throw new ExportProblem("integrity_error");
    }
    private string Required(string name) => configuration[name] ?? throw new ExportProblem("exports_unavailable", 503);
    private static string SafeReason(string? code) => ExportStates.Reasons.Contains(code ?? "") ? code! : "output_invalid";
    private static ExportOutcome Failed(ExportPin pin, string status, string code) => new(pin.ItemId, status, code, null, null, null);
}
