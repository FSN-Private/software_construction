using System.Net;
using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryCrossPointReuseChecks
{
    public static async Task RunAsync()
    {
        var fixture = Fixture.Create();
        var dynamo = new ReuseDynamo(fixture.ControlItems);
        var evidence = new ReuseS3();
        foreach (var item in fixture.EvidenceObjects)
            evidence.Add("recovery-evidence", item.Key, item.Version, item.Bytes);

        var reusable = await RecoveryCopyReceiptReuse.ReadLatestAsync(dynamo, evidence,
            "control", fixture.OwnerId, fixture.LibraryId, "primary-capture", "primary-media",
            "recovery-payload", "recovery-evidence", fixture.SourceAccountId, fixture.SourceRegion,
            fixture.DestinationAccountId, fixture.DestinationRegion, fixture.WorkerBuildDigest,
            fixture.Trust, CancellationToken.None);
        var identity = RecoveryCopyReceiptReuse.ReceiptIdentity.From("primary-media", fixture.MediaReference,
            "recovery-payload", "payload/sha256/" + fixture.MediaReference.Sha256);
        Check(reusable.TryGetValue(identity, out var signedReceipt) &&
            signedReceipt.DestinationVersionId == "payload-v1",
            "The exact source tuple is reusable only after its prior checkpoint and receipt chunk verify.");

        var primary = new ReuseS3();
        var payload = new ReuseS3 { RetainUntilUtc = fixture.Now.AddDays(100).UtcDateTime };
        payload.Add("recovery-payload", identity.DestinationKey, "payload-v1", fixture.MediaBytes);
        var store = new RecoveryObjectStore(primary, payload, "primary-media", "recovery-payload",
            "recovery-fixture", fixture.DestinationRegion, "kms-fixture", fixture.WorkerBuildDigest,
            new FrozenTimeProvider(fixture.Now));
        var candidate = new RecoveryCopyReceipt(fixture.MediaReference, signedReceipt!.DestinationBucket,
            signedReceipt.DestinationKey, signedReceipt.DestinationVersionId, "recovery-fixture",
            fixture.DestinationRegion, signedReceipt.VerifiedAtUtc, signedReceipt.VerifiedAtUtc,
            fixture.WorkerBuildDigest, RetentionPolicyVerified: true, AccessPolicyVerified: true);
        var currentAdmission = fixture.Now.AddMinutes(-1);
        var reused = await store.ReuseVerifiedReceiptAsync(fixture.MediaReference, identity.DestinationKey,
            candidate, currentAdmission, CancellationToken.None);
        Check(reused.DestinationVersionId == "payload-v1" && reused.RetentionProtectedFromUtc == currentAdmission,
            "Reuse keeps the exact signed version and rechecks protection from the current logical admission.");
        Check(primary.FullReadBytes == 0 && payload.FullReadBytes == 0 && payload.RangeReadBytes == 1,
            "Cross-point reuse does not reread full media bytes and performs only a one-byte current access probe.");

        var expiredPayload = new ReuseS3 { RetainUntilUtc = currentAdmission.AddDays(89).UtcDateTime };
        expiredPayload.Add("recovery-payload", identity.DestinationKey, "payload-v1", fixture.MediaBytes);
        var expiredStore = new RecoveryObjectStore(primary, expiredPayload, "primary-media", "recovery-payload",
            "recovery-fixture", fixture.DestinationRegion, "kms-fixture", fixture.WorkerBuildDigest,
            new FrozenTimeProvider(fixture.Now));
        var extended = await expiredStore.ReuseVerifiedReceiptAsync(
            fixture.MediaReference, identity.DestinationKey, candidate, currentAdmission, CancellationToken.None);
        Check(extended.DestinationVersionId == candidate.DestinationVersionId && expiredPayload.RetentionWrites == 1 &&
            expiredPayload.RetainUntilUtc == fixture.Now.AddDays(91).UtcDateTime && expiredPayload.FullReadBytes == 0,
            "Fenced reuse extends only the same exact media version, preserving byte verification cadence.");
        Check(payload.RetentionWrites == 0, "Sufficient protection must not issue a retention write.");
        foreach (var fault in new[] { "denied", "lost_response", "insufficient", "compliance", "missing_mode" })
        {
            var failing = new ReuseS3 { RetainUntilUtc = currentAdmission.AddDays(89).UtcDateTime, RetentionFault = fault };
            failing.Add("recovery-payload", identity.DestinationKey, "payload-v1", fixture.MediaBytes);
            var failingStore = new RecoveryObjectStore(primary, failing, "primary-media", "recovery-payload",
                "recovery-fixture", fixture.DestinationRegion, "kms-fixture", fixture.WorkerBuildDigest,
                new FrozenTimeProvider(fixture.Now));
            if (fault is "denied" or "lost_response")
                await ThrowsAsync<AmazonS3Exception>(() => failingStore.ReuseVerifiedReceiptAsync(
                    fixture.MediaReference, identity.DestinationKey, candidate, currentAdmission, default));
            else await ThrowsAsync<InvalidDataException>(() => failingStore.ReuseVerifiedReceiptAsync(
                fixture.MediaReference, identity.DestinationKey, candidate, currentAdmission, default));
            if (fault == "lost_response")
            {
                var settled = await failingStore.ReuseVerifiedReceiptAsync(fixture.MediaReference,
                    identity.DestinationKey, candidate, currentAdmission, default);
                Check(settled.DestinationVersionId == "payload-v1" && failing.RetentionWrites == 1,
                    "Accepted-but-lost retention response is reconciled by exact reread, without another write.");
            }
            if (fault is "compliance" or "missing_mode") Check(failing.RetentionWrites == 0,
                "An unknown or different lock mode cannot be extended.");
        }

        var noHeadDynamo = new ReuseDynamo(fixture.ControlItems, includeHead: false);
        var noHead = await RecoveryCopyReceiptReuse.ReadLatestAsync(noHeadDynamo, evidence, "control",
            fixture.OwnerId, fixture.LibraryId, "primary-capture", "primary-media", "recovery-payload",
            "recovery-evidence", fixture.SourceAccountId, fixture.SourceRegion, fixture.DestinationAccountId,
            fixture.DestinationRegion, fixture.WorkerBuildDigest, fixture.Trust, CancellationToken.None);
        Check(noHead.Count == 0, "A pre-upgrade owner index without latest-copy fields safely falls back to a full copy.");

        var badEvidence = new ReuseS3();
        foreach (var item in fixture.EvidenceObjects)
            badEvidence.Add("recovery-evidence", item.Key, item.Version,
                item.Key == fixture.CheckpointKey ? [.. item.Bytes.AsSpan(0, item.Bytes.Length - 1), (byte)(item.Bytes[^1] ^ 0xff)] : item.Bytes);
        await ThrowsAsync<RecoveryWorkerException>(() => RecoveryCopyReceiptReuse.ReadLatestAsync(dynamo,
            badEvidence, "control", fixture.OwnerId, fixture.LibraryId, "primary-capture", "primary-media",
            "recovery-payload", "recovery-evidence", fixture.SourceAccountId, fixture.SourceRegion,
            fixture.DestinationAccountId, fixture.DestinationRegion, fixture.WorkerBuildDigest,
            fixture.Trust, CancellationToken.None));

        var older = fixture.Manifest with
        {
            RecoveryPointId = Guid.NewGuid(),
            SnapshotCutoffUtc = fixture.Manifest.SnapshotCutoffUtc.AddMinutes(-1)
        };
        var markerStore = new ReuseDynamo(Fixture.IndexOnly(fixture, includeHead: false));
        await RecoveryCopyReceiptReuse.AdvanceHeadAsync(markerStore, "control", older,
            RecoveryEvidenceCodec.Hash(older.SerializeCanonical()), CancellationToken.None);
        Check(markerStore.GetMarker("control", fixture.OwnerId, fixture.LibraryId).ContainsKey("latestCopiedPointId"),
            "The first sealed copy atomically publishes the optional chronological reuse head.");
        await RecoveryCopyReceiptReuse.AdvanceHeadAsync(markerStore, "control", fixture.Manifest,
            fixture.SourceManifestSha256, CancellationToken.None);
        Check(markerStore.GetMarker("control", fixture.OwnerId, fixture.LibraryId)["latestCopiedPointId"].S ==
            fixture.Manifest.RecoveryPointId.ToString("D").ToLowerInvariant(),
            "A newer snapshot advances the sealed-copy head.");
        await RecoveryCopyReceiptReuse.AdvanceHeadAsync(markerStore, "control", older,
            RecoveryEvidenceCodec.Hash(older.SerializeCanonical()), CancellationToken.None);
        Check(markerStore.GetMarker("control", fixture.OwnerId, fixture.LibraryId)["latestCopiedPointId"].S ==
            fixture.Manifest.RecoveryPointId.ToString("D").ToLowerInvariant(),
            "An out-of-order older copy cannot roll the sealed-copy head backward.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private sealed class Fixture
    {
        private Fixture() { }

        public required string OwnerId { get; init; }
        public required string LibraryId { get; init; }
        public required string SourceAccountId { get; init; }
        public required string SourceRegion { get; init; }
        public required string DestinationAccountId { get; init; }
        public required string DestinationRegion { get; init; }
        public required string WorkerBuildDigest { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required RecoverySigningTrust Trust { get; init; }
        public required byte[] MediaBytes { get; init; }
        public required RecoveryObjectReference MediaReference { get; init; }
        public required RecoveryPointManifestV1 Manifest { get; init; }
        public required string SourceManifestSha256 { get; init; }
        public required string CheckpointKey { get; init; }
        public required IReadOnlyList<StoredObject> EvidenceObjects { get; init; }
        public required Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> ControlItems { get; init; }

        public static Fixture Create()
        {
            const string owner = "owner-reuse";
            const string library = "library-reuse";
            const string sourceAccount = "111122223333";
            const string sourceRegion = "ap-southeast-1";
            const string destinationAccount = "444455556666";
            const string destinationRegion = "ap-southeast-2";
            var workerDigest = new string('a', 64);
            var now = DateTimeOffset.UtcNow;
            var pointId = Guid.NewGuid();
            var captureId = Guid.NewGuid();
            var mediaBytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            var mediaHash = RecoveryEvidenceCodec.Hash(mediaBytes);
            var mediaReference = new RecoveryObjectReference(RecoveryDataClass.OriginalImage, owner, library,
                "objects/media/original.jpg", "source-media-v7", mediaHash, mediaBytes.LongLength,
                [new RecoveryLogicalReference(owner, library, Guid.NewGuid())]);
            var manifest = MakeManifest(pointId, captureId, owner, library, now, mediaReference);
            var sourceManifestBytes = manifest.SerializeCanonical();
            var sourceManifestSha = RecoveryEvidenceCodec.Hash(sourceManifestBytes);
            var sourceManifestKey = $"captures/{captureId:D}/scopes/{ScopeHash(owner, library)}/manifest.json";
            var sourceManifestObjectKey = $"points/{pointId:D}/source-manifests/{sourceManifestSha}.json";
            var ownerProofBytes = "owner-access-proof"u8.ToArray();
            var ownerProofSha = RecoveryEvidenceCodec.Hash(ownerProofBytes);
            var ownerProofKey = $"evidence/owner-access/{ScopeHash(owner, library)}/proof.json";
            var finalManifest = manifest with
            {
                Components = manifest.Components.Concat([new RecoveryComponentReference("owner-access-proof",
                    RecoveryDataClass.AccessEvidence, $"s3://recovery-evidence/{ownerProofKey}", "owner-proof-v1",
                    ownerProofSha, ownerProofBytes.LongLength)]).ToArray(),
                Coverage = manifest.Coverage.Select(item => item.DataClass == RecoveryDataClass.AccessEvidence
                    ? new RecoveryClassCoverage(item.DataClass, 1, ownerProofBytes.LongLength)
                    : item).ToArray()
            };
            var finalManifestBytes = finalManifest.SerializeCanonical();
            var finalManifestSha = RecoveryEvidenceCodec.Hash(finalManifestBytes);
            var finalManifestKey = $"points/{pointId:D}/manifests/{finalManifestSha}.json";
            var finalVersion = "final-manifest-v1";
            var chunkKey = $"points/{pointId:D}/receipts/00000000.json";
            var chunkVersion = "receipt-chunk-v1";
            var mediaReceipt = new PrivateCopyReceiptV1("primary-media", mediaReference.SourceKey,
                mediaReference.SourceVersionId, mediaHash, mediaBytes.LongLength, "recovery-payload",
                "payload/sha256/" + mediaHash, "payload-v1", mediaReference.DataClass.ToString(), now.AddMinutes(-5));
            var ownerReceipt = new PrivateCopyReceiptV1("recovery-evidence", ownerProofKey, "owner-proof-v1",
                ownerProofSha, ownerProofBytes.LongLength, "recovery-evidence", ownerProofKey, "owner-proof-v1",
                RecoveryDataClass.AccessEvidence.ToString(), now.AddMinutes(-5));
            var chunkBytes = RecoveryEvidenceCodec.SerializeCanonical(new[] { mediaReceipt, ownerReceipt });
            var chunkSha = RecoveryEvidenceCodec.Hash(chunkBytes);
            var sourceManifestReceipt = new PrivateArtifactReceiptV1("primary-capture", sourceManifestKey,
                "source-manifest-v7", "recovery-evidence", sourceManifestObjectKey, "source-manifest-copy-v1",
                sourceManifestSha, sourceManifestBytes.LongLength);
            var finalManifestReceipt = new PrivateArtifactReceiptV1("recovery-evidence", finalManifestKey,
                finalVersion, "recovery-evidence", finalManifestKey, finalVersion, finalManifestSha,
                finalManifestBytes.LongLength);
            var ownerProofReceipt = new PrivateArtifactReceiptV1("recovery-evidence", ownerProofKey,
                "owner-proof-v1", "recovery-evidence", ownerProofKey, "owner-proof-v1", ownerProofSha,
                ownerProofBytes.LongLength);
            using var rsa = RSA.Create(2048);
            var publicSpki = rsa.ExportSubjectPublicKeyInfo();
            const string keyId = "recovery-test-signing-key";
            var fingerprint = RecoveryEvidenceCodec.Hash(publicSpki);
            var trust = new RecoverySigningTrust(keyId, Convert.ToBase64String(publicSpki), fingerprint);
            var payload = new PrivateCopyCheckpointPayloadV1(1, captureId, pointId, owner, library,
                sourceManifestSha, finalManifestSha, sourceRegion, destinationRegion, destinationAccount,
                now.AddMinutes(-4), workerDigest, 2,
                [new PrivateReceiptChunkV1(0, 2, chunkKey, chunkVersion, chunkSha, chunkBytes.LongLength)],
                sourceManifestReceipt, ownerProofReceipt, finalManifestReceipt);
            var payloadBytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
            var signature = rsa.SignHash(SHA256.HashData(payloadBytes), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            var envelope = new PrivateSignedCheckpointV1(1, "recovery_copy_checkpoint", "copy-worker",
                keyId, fingerprint, RecoveryEvidenceCodec.Hash(payloadBytes), Convert.ToBase64String(payloadBytes),
                Convert.ToBase64String(signature));
            var checkpointBytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
            var checkpointKey = $"points/{pointId:D}/checkpoints/{RecoveryEvidenceCodec.Hash(payloadBytes)}.json";
            var checkpointSha = RecoveryEvidenceCodec.Hash(checkpointBytes);

            var scope = ScopeHash(owner, library);
            var index = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S("OWNER#" + scope), ["sk"] = S("INDEX"), ["indexVersion"] = N(1),
                ["status"] = S("complete"), ["bootstrapPointCount"] = N(0), ["indexedPointCount"] = N(1),
                ["bootstrapPointSetSha256"] = S(new string('0', 64)),
                ["latestCopiedPointId"] = S(pointId.ToString("D").ToLowerInvariant()),
                ["latestSourceManifestSha256"] = S(sourceManifestSha),
                ["latestSnapshotCutoffUtc"] = S(manifest.SnapshotCutoffUtc.ToString("O")),
                ["latestChangeSequence"] = N(manifest.ChangeWatermark.LastSequence)
            };
            var copy = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()), ["sk"] = S("COPY"),
                ["captureId"] = S(captureId.ToString("D")), ["ownerId"] = S(owner), ["libraryId"] = S(library),
                ["sourceAccountId"] = S(sourceAccount), ["sourceRegion"] = S(sourceRegion),
                ["recoveryAccountId"] = S(destinationAccount), ["recoveryRegion"] = S(destinationRegion),
                ["sourceManifestBucket"] = S("primary-capture"), ["sourceManifestKey"] = S(sourceManifestKey),
                ["sourceManifestVersionId"] = S("source-manifest-v7"), ["sourceManifestSha256"] = S(sourceManifestSha),
                ["evidenceBucket"] = S("recovery-evidence"), ["manifestKey"] = S(finalManifestKey),
                ["manifestVersionId"] = S(finalVersion), ["manifestSha256"] = S(finalManifestSha),
                ["checkpointKey"] = S(checkpointKey), ["checkpointVersionId"] = S("checkpoint-v1"),
                ["checkpointSha256"] = S(checkpointSha), ["checkpointByteLength"] = N(checkpointBytes.LongLength),
                ["copiedAtUtc"] = S(now.AddMinutes(-3).ToString("O")), ["state"] = S("copied")
            };
            var controlItems = new Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>>
            {
                [("OWNER#" + scope, "INDEX")] = index,
                [("POINT#" + pointId.ToString("D").ToLowerInvariant(), "COPY")] = copy
            };
            return new Fixture
            {
                OwnerId = owner,
                LibraryId = library,
                SourceAccountId = sourceAccount,
                SourceRegion = sourceRegion,
                DestinationAccountId = destinationAccount,
                DestinationRegion = destinationRegion,
                WorkerBuildDigest = workerDigest,
                Now = now,
                Trust = trust,
                MediaBytes = mediaBytes,
                MediaReference = mediaReference,
                Manifest = manifest,
                SourceManifestSha256 = sourceManifestSha,
                CheckpointKey = checkpointKey,
                EvidenceObjects =
                [
                    new(sourceManifestObjectKey, "source-manifest-copy-v1", sourceManifestBytes),
                    new(chunkKey, chunkVersion, chunkBytes),
                    new(ownerProofKey, "owner-proof-v1", ownerProofBytes),
                    new(finalManifestKey, finalVersion, finalManifestBytes),
                    new(checkpointKey, "checkpoint-v1", checkpointBytes)
                ],
                ControlItems = controlItems
            };
        }

        public static Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> IndexOnly(
            Fixture fixture, bool includeHead)
        {
            var existing = fixture.ControlItems[("OWNER#" + ScopeHash(fixture.OwnerId, fixture.LibraryId), "INDEX")];
            var marker = new Dictionary<string, AttributeValue>(existing);
            if (!includeHead)
                foreach (var name in new[] { "latestCopiedPointId", "latestSourceManifestSha256", "latestSnapshotCutoffUtc", "latestChangeSequence" })
                    marker.Remove(name);
            return new() { [("OWNER#" + ScopeHash(fixture.OwnerId, fixture.LibraryId), "INDEX")] = marker };
        }

        private static RecoveryPointManifestV1 MakeManifest(Guid pointId, Guid captureId, string owner,
            string library, DateTimeOffset now, RecoveryObjectReference media)
        {
            var bytes = new byte[] { 4, 5, 6, 7 };
            var digest = RecoveryEvidenceCodec.Hash(bytes);
            var components = new[]
            {
                new RecoveryComponentReference("database", RecoveryDataClass.DatabaseSnapshot,
                    "s3://primary-capture/captures/db.dump", "db-v1", digest, bytes.LongLength),
                new RecoveryComponentReference("inventory", RecoveryDataClass.ObjectInventory,
                    "s3://primary-capture/captures/inventory.json", "inventory-v1", digest, bytes.LongLength)
            };
            var classes = new[]
            {
                RecoveryDataClass.OriginalImage, RecoveryDataClass.XmpSidecar, RecoveryDataClass.VersionGraph,
                RecoveryDataClass.LibraryMetadata, RecoveryDataClass.AuditRecord, RecoveryDataClass.Configuration,
                RecoveryDataClass.ReleaseArtifact, RecoveryDataClass.AccessEvidence, RecoveryDataClass.RecoveryEvidence,
                RecoveryDataClass.DatabaseSnapshot, RecoveryDataClass.ObjectInventory
            };
            return new RecoveryPointManifestV1(RecoveryPointManifestV1.CurrentSchemaVersion, pointId, captureId,
                owner, library, "ap-southeast-1", now, now.AddMinutes(-1), "12345", "16/B374D848", digest,
                new RecoveryChangeWatermark(7, now.AddMinutes(-2)), components, [media],
                classes.Select(dataClass => new RecoveryClassCoverage(dataClass,
                    dataClass == RecoveryDataClass.OriginalImage ? 1 : 0,
                    dataClass == RecoveryDataClass.OriginalImage ? media.ByteLength : 0)).ToArray());
        }

        private static string ScopeHash(string owner, string library) => RecoveryEvidenceCodec.Hash(
            System.Text.Encoding.UTF8.GetBytes(owner + "\0" + library));
    }

    private sealed record StoredObject(string Key, string Version, byte[] Bytes);

    private sealed class ReuseS3 : AmazonS3Client
    {
        private readonly Dictionary<(string Bucket, string Key, string Version), byte[]> objects = [];

        public ReuseS3() : base(new AnonymousAWSCredentials(), new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true
        }) { }

        public DateTime? RetainUntilUtc { get; set; }
        public string? RetentionFault { get; init; }
        public int RetentionWrites { get; private set; }
        public long FullReadBytes { get; private set; }
        public long RangeReadBytes { get; private set; }
        public void Add(string bucket, string key, string version, byte[] bytes) => objects[(bucket, key, version)] = bytes;

        public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(
            GetBucketVersioningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetBucketVersioningResponse
            { VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

        public override Task<GetObjectLockConfigurationResponse> GetObjectLockConfigurationAsync(
            GetObjectLockConfigurationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectLockConfigurationResponse
            {
                ObjectLockConfiguration = new ObjectLockConfiguration
                {
                    ObjectLockEnabled = ObjectLockEnabled.Enabled,
                    Rule = new ObjectLockRule
                    { DefaultRetention = new DefaultRetention { Days = 90, Mode = ObjectLockRetentionMode.Governance } }
                }
            });

        public override Task<GetPublicAccessBlockResponse> GetPublicAccessBlockAsync(
            GetPublicAccessBlockRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetPublicAccessBlockResponse
            {
                PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                {
                    BlockPublicAcls = true, IgnorePublicAcls = true, BlockPublicPolicy = true, RestrictPublicBuckets = true
                }
            });

        public override Task<GetObjectRetentionResponse> GetObjectRetentionAsync(
            GetObjectRetentionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectRetentionResponse
            {
                Retention = new ObjectLockRetention
                {
                    Mode = RetentionFault == "missing_mode" ? null : RetentionFault == "compliance" ?
                        ObjectLockRetentionMode.Compliance : ObjectLockRetentionMode.Governance,
                    RetainUntilDate = RetainUntilUtc ?? DateTime.UtcNow.AddDays(100)
                }
            });

        public override Task<PutObjectRetentionResponse> PutObjectRetentionAsync(PutObjectRetentionRequest request,
            CancellationToken cancellationToken = default)
        {
            Check(request.BucketName == "recovery-payload" && request.Key.StartsWith("payload/sha256/", StringComparison.Ordinal) &&
                request.VersionId == "payload-v1" && request.BypassGovernanceRetention is false &&
                request.Retention.Mode == ObjectLockRetentionMode.Governance,
                "Retention extension escaped exact approved scope or bypassed governance.");
            RetentionWrites++;
            if (RetentionFault == "denied") throw new AmazonS3Exception("fixture denied") { StatusCode = HttpStatusCode.Forbidden };
            if (RetentionFault != "insufficient") RetainUntilUtc = request.Retention.RetainUntilDate;
            if (RetentionFault == "lost_response") throw new AmazonS3Exception("fixture accepted but response lost");
            return Task.FromResult(new PutObjectRetentionResponse());
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!objects.TryGetValue((request.BucketName, request.Key, request.VersionId ?? string.Empty), out var bytes))
                throw new AmazonS3Exception("fixture exact version missing") { StatusCode = HttpStatusCode.NotFound };
            var body = request.ByteRange is null ? bytes : bytes[..Math.Min(1, bytes.Length)];
            if (request.ByteRange is null) FullReadBytes += body.LongLength;
            else RangeReadBytes += body.LongLength;
            return Task.FromResult(new GetObjectResponse
            {
                BucketName = request.BucketName, Key = request.Key, VersionId = request.VersionId,
                ContentLength = body.LongLength, ResponseStream = new MemoryStream(body, writable: false)
            });
        }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!objects.TryGetValue((request.BucketName, request.Key, request.VersionId ?? string.Empty), out var bytes))
                throw new AmazonS3Exception("fixture exact version missing") { StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectMetadataResponse
            { VersionId = request.VersionId, ContentLength = bytes.LongLength });
        }
    }

    private sealed class ReuseDynamo : AmazonDynamoDBClient
    {
        private readonly Dictionary<(string Table, string Pk, string Sk), Dictionary<string, AttributeValue>> items = [];

        public ReuseDynamo(Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> source,
            bool includeHead = true) : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig
            { ServiceURL = "http://127.0.0.1:1" })
        {
            foreach (var (key, value) in source)
            {
                var clone = new Dictionary<string, AttributeValue>(value);
                if (!includeHead && key.Sk == "INDEX")
                    foreach (var name in new[] { "latestCopiedPointId", "latestSourceManifestSha256", "latestSnapshotCutoffUtc", "latestChangeSequence" })
                        clone.Remove(name);
                items[("control", key.Pk, key.Sk)] = clone;
            }
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ConsistentRead != true) throw new InvalidOperationException("strong_reads_required");
            var key = (request.TableName, request.Key["pk"].S!, request.Key["sk"].S!);
            return Task.FromResult(new GetItemResponse
            { Item = items.TryGetValue(key, out var item) ? new Dictionary<string, AttributeValue>(item) : [] });
        }

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = (request.TableName, request.Key["pk"].S!, request.Key["sk"].S!);
            if (!items.TryGetValue(key, out var marker) || Get(marker, "status") != "complete" ||
                GetNumber(marker, "indexVersion") != "1")
                throw new ConditionalCheckFailedException("marker condition failed");
            var nextCutoff = DateTimeOffset.Parse(Get(request.ExpressionAttributeValues, ":cutoff"),
                System.Globalization.CultureInfo.InvariantCulture);
            var nextSequence = long.Parse(GetNumber(request.ExpressionAttributeValues, ":sequence"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (marker.TryGetValue("latestSnapshotCutoffUtc", out var cutoffValue))
            {
                var currentCutoff = DateTimeOffset.Parse(cutoffValue.S!,
                    System.Globalization.CultureInfo.InvariantCulture);
                var currentSequence = long.Parse(GetNumber(marker, "latestChangeSequence"),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (currentCutoff > nextCutoff || currentCutoff == nextCutoff && currentSequence >= nextSequence)
                    throw new ConditionalCheckFailedException("chronological condition failed");
            }
            marker["latestCopiedPointId"] = S(Get(request.ExpressionAttributeValues, ":point"));
            marker["latestSourceManifestSha256"] = S(Get(request.ExpressionAttributeValues, ":manifest"));
            marker["latestSnapshotCutoffUtc"] = S(Get(request.ExpressionAttributeValues, ":cutoff"));
            marker["latestChangeSequence"] = N(nextSequence);
            return Task.FromResult(new UpdateItemResponse { Attributes = new Dictionary<string, AttributeValue>(marker) });
        }

        public Dictionary<string, AttributeValue> GetMarker(string table, string owner, string library) =>
            items[(table, "OWNER#" + RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes(owner + "\0" + library)), "INDEX")];

        private static string Get(Dictionary<string, AttributeValue> item, string name) => item[name].S ?? string.Empty;
        private static string GetNumber(Dictionary<string, AttributeValue> item, string name) => item[name].N ?? string.Empty;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
