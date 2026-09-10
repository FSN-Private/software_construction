using Amazon.S3;
using Amazon.S3.Model;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Npgsql;
using Platform.Storage;
using Platform.Ingest.Worker;
using static Platform.Storage.IngestDb;
using static IngestChecks;

internal static class ReferenceBatchChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage, IAmazonS3 s3, string bucket)
    {
        if (Environment.GetEnvironmentVariable("INGEST_REFERENCE") != "1") return;
        var bytes = File.ReadAllBytes("tests/fixtures/ingest/sample.png");
        var imports = new ImportService(db, storage);
        var batch = await imports.CreateAsync("reference-owner", Guid.NewGuid().ToString("N"), "op_reference", new(true, Enumerable.Range(0, 1000).Select(i => new ImportItem($"reference/{i}.png", Hash(bytes), bytes.Length)).ToArray()), default);
        var versions = new System.Collections.Concurrent.ConcurrentBag<object>();
        // Transfer time is excluded from the reference verification target; the protocol is exercised separately.
        await Parallel.ForEachAsync(batch.Items, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (item, ct) =>
        {
            var result = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = storage.StagingKey(item.Id), InputStream = new MemoryStream(bytes), ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = "ingest-test-key" }, ct);
            versions.Add(new { id = item.Id, version = result.VersionId });
        });
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE upload_sessions s SET state='verifying',staging_version=v.version FROM jsonb_to_recordset($1::jsonb) v(id uuid,version text) WHERE s.id=v.id; INSERT INTO ingest_publications(batch_id) VALUES($2); UPDATE import_batches SET state='processing' WHERE id=$2", default, JsonSerializer.Serialize(versions), batch.Id);
        var elapsed = new List<double>(); var timer = new Stopwatch();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) => { if (instrument.Name == "ingest.verification.duration") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => elapsed.Add(timer.Elapsed.TotalSeconds));
        listener.Start(); timer.Start();
        var processor = new BatchProcessor(db, storage);
        while (await processor.ProcessNextAsync(default)) { }
        var outcome = await imports.StatusAsync("reference-owner", batch.Id, default);
        Check(outcome.Items.Count == 1000 && outcome.Items.All(i => i.State == "active"), "1000-item reference batch has no silent failures");
        var p95 = elapsed.Order().ElementAt(949);
        var evidence = new { items = 1000, verificationCompletionP95Seconds = p95, totalSeconds = timer.Elapsed.TotalSeconds, storedBytes = outcome.StoredBytes, objectCount = outcome.ObjectCount };
        Directory.CreateDirectory("artifacts");
        await File.WriteAllTextAsync("artifacts/ingest-reference.json", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(evidence));
        Check(p95 < 60, "Reference p95 verification completion must be under 60 seconds after transfer");
    }
}
