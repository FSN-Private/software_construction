using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryPurgeDeletionJournalChecks
{
    internal static async Task RunAsync()
    {
        await CheckSdkAbsenceAsync();
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var asset = Guid.NewGuid();
        var points = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var observations = points.Select((id, index) => new RecoveryRetentionPointObservationV1(id,
            now.AddDays(index == 0 ? -100 : -1), now.AddDays(index == 0 ? -100 : -1).AddMinutes(1),
            hash, hash, "independent-key", "recoverable-deleted", 2, 0, hash, now.AddDays(-100), [])).ToArray();
        var history = RetentionService.VerifyHistory("journal-owner", "main", asset, null, "original", hash,
            "independent-key", points, observations, now);
        var target = new RecoveryPurgeTargetVersionV1("media", points[0], "recovery-payload", "payload/sha256/" + hash,
            "exact-version", hash, 4, history.EligibleAtUtc!.Value, false);
        var plan = new RecoveryPurgePlanV1(3, Guid.NewGuid(), Guid.NewGuid(), hash, hash, "journal-owner", "main", asset,
            null, "original", hash, 1, 1, points.Length, history.CompletePointSetSha256,
            RecoveryPurgeRunner.ComputePointSetSha256([target.PointId]), hash, now.AddMinutes(-1), now.AddMinutes(20), history, [target]);
        var payload = new RecoveryPurgeApprovalV1(1, Guid.NewGuid(), plan.OperationId, plan.ProposalDigest, plan.Digest(),
            plan.OwnerId, plan.LibraryId, asset, null, "original", hash, 1, "independent-mfa", now.AddMinutes(-1), now.AddMinutes(10));
        var approval = new VerifiedRecoveryPurgeApproval(payload, RecoveryEvidenceCodec.SerializeCanonical(payload), hash,
            "owner-key", hash, []);
        var scope = RecoveryFenceScope.MediaScopeId(new(plan.OwnerId, plan.LibraryId, asset), "original", hash);
        foreach (var fault in new[] { "none", "lost_delete", "lost_admission", "lost_result", "delete_denied", "read_denied" })
        {
            using var dynamo = new JournalDynamo { LoseWrite = fault == "lost_admission" ? 1 : fault == "lost_result" ? 2 : 0 };
            using var s3 = new JournalS3(target) { LoseDelete = fault == "lost_delete", DenyDelete = fault == "delete_denied", DenyRead = fault == "read_denied" };
            var journal = new RecoveryPurgeDeletionJournal(dynamo, s3, "fences", scope, plan, hash);
            if (fault is "delete_denied" or "read_denied")
            {
                await RejectAsync(() => journal.DeleteAsync(target, approval, default));
                if (!s3.Present || dynamo.Items.Keys.Any(id => id.EndsWith("#RESULT", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Denied provider operation manufactured deletion evidence");
                continue;
            }
            if (fault is "lost_admission" or "lost_result") await RejectAsync(() => journal.DeleteAsync(target, approval, default));
            await journal.DeleteAsync(target, approval, default);
            if (s3.Present || dynamo.Items.Count != 2 || s3.Deletes != 1)
                throw new InvalidOperationException("Deletion did not reconcile its two immutable receipts");
            var resultBefore = dynamo.Items.Single(item => item.Key.EndsWith("#RESULT", StringComparison.Ordinal)).Value["payloadBytes"].S;
            var missing = await journal.ReadMissingAsync(target, default) ?? throw new InvalidOperationException("Missing exact version has no durable proof");
            await journal.DeleteAsync(target, approval, default);
            if (s3.Deletes != 1 || dynamo.Items.Single(item => item.Key.EndsWith("#RESULT", StringComparison.Ordinal)).Value["payloadBytes"].S != resultBefore)
                throw new InvalidOperationException("Replay renewed absence time or repeated deletion");
            if (missing.AdmissionDigest.Length != 64 || missing.ResultDigest.Length != 64)
                throw new InvalidOperationException("Missing-version proof is not bound to both records");
            var receipt = new PrivateCopyReceiptV1("primary-media", "sha256/" + hash, "source-v1", hash, 4,
                target.Bucket, target.Key, target.VersionId, nameof(RecoveryDataClass.OriginalImage), now);
            var resume = new VerifiedRecoveryPurgeResumeContext(plan.OperationId, plan.Digest(), plan.Generation, [missing]);
            if (!RecoveryPurgePointVerifier.BuildAuthorizedMissingSet(resume, points[1], [receipt])
                    .Contains((points[1], target.Bucket, target.Key, target.VersionId)))
                throw new InvalidOperationException("Deduplicated journal version could not reconcile another signed point's exact receipt");
            try
            {
                _ = RecoveryPurgePointVerifier.BuildAuthorizedMissingSet(resume, points[1], [receipt with { Sha256 = new string('b', 64) }]);
                throw new InvalidOperationException("A changed signed receipt matched a journalled deletion");
            }
            catch (RecoveryWorkerException error) when (error.SafeCode == "purge_resume_target_binding_invalid") { }
            s3.Present = true;
            await RejectAsync(async () => { _ = await journal.ReadMissingAsync(target, default); });
            s3.Present = false;
            var item = dynamo.Items.Single(pair => pair.Key.EndsWith("#ADMISSION", StringComparison.Ordinal)).Value;
            item["payloadSha256"] = new() { S = new string('b', 64) };
            await RejectAsync(async () => { _ = await journal.ReadMissingAsync(target, default); });
        }
        using var cleanDynamo = new JournalDynamo();
        using var missingS3 = new JournalS3(target) { Present = false };
        var clean = new RecoveryPurgeDeletionJournal(cleanDynamo, missingS3, "fences", scope, plan, hash);
        await RejectAsync(() => clean.DeleteAsync(target, approval, default));
        if (cleanDynamo.Items.Count != 0) throw new InvalidOperationException("Unadmitted absence manufactured a journal");
        missingS3.Present = true;
        await RejectAsync(() => clean.DeleteAsync(target with { VersionId = "foreign-version" }, approval, default));
        await RejectAsync(() => clean.DeleteAsync(target with { RetainedForSharedIdentity = true }, approval, default));
        await RejectAsync(() => clean.DeleteAsync(target, approval with { Payload = payload with { ApprovedAtUtc = now.AddMinutes(-11), ExpiresAtUtc = now.AddMinutes(-1) } }, default));
        await RejectAsync(() => clean.DeleteAsync(target, approval with { Payload = payload with { OperationId = Guid.NewGuid() } }, default));
        if (cleanDynamo.Items.Count != 0 || missingS3.Deletes != 0) throw new InvalidOperationException("Invalid target or approval caused a side effect");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is RecoveryWorkerException or AmazonS3Exception or IOException) { return; }
        throw new InvalidOperationException("Unsafe or unknown purge operation succeeded");
    }

    private static async Task CheckSdkAbsenceAsync()
    {
        // Exercise actual AWS request marshalling and XML/error unmarshalling,
        // rather than overriding the SDK operation with a manufactured exception.
        foreach (var test in new[] { "present", "empty", "absent", "denied", "generic404", "wrong_version", "wrong_length", "truncated" })
        {
            using var handler = new VersionResponseHandler(test);
            using var client = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
            { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true, MaxErrorRetry = 0,
                HttpClientFactory = new VersionHttpClientFactory(handler) });
            var length = test == "empty" ? 0 : 4;
            if (test is "present" or "empty" or "absent")
            {
                var exists = await RecoveryPurgeDeletionJournal.ExactVersionExistsAsync(client, "recovery-payload",
                    "payload/sha256/hash", "exact-version", length, default);
                if (exists != (test != "absent")) throw new InvalidOperationException("SDK absence classification failed");
            }
            else await RejectAsync(async () => { _ = await RecoveryPurgeDeletionJournal.ExactVersionExistsAsync(client,
                "recovery-payload", "payload/sha256/hash", "exact-version", length, default); });
            if (handler.Requests != 1) throw new InvalidOperationException("Unexpected provider request retry");
        }
    }

    private sealed class VersionHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) => new(handler, disposeHandler: false);
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
    }

    private sealed class VersionResponseHandler(string test) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/recovery-payload/payload/sha256/hash" ||
                request.RequestUri.Query != "?versionId=exact-version" ||
                request.Headers.Range?.ToString() != (test == "empty" ? null : "bytes=0-0"))
                throw new InvalidOperationException("SDK probe did not request the exact bounded version");
            if (test is "absent" or "denied" or "generic404")
                return Task.FromResult(new HttpResponseMessage(test == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound)
                { Content = new StringContent(test == "generic404" ? "" :
                    $"<Error><Code>{(test == "denied" ? "AccessDenied" : "NoSuchVersion")}</Code><Message>fixture</Message></Error>",
                    System.Text.Encoding.UTF8, "application/xml") });
            var response = new HttpResponseMessage(test == "empty" ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(test is "empty" or "truncated" ? [] : [1]) };
            response.Headers.Add("x-amz-version-id", test == "wrong_version" ? "other-version" : "exact-version");
            if (test != "empty") response.Content.Headers.TryAddWithoutValidation("Content-Range",
                test == "wrong_length" ? "bytes 0-0/5" : "bytes 0-0/4");
            return Task.FromResult(response);
        }
    }

    private sealed class JournalDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:1" })
    {
        internal Dictionary<string, Dictionary<string, AttributeValue>> Items { get; } = [];
        internal int LoseWrite { get; init; }
        private int writes;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            if (request.TableName != "fences" || request.ConsistentRead != true) throw new InvalidOperationException("Journal read is not strongly scoped");
            return Task.FromResult(new GetItemResponse { Item = Items.TryGetValue(request.Key["recordId"].S, out var item) ? item : [] });
        }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken ct = default)
        {
            if (request.TableName != "fences" || request.ConditionExpression != "attribute_not_exists(scopeId)")
                throw new InvalidOperationException("Journal write is not immutable");
            if (!Items.TryAdd(request.Item["recordId"].S, request.Item)) throw new ConditionalCheckFailedException("exists");
            if (++writes == LoseWrite) throw new IOException("Accepted but lost fixture write");
            return Task.FromResult(new PutItemResponse());
        }
    }

    private sealed class JournalS3(RecoveryPurgeTargetVersionV1 target) : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        internal bool Present { get; set; } = true;
        internal bool LoseDelete { get; init; }
        internal bool DenyDelete { get; init; }
        internal bool DenyRead { get; init; }
        internal int Deletes { get; private set; }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default)
        {
            if (request.BucketName != target.Bucket || request.Key != target.Key || request.VersionId != target.VersionId) throw new InvalidOperationException("Unscoped version read");
            if (DenyRead) throw new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" };
            if (!Present) throw new AmazonS3Exception("absent") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchVersion" };
            return Task.FromResult(new GetObjectResponse { VersionId = target.VersionId, ContentLength = 1,
                ContentRange = $"bytes 0-0/{target.ByteLength}", ResponseStream = new MemoryStream([1]) });
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken ct = default)
        {
            if (request.VersionId != target.VersionId || request.Key != target.Key || request.BucketName != target.Bucket) throw new InvalidOperationException("Unscoped version deletion");
            if (DenyDelete) throw new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" };
            Present = false;
            Deletes++;
            if (LoseDelete) throw new IOException("Accepted but lost fixture deletion");
            return Task.FromResult(new DeleteObjectResponse { VersionId = target.VersionId });
        }
    }
}
