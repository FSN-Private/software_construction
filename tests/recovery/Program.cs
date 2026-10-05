using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Storage;

try
{
    ContractChecks();
    await ObjectStoreChecksAsync();
    Console.WriteLine("Recovery contracts and exact-version storage checks passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Recovery checks failed: {exception.GetType().Name} {exception.Message}");
    Environment.ExitCode = 1;
}

static void ContractChecks()
{
    var now = DateTimeOffset.Parse("2026-10-05T10:20:30Z");
    var pointId = Guid.NewGuid();
    var manifest = Manifest(pointId, now);
    var bytes = manifest.SerializeCanonical();
    var parsed = RecoveryPointManifestV1.ParseCanonical(bytes);
    Check(parsed.Digest() == manifest.Digest(), "Canonical manifest must round trip without changing its digest.");
    Check(parsed.Objects.Count == 1 && parsed.Coverage.Count == 11, "Manifest canonicalization preserves exact object and all protected class counts.");

    RejectInvalid(() => RecoveryPointManifestV1.ParseCanonical("{"u8));
    RejectInvalid(() => (manifest with { SchemaVersion = 2 }).SerializeCanonical());
    RejectInvalid(() => (manifest with { MigrationDigest = "bad" }).SerializeCanonical());
    RejectInvalid(() => (manifest with { CapturedAtUtc = now.ToOffset(TimeSpan.FromHours(1)) }).SerializeCanonical());
    RejectInvalid(() => (manifest with { Objects = [manifest.Objects[0] with { OwnerId = "foreign" }] }).SerializeCanonical());
    RejectInvalid(() => (manifest with { Coverage = manifest.Coverage.Where(item => item.DataClass != RecoveryDataClass.AuditRecord).ToArray() }).SerializeCanonical());
    RejectInvalid(() => (manifest with { Objects = [manifest.Objects[0] with { DataClass = RecoveryDataClass.PreviewDerivative }] }).SerializeCanonical());

    var intent = Intent(manifest.Objects[0], pointId, "recovery/objects/item");
    Check(intent.CanTransitionTo(RecoveryWriteState.Unknown), "Admitted write may become unknown after a provider timeout.");
    Check(!intent.CanTransitionTo(RecoveryWriteState.Cleaned), "A write cannot skip provider settlement evidence.");
    Check(intent with { OwnerId = "foreign" } is var foreign && ThrowsInvalid(foreign.Validate), "Write intent must reject a source from another owner.");

    var shuffled = manifest with { Coverage = manifest.Coverage.Reverse().ToArray() };
    Check(shuffled.Digest() == manifest.Digest(), "Manifest order must not alter canonical recovery-point identity.");
    var noncanonical = bytes.Concat(" "u8.ToArray()).ToArray();
    RejectInvalid(() => RecoveryPointManifestV1.ParseCanonical(noncanonical));
}

static async Task ObjectStoreChecksAsync()
{
    var content = Enumerable.Range(0, 256 * 1024).Select(value => (byte)(value % 251)).ToArray();
    var address = Address(content);
    var source = new TestS3();
    var destination = new TestS3();
    source.Add("primary", "source-key", "source-v1", content);
    var objectRef = ObjectReference(address, "source-key", "source-v1");
    var intent = Intent(objectRef, Guid.NewGuid(), "recovery/point/objects/test");
    var store = Store(source, destination);

    var receipt = await store.CopyVerifiedAsync(intent);
    Check(receipt.Source == objectRef && receipt.DestinationVersionId.Length > 0, "Copy receipt binds exact source and recovery versions.");
    Check(destination.PutCount == 1 && destination.Objects.Count == 1, "Successful copy publishes one version after source verification.");
    await using (var recovered = await store.ReadVerifiedAsync(receipt))
    {
        using var output = new MemoryStream();
        await recovered.CopyToAsync(output);
        Check(output.ToArray().SequenceEqual(content), "Recovery read returns actual exact-version bytes after hashing.");
    }

    var replayed = await store.CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid(), AdmittedAtUtc = DateTimeOffset.UtcNow });
    Check(replayed.DestinationVersionId == receipt.DestinationVersionId && destination.PutCount == 1,
        "Provider-success/database-settlement replay reuses only the verified immutable destination version.");

    var admitted = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
    var clock = new MutableClock(admitted);
    var timedSource = new TestS3();
    timedSource.Add("primary", "source-key", "source-v1", content);
    var timedDestination = new TestS3 { RetainUntilUtc = admitted.AddDays(90).AddMinutes(1).UtcDateTime };
    clock.Advance(TimeSpan.FromMinutes(2));
    var timedStore = Store(timedSource, timedDestination, clock);
    var timedReceipt = await timedStore.CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid(), AdmittedAtUtc = admitted });
    Check(timedReceipt.RetentionProtectedFromUtc == admitted && timedReceipt.VerifiedAtUtc > admitted,
        "Receipt binds the durable admission time, so native 90-day retention is not measured from the later verification time.");
    clock.Advance(TimeSpan.FromDays(91));
    await using (var longTermRead = await timedStore.ReadVerifiedAsync(timedReceipt))
    {
        using var output = new MemoryStream();
        await longTermRead.CopyToAsync(output);
        Check(output.ToArray().SequenceEqual(content), "Long-term exact-version reads remain available after the minimum retention window expires.");
    }

    destination.ReturnedVersionId = "swapped-version";
    await RejectAsync<InvalidDataException>(() => store.ReadVerifiedAsync(receipt));
    destination.ReturnedVersionId = null;

    var corruptSource = new TestS3();
    corruptSource.Add("primary", "source-key", "source-v1", [1, 2, 3]);
    var noDestination = new TestS3();
    await RejectAsync<InvalidDataException>(() => Store(corruptSource, noDestination).CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid() }));
    Check(noDestination.PutCount == 0, "Corrupt source bytes are rejected before any destination write.");

    source.ReturnedVersionId = "source-v2";
    var wrongVersionDestination = new TestS3();
    await RejectAsync<InvalidDataException>(() => Store(source, wrongVersionDestination).CopyVerifiedAsync(intent));
    Check(wrongVersionDestination.PutCount == 0, "A wrong source version is rejected even when its bytes match.");
    source.ReturnedVersionId = null;

    var deniedSource = new TestS3 { DenyReads = true };
    var deniedDestination = new TestS3();
    await RejectAsync<AmazonS3Exception>(() => Store(deniedSource, deniedDestination).CopyVerifiedAsync(intent));
    Check(deniedDestination.PutCount == 0, "Denied source/key access cannot produce a recovery copy.");
    destination.DenyReads = true;
    await RejectAsync<AmazonS3Exception>(() => store.ReadVerifiedAsync(receipt));
    destination.DenyReads = false;

    var swapped = new TestS3();
    swapped.Add("primary", "source-key", "source-v1", [9, 8, 7]);
    swapped.Add("primary", "source-key", "source-v2", content);
    await RejectAsync<InvalidDataException>(() => Store(swapped, new TestS3()).CopyVerifiedAsync(intent with
    {
        IntentId = Guid.NewGuid(),
        Source = objectRef with { SourceVersionId = "source-v1" }
    }));

    var interrupted = new TestS3 { FailReadAfterBytes = 4096 };
    interrupted.Add("primary", "source-key", "source-v1", content);
    var interruptedDestination = new TestS3();
    await RejectAsync<IOException>(() => Store(interrupted, interruptedDestination).CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid() }));
    Check(interruptedDestination.PutCount == 0, "Interrupted exact-version reads do not publish partial destination bytes.");

    var badExisting = new TestS3();
    badExisting.Add("recovery", intent.DestinationKey, "bad-v1", [8, 8, 8]);
    await RejectAsync<InvalidDataException>(() => Store(source, badExisting).CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid() }));

    var missingPolicy = new TestS3 { HasRetention = false };
    await RejectAsync<InvalidDataException>(() => Store(source, missingPolicy).CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid() }));

    var publicBucket = new TestS3 { PublicAccessBlock = null };
    await RejectAsync<InvalidOperationException>(() => Store(source, publicBucket).CopyVerifiedAsync(intent with { IntentId = Guid.NewGuid() }));
}

static RecoveryObjectStore Store(TestS3 source, TestS3 destination, TimeProvider? timeProvider = null) => new(
    source, destination, "primary", "recovery", "recovery-fixture", "ap-southeast-2", "kms-fixture", "unit-test", timeProvider);

static RecoveryObjectReference ObjectReference(ContentAddress address, string key, string version) => new(
    RecoveryDataClass.OriginalImage, "owner-a", "library-a", key, version, address.Sha256,
    address.Length, [new RecoveryLogicalReference("owner-a", "library-a", Guid.NewGuid())]);

static RecoveryWriteIntent Intent(RecoveryObjectReference reference, Guid point, string destinationKey) => new(
    Guid.NewGuid(), point, null, reference.OwnerId, reference.LibraryId, 1, reference,
    destinationKey, "recovery-copy-worker", RecoveryWriteState.StoragePending,
    DateTimeOffset.UtcNow, null, null, null);

static ContentAddress Address(byte[] content) => new(
    Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), content.LongLength);

static RecoveryPointManifestV1 Manifest(Guid point, DateTimeOffset now)
{
    byte[] bytes = [1, 2, 3, 4];
    var address = Address(bytes);
    var componentClasses = new[] { RecoveryDataClass.DatabaseSnapshot, RecoveryDataClass.ObjectInventory };
    var components = componentClasses.Select((dataClass, index) => new RecoveryComponentReference(
        index == 0 ? "database" : "inventory", dataClass,
        $"capture/{(index == 0 ? "database" : "inventory")}", $"version-{index}", address.Sha256, address.Length)).ToArray();
    var classes = new[]
    {
        RecoveryDataClass.OriginalImage, RecoveryDataClass.XmpSidecar, RecoveryDataClass.VersionGraph,
        RecoveryDataClass.LibraryMetadata, RecoveryDataClass.AuditRecord, RecoveryDataClass.Configuration,
        RecoveryDataClass.ReleaseArtifact, RecoveryDataClass.AccessEvidence, RecoveryDataClass.RecoveryEvidence,
        RecoveryDataClass.DatabaseSnapshot, RecoveryDataClass.ObjectInventory
    };
    return new RecoveryPointManifestV1(
        RecoveryPointManifestV1.CurrentSchemaVersion, point, Guid.NewGuid(), "owner-a", "library-a",
        "ap-southeast-1", now, now.AddMinutes(-1), "12345", "16/B374D848", address.Sha256,
        new RecoveryChangeWatermark(3, now.AddMinutes(-2)), components,
        [ObjectReference(address, "source/object", "source-v1")], classes.Select(item => new RecoveryClassCoverage(item, item == RecoveryDataClass.OriginalImage ? 1 : 0, item == RecoveryDataClass.OriginalImage ? 4 : 0)).ToArray());
}

static bool ThrowsInvalid(Action action)
{
    try { action(); return false; }
    catch (InvalidDataException) { return true; }
}

static void RejectInvalid(Action action)
{
    if (!ThrowsInvalid(action)) throw new InvalidOperationException("Expected InvalidDataException.");
}

static async Task RejectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestS3 : AmazonS3Client
{
    private readonly Dictionary<(string Bucket, string Key, string Version), byte[]> objects = [];
    private int nextVersion;

    public TestS3() : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true }) { }
    public IReadOnlyDictionary<(string Bucket, string Key, string Version), byte[]> Objects => objects;
    public int PutCount { get; private set; }
    public int? FailReadAfterBytes { get; init; }
    public bool HasRetention { get; init; } = true;
    public DateTime? RetainUntilUtc { get; init; }
    public string? ReturnedVersionId { get; set; }
    public bool DenyReads { get; set; }
    public PublicAccessBlockConfiguration? PublicAccessBlock { get; init; } = new()
    {
        BlockPublicAcls = true, IgnorePublicAcls = true, BlockPublicPolicy = true, RestrictPublicBuckets = true
    };

    public void Add(string bucket, string key, string version, byte[] content) => objects[(bucket, key, version)] = content;

    public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(GetBucketVersioningRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GetBucketVersioningResponse { VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

    public override Task<GetObjectLockConfigurationResponse> GetObjectLockConfigurationAsync(GetObjectLockConfigurationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GetObjectLockConfigurationResponse
        {
            ObjectLockConfiguration = new ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule { DefaultRetention = new DefaultRetention { Days = 90, Mode = ObjectLockRetentionMode.Governance } }
            }
        });

    public override Task<GetPublicAccessBlockResponse> GetPublicAccessBlockAsync(GetPublicAccessBlockRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GetPublicAccessBlockResponse { PublicAccessBlockConfiguration = PublicAccessBlock });

    public override Task<GetObjectRetentionResponse> GetObjectRetentionAsync(GetObjectRetentionRequest request, CancellationToken cancellationToken = default) =>
        HasRetention
            ? Task.FromResult(new GetObjectRetentionResponse { Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = RetainUntilUtc ?? DateTime.UtcNow.AddDays(100) } })
            : Task.FromResult(new GetObjectRetentionResponse());

    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (DenyReads) throw new AmazonS3Exception("fixture access denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" };
        if (!objects.TryGetValue((request.BucketName, request.Key, request.VersionId ?? ""), out var bytes))
            throw new AmazonS3Exception("fixture exact version missing") { StatusCode = HttpStatusCode.NotFound };
        Stream stream = FailReadAfterBytes is { } after ? new ReadFailureStream(bytes, after) : new MemoryStream(bytes, writable: false);
        return Task.FromResult(new GetObjectResponse { BucketName = request.BucketName, Key = request.Key, VersionId = ReturnedVersionId ?? request.VersionId, ContentLength = bytes.LongLength, ResponseStream = stream });
    }

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (objects.Keys.Any(item => item.Bucket == request.BucketName && item.Key == request.Key))
            throw new AmazonS3Exception("destination already exists") { StatusCode = HttpStatusCode.PreconditionFailed };
        using var output = new MemoryStream();
        await request.InputStream.CopyToAsync(output, cancellationToken);
        var version = "destination-v" + ++nextVersion;
        objects[(request.BucketName, request.Key, version)] = output.ToArray();
        PutCount++;
        return new PutObjectResponse { VersionId = version };
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
    {
        var exact = objects.Keys.Where(item => item.Bucket == request.BucketName && item.Key == request.Key && (request.VersionId is null || item.Version == request.VersionId))
            .OrderByDescending(item => item.Version, StringComparer.Ordinal).FirstOrDefault();
        if (exact == default) throw new AmazonS3Exception("fixture exact version missing") { StatusCode = HttpStatusCode.NotFound };
        var bytes = objects[exact];
        return Task.FromResult(new GetObjectMetadataResponse { VersionId = exact.Version, ContentLength = bytes.LongLength });
    }
}

sealed class ReadFailureStream(byte[] bytes, int failAfter) : MemoryStream(bytes, writable: false)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= failAfter) return ValueTask.FromException<int>(new IOException("fixture interrupted read"));
        var allowed = (int)Math.Min(buffer.Length, failAfter - Position);
        return base.ReadAsync(buffer[..allowed], cancellationToken);
    }
}

sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset value = now;
    public override DateTimeOffset GetUtcNow() => value;
    public void Advance(TimeSpan by) => value = value.Add(by);
}
