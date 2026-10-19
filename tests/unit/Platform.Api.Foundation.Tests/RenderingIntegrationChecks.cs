using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Platform.Api.Features.Rendering;
using Platform.Contracts.Rendering;
using static Platform.Storage.IngestDb;

internal static class RenderingIntegrationChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, IAmazonS3 s3, string bucket)
    {
        const string owner = "test-owner";
        var library = "rendering-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb";
        var asset = Guid.NewGuid();
        var edit = Guid.NewGuid();
        string sourceSha;
        string xmpSha;
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var source = Command(connection, "SELECT a.original_sha256,e.xmp_sha256 FROM assets a JOIN edit_identities e ON e.asset_id=a.id JOIN content_objects original ON original.sha256=a.original_sha256 AND original.integrity_state='authoritative' JOIN content_objects xmp ON xmp.sha256=e.xmp_sha256 AND xmp.integrity_state='authoritative' WHERE a.owner_id=$1 AND a.original_sha256 IS NOT NULL AND e.xmp_sha256 IS NOT NULL LIMIT 1", owner);
            await using var reader = await source.ExecuteReaderAsync();
            IngestChecks.Check(await reader.ReadAsync(), "Rendering integration requires authoritative immutable source and XMP content.");
            sourceSha = reader.GetString(0).Trim();
            xmpSha = reader.GetString(1).Trim();
            await reader.DisposeAsync();
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,original_sha256,state,format) VALUES($1,$2,$3,'active','dng'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($4,$1,0,$5,'rendering-integration'); INSERT INTO vc_working_changes(id,owner_id,library_id,client_id,branch_name,asset_id,edit_id,current_content_sha256,change_kind) VALUES($6,$2,$7,'rendering-client','main',$1,$4,$5,'modified'); UPDATE rendering_compatibility SET renderer_artifact_digest=$8,corpus_evidence_id='ecr-corpus-20260924',approved_at=now(),approved_by='Fredrik Snårbacka',state='approved' WHERE matrix_version='rendering-v1'", default, asset, owner, sourceSha, edit, xmpSha, Guid.NewGuid(), library, digest);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Rendering:CompatibilityMatrix"] = "rendering-v1", ["Rendering:DeployedArtifactDigest"] = digest, ["Rendering:PromotionEvidenceId"] = "trusted-linux-integration", ["Rendering:OperationsUsername"] = "release-operator" }).Build();
        var operations = new RenderOperationsService(db, configuration);
        var admitted = await operations.SetAsync("admit", owner, "release-operator", default);
        IngestChecks.Check(admitted.Paused && await operations.IsAcceptanceOwnerAsync(owner, default) && !await operations.IsAcceptanceOwnerAsync("other-owner", default), "Candidate admission must pause general intake and scope acceptance to one owner.");
        await operations.SetAsync("activate", owner, "release-operator", default);
        IngestChecks.Check(!(await operations.SetAsync("activate", owner, "release-operator", default)).Paused, "A retry after an ambiguous activate response must be idempotent for the exact activated candidate.");
        var queue = new RecordingQueue();
        var service = new RenderingService(db, new CurrentOnlyResolver(), configuration, queue, new FixedLocator());
        var input = new RenditionRequestInput(asset, edit, "current", RenditionKind.Thumbnail512, "render-v1", library);
        var created = await service.RequestAsync(owner, input, default);
        var duplicate = await service.RequestAsync(owner, input, default);
        IngestChecks.Check(created.State == RenderState.Pending && created.AttemptId is not null, "A current preview must create an observable pending attempt.");
        IngestChecks.Check(created.RequestId == duplicate.RequestId && created.AttemptId == duplicate.AttemptId && !duplicate.CacheHit, "PostgreSQL must coalesce duplicate pending current-preview requests.");
        IngestChecks.Check(queue.Messages.Count == 1 && queue.Messages[0].RequestId == created.RequestId && queue.Messages[0].AttemptId == created.AttemptId && queue.Messages[0].DerivativeKey == $"derivatives/{queue.Messages[0].Request.Key}.jpg", "A new render attempt must dispatch one immutable queue envelope.");
        IngestChecks.Check(queue.Messages[0].SourceFormat == "dng", "Queue dispatch must carry the existing verified asset format, never a user filename.");

        var objectKey = $"private/{owner}/{library}/renditions/{created.RequestId:N}.jpg";
        var output = "integration-render-output"u8.ToArray();
        await using (var stream = new MemoryStream(output, writable: false))
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = objectKey, InputStream = stream, ContentType = "image/jpeg" });
        var metadata = await s3.GetObjectMetadataAsync(bucket, objectKey);
        var provenance = new RenderProvenance(sourceSha, xmpSha, "current", "5.6.1", digest, "render-v1", "rendering-v1", DateTimeOffset.UtcNow);
        await new RenderWorkerCallbacks(db).CompleteAsync(created.RequestId, created.AttemptId!.Value, objectKey, metadata.VersionId!, IngestChecks.Hash(output), output.LongLength, "image/jpeg", provenance, default);

        var completed = await service.StatusAsync(owner, library, created.RequestId, default);
        var cacheHit = await service.RequestAsync(owner, input, default);
        IngestChecks.Check(completed.State == RenderState.Completed && Matches(provenance, completed.Provenance), "A completed private derivative must expose exact current-preview provenance.");
        var locator = await service.LocateAsync(owner, library, created.RequestId, default);
        IngestChecks.Check(locator.Locator == "https://derivative.example/private" && Matches(provenance, locator.Provenance), "A completed rendition must return only an owner-authorized private locator and provenance.");
        await RejectCrossLibraryLocatorAsync(service, created.RequestId);
        await RejectSameOwnerCrossLibraryRenderAsync(service, owner, input);
        IngestChecks.Check(cacheHit.CacheHit && Matches(provenance, cacheHit.Provenance), "An exact completed current-preview request must reuse the cached derivative and provenance.");
        var failed = await service.RequestAsync(owner, input with { Rendition = RenditionKind.Preview2048 }, default);
        await new RenderWorkerCallbacks(db).FailAsync(failed.RequestId, failed.AttemptId!.Value, "malformed_xmp", default);
        var failure = await service.StatusAsync(owner, library, failed.RequestId, default);
        IngestChecks.Check(failure.State == RenderState.Failed && failure.FailureCode == "malformed_xmp", "A renderer failure must retain only its safe category.");
        var retry = await service.RetryAsync(owner, library, failed.RequestId, default);
        IngestChecks.Check(retry.State == RenderState.Pending && retry.AttemptId != failed.AttemptId, "A corrected render retry must create a distinct pending attempt.");
        IngestChecks.Check(queue.Messages.Count == 3 && queue.Messages[2].AttemptId == retry.AttemptId, "A retry must dispatch its distinct immutable render attempt.");
        IngestChecks.Check(queue.Messages.All(message => message.SourceFormat == "dng"), "Initial, redispatched and retried rendering must preserve the verified asset format.");
        await new RenderWorkerCallbacks(db).FailAsync(retry.RequestId, retry.AttemptId!.Value, "resource_limit", default);
        IngestChecks.Check((await service.StatusAsync(owner, library, retry.RequestId, default)).FailureCode == "resource_limit", "A resource-limited attempt must remain visibly retryable without exposing worker details.");
        await MissingSourceIsRejectedAsync(service, owner, library, edit);
        await using var verification = await db.OpenConnectionAsync();
        IngestChecks.Check(await ScalarAsync(verification, "SELECT original_sha256=$2 FROM assets WHERE id=$1", default, asset, sourceSha) is true && await ScalarAsync(verification, "SELECT xmp_sha256=$2 FROM edit_identities WHERE id=$1", default, edit, xmpSha) is true, "Rendering integration must not mutate source or XMP identities.");
    }

    private sealed class CurrentOnlyResolver : ISelectedVersionResolver
    {
        public Task<SelectedVersionResolution> ResolveAsync(string ownerId, string libraryId, Guid assetId, Guid editId, string selectedVersion, CancellationToken cancellationToken = default) => Task.FromResult(SelectedVersionResolution.Unavailable("selected_version_unavailable"));
    }

    private sealed class RecordingQueue : IRenderQueueDispatcher
    {
        public List<RenderQueueMessage> Messages { get; } = [];
        public Task EnqueueAsync(RenderQueueMessage message, CancellationToken cancellationToken = default) { Messages.Add(message); return Task.CompletedTask; }
    }

    private sealed class FixedLocator : IPrivateDerivativeLocator
    {
        public string Locate(RenderPublication publication) => "https://derivative.example/private";
    }

    private static bool Matches(RenderProvenance expected, RenderProvenance? actual) => actual is not null
        && actual.SourceSha256 == expected.SourceSha256
        && actual.XmpSha256 == expected.XmpSha256
        && actual.SelectedVersion == expected.SelectedVersion
        && actual.RendererRelease == expected.RendererRelease
        && actual.RendererArtifactDigest == expected.RendererArtifactDigest
        && actual.Profile == expected.Profile
        && actual.CompatibilityMatrix == expected.CompatibilityMatrix;

    private static async Task MissingSourceIsRejectedAsync(RenderingService service, string owner, string library, Guid edit)
    {
        try { await service.RequestAsync(owner, new RenditionRequestInput(Guid.NewGuid(), edit, "current", RenditionKind.Thumbnail512, "render-v1", library), default); }
        catch (RenderingProblem error) when (error.Code == "source_unavailable" && error.Status == 404) { return; }
        throw new InvalidOperationException("Missing render sources must be rejected without a substitute rendition.");
    }

    private static async Task RejectCrossLibraryLocatorAsync(RenderingService service, Guid requestId)
    {
        try { await service.LocateAsync("other-owner", "other-library", requestId, default); }
        catch (RenderingProblem error) when (error.Code == "rendition_not_found" && error.Status == 404) { return; }
        throw new InvalidOperationException("A derivative locator must refuse cross-library access.");
    }

    private static async Task RejectSameOwnerCrossLibraryRenderAsync(RenderingService service, string owner, RenditionRequestInput input)
    {
        try { await service.RequestAsync(owner, input with { LibraryId = input.LibraryId + "-other" }, default); }
        catch (RenderingProblem error) when (error.Code == "source_unavailable" && error.Status == 404) { return; }
        throw new InvalidOperationException("A same-owner asset must not be rendered through another library identity.");
    }
}
