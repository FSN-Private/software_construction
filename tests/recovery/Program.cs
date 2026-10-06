using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;
using Platform.Storage;

try
{
    if (args.Length == 2 && args[0] == "--write-qualification-fixture")
    {
        WriteQualificationFixture(args[1]);
        Console.WriteLine("Recovery qualification test fixture written; it is not cloud evidence.");
        return;
    }
    if (args.Length != 0) throw new InvalidOperationException("usage: recovery-tests [--write-qualification-fixture <directory>]");
    ContractChecks();
    OwnerPointIndexChecks();
    await OwnerPointIndexBootstrapChecksAsync();
    EvidenceContractChecks();
    OwnerAccessEvidenceChecks();
    VerifierContractChecks();
    await ObjectStoreChecksAsync();
    await FenceIntentChecksAsync();
    Console.WriteLine("Recovery contracts, signed evidence, and exact-version storage checks passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Recovery checks failed: {exception.GetType().Name} {exception.Message}");
    Environment.ExitCode = 1;
}

static void WriteQualificationFixture(string directory)
{
    using var rsa = RSA.Create(2048);
    var publicKey = rsa.ExportSubjectPublicKeyInfo();
    var keyId = "fixture-kms-key";
    var fingerprint = RecoveryEvidenceCodec.Hash(publicKey);
    var nowText = Environment.GetEnvironmentVariable("RECOVERY_FIXTURE_NOW_UTC");
    var now = string.IsNullOrWhiteSpace(nowText)
        ? DateTimeOffset.UtcNow
        : DateTimeOffset.Parse(nowText, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    var qualification = Qualification(now) with { SourceRevision = ReadSourceRevision() };
    var pointBytes = RecoveryEvidenceCodec.SerializeCanonical(new
    {
        schemaVersion = 1,
        qualification.Point.PointId,
        qualification.Point.CaptureId,
        qualification.Point.ManifestSha256
    });
    var pointSignature = Sign(rsa, pointBytes, keyId, fingerprint);
    qualification = qualification with
    {
        Point = qualification.Point with
        {
            CheckpointSha256 = RecoveryEvidenceCodec.Hash(pointBytes),
            CheckpointKeyId = keyId,
            CheckpointSignatureBase64 = Convert.ToBase64String(pointSignature.Signature)
        }
    };
    var qualificationSignature = Sign(rsa, RecoveryQualificationCodec.SerializePayload(qualification), keyId, fingerprint);
    var envelope = RecoveryQualificationCodec.CreateEnvelope(qualification, qualificationSignature);
    var envelopeBytes = RecoveryQualificationCodec.SerializeEnvelope(envelope);
    var trust = new RecoverySigningTrust(keyId, Convert.ToBase64String(publicKey), fingerprint);
    var binding = new RecoveryQualificationBindingV1(qualification.SourceRevision,
        qualification.RecoveryTemplateDigest, qualification.ApiBuildDigest, qualification.RecoveryWorkerBuildDigest,
        qualification.IngestWorkerBuildDigest, qualification.RendererBuildDigest, qualification.MigrationDigest,
        qualification.ConfigurationDigest, qualification.OwnerAccessEvidenceSha256);

    var previous = new string('0', 64);
    var checkpointPayload = new RecoveryAuditCheckpointPayloadV1(1, 1, Guid.NewGuid(), previous, now,
        qualification.QualificationId, qualification.RunId, RecoveryEvidenceCodec.Hash(envelopeBytes),
        envelope.PayloadSha256, qualification.Point.PointId, qualification.Point.ManifestSha256,
        qualification.Point.CheckpointSha256);
    var auditSignature = Sign(rsa, RecoveryAuditCheckpointCodec.SerializePayload(checkpointPayload), keyId, fingerprint);
    var auditEnvelope = RecoveryAuditCheckpointCodec.CreateEnvelope(checkpointPayload, auditSignature);
    var auditBytes = RecoveryAuditCheckpointCodec.SerializeArtifact(auditEnvelope);
    var anchor = new RecoveryOfflineAuditAnchorV1(1, keyId, Convert.ToBase64String(publicKey), fingerprint,
        0, previous, 1, RecoveryEvidenceCodec.Hash(auditBytes), now);

    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "recovery-qualification.v1.json"), envelopeBytes);
    File.WriteAllBytes(Path.Combine(directory, "recovery-trust.json"), RecoveryEvidenceCodec.SerializeCanonical(trust));
    File.WriteAllBytes(Path.Combine(directory, "release-binding.json"), RecoveryEvidenceCodec.SerializeCanonical(binding));
    File.WriteAllBytes(Path.Combine(directory, "recovery-audit-checkpoint.v1.json"), auditBytes);
    File.WriteAllBytes(Path.Combine(directory, "recovery-offline-anchor.json"), RecoveryAuditCheckpointCodec.SerializeAnchor(anchor));
}

static string ReadSourceRevision()
{
    var configured = Environment.GetEnvironmentVariable("RECOVERY_FIXTURE_SOURCE_REVISION");
    if (configured is { Length: 40 } && configured.All(Uri.IsHexDigit)) return configured.ToLowerInvariant();
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("fixture_source_revision_unavailable");
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    if (process.ExitCode != 0 || output.Length != 40 || output.Any(character => !Uri.IsHexDigit(character)))
        throw new InvalidOperationException("fixture_source_revision_unavailable");
    return output.ToLowerInvariant();
}

static void OwnerAccessEvidenceChecks()
{
    using var ownerKey = RSA.Create(2048);
    var ownerSpki = ownerKey.ExportSubjectPublicKeyInfo();
    var ownerKeyId = "arn:aws:kms:ap-southeast-2:222222222222:key/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    var ownerFingerprint = RecoveryEvidenceCodec.Hash(ownerSpki);
    var trust = new RecoverySigningTrust(ownerKeyId, Convert.ToBase64String(ownerSpki), ownerFingerprint);
    var now = DateTimeOffset.Parse("2026-10-05T10:20:30Z");
    var digest = new string('a', 64);
    var transcript = new RecoveryOwnerAccessTranscriptV1(1, Guid.NewGuid(), now.AddMinutes(-3),
        now.AddMinutes(-2), digest, 200, RecoveryEvidenceCodec.Hash("owner-a"u8), digest,
        now.AddMinutes(-1), new string('b', 64), 403, new string('c', 64));
    var payload = new RecoveryOwnerAccessEvidenceV1(1, "owner-a", "library-a", digest, digest, "source-revision", digest, digest, digest,
        "ap-southeast-2_abc", "client-123", "https://cognito-idp.ap-southeast-2.amazonaws.com/ap-southeast-2_abc",
        now, now.AddMonths(3), RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(transcript)), transcript);
    var payloadBytes = RecoveryOwnerAccessCodec.SerializePayload(payload);
    var signature = new RecoveryEvidenceSignature(ownerKeyId, ownerFingerprint,
        ownerKey.SignHash(SHA256.HashData(payloadBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    var envelope = RecoveryOwnerAccessCodec.CreateEnvelope(payload, signature);
    var parsed = RecoveryOwnerAccessCodec.ParseEnvelope(RecoveryOwnerAccessCodec.SerializeEnvelope(envelope));
    var expected = new RecoveryOwnerAccessExpectationV1(payload.OwnerId, payload.LibraryId, payload.ConfigurationDigest,
        payload.ApiBuildDigest, payload.SourceRevision, payload.MigrationDigest, payload.RecoveryWorkerBuildDigest, payload.RecoveryTemplateDigest,
        payload.UserPoolId, payload.UserPoolClientId, payload.Issuer);
    var verified = RecoveryOwnerAccessCodec.Verify(parsed, trust, expected, now);
    Check(verified.Payload.Transcript.OwnerHttpStatus == 200 && verified.Payload.Transcript.ForeignHttpStatus == 403,
        "Pinned owner approval attestation binds the actual owner success and foreign denial transcript.");

    RejectInvalid(() => RecoveryOwnerAccessCodec.Verify(parsed, trust, expected, now.AddMonths(3).AddTicks(1)));
    RejectInvalid(() => RecoveryOwnerAccessCodec.Verify(parsed, trust,
        expected with { RecoveryTemplateDigest = new string('f', 64) }, now));
    RejectInvalid(() => RecoveryOwnerAccessCodec.Verify(parsed with { SigningKeyId = "untrusted" }, trust, expected, now));
    RejectInvalid(() => RecoveryOwnerAccessCodec.SerializePayload(payload with { ExpiresAtUtc = now.AddMonths(3).AddTicks(1) }));
    RejectInvalid(() => RecoveryOwnerAccessCodec.SerializePayload(payload with
    {
        Transcript = transcript with { ForeignHttpStatus = 200 }
    }));
}

static void VerifierContractChecks()
{
    var now = DateTimeOffset.UtcNow;
    var sha = new string('a', 64);
    var receipts = Enumerable.Range(0, 100_000).Select(index => new PrivateCopyReceiptV1(
        "primary-content", $"objects/{index:D6}", "source-v1", sha, 1,
        "recovery-payload", $"payload/sha256/{sha}/{index:D6}", "destination-v1",
        RecoveryDataClass.OriginalImage.ToString(), now)).ToArray();
    var expected = receipts.Select(receipt => receipt with { DestinationVersionId = "" }).ToArray();
    RecoveryVerifierRunner.ValidateReceiptSet(receipts, expected);
    Check(true, "Verifier receipt matching handles a 100,000-item exact closure with bounded linear lookups.");
    var duplicate = (PrivateCopyReceiptV1[])receipts.Clone();
    duplicate[^1] = duplicate[^1] with { SourceKey = duplicate[0].SourceKey };
    RejectInvalidData(() => RecoveryVerifierRunner.ValidateReceiptSet(duplicate, expected));
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
    var expectedSnapshotScope = "SNAPSHOT#" + RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes("owner\0library"));
    Check(RecoveryFenceScope.SnapshotScopeId("owner", "library") == expectedSnapshotScope,
        "Snapshot admission and purge use the same exact owner/library fence partition.");
    var logical = new RecoveryLogicalReference("owner", "library", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    var expectedMediaScope = "MEDIA#" + RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes(
        "owner\0library\0" + logical.AssetId!.Value.ToString("D") + "\0original\0original\0" + new string('a', 64)));
    Check(RecoveryFenceScope.MediaScopeId(logical, "original", new string('a', 64)) == expectedMediaScope,
        "Media admission and purge use the same exact logical reference plus content digest.");

    var shuffled = manifest with { Coverage = manifest.Coverage.Reverse().ToArray() };
    Check(shuffled.Digest() == manifest.Digest(), "Manifest order must not alter canonical recovery-point identity.");
    var noncanonical = bytes.Concat(" "u8.ToArray()).ToArray();
    RejectInvalid(() => RecoveryPointManifestV1.ParseCanonical(noncanonical));
}

static void OwnerPointIndexChecks()
{
    var owner = "owner-1";
    var library = "main";
    var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var forward = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, library, [first, second]);
    var reverse = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, library, [second, first]);
    if (forward != reverse || forward == RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, "other", [first, second]) ||
        RecoveryOwnerPointIndex.PartitionKey(owner, library) != "OWNER#" + RecoveryOwnerPointIndex.ScopeHash(owner, library) ||
        RecoveryOwnerPointIndex.PointSortKey(first) != "POINT#00000000-0000-0000-0000-000000000001")
        throw new InvalidOperationException("Owner point index canonicalization failed.");
    ThrowsInvalid(() => RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, library, [first, first]));

    var firstDigest = new string('a', 64);
    var secondDigest = new string('b', 64);
    var thirdDigest = new string('c', 64);
    var bootstrapDigest = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(owner, library, [first, second]);
    var indexed = new RecoveryOwnerPointMembership[]
    {
        new(first, firstDigest, true),
        new(second, secondDigest, true),
        new(Guid.Parse("00000000-0000-0000-0000-000000000003"), thirdDigest, false)
    };
    var discovered = new Dictionary<Guid, string>
    {
        [first] = firstDigest,
        [second] = secondDigest,
        [indexed[2].PointId] = thirdDigest
    };
    RecoveryOwnerPointIndex.ValidateStrongIndex(owner, library, 2, bootstrapDigest, 3, indexed, discovered);
    RejectInvalid(() => RecoveryOwnerPointIndex.ValidateStrongIndex(owner, library, 2, bootstrapDigest, 4, indexed, discovered));
    RejectInvalid(() => RecoveryOwnerPointIndex.ValidateStrongIndex(owner, library, 2, new string('f', 64), 3, indexed, discovered));
    RejectInvalid(() => RecoveryOwnerPointIndex.ValidateStrongIndex(owner, library, 2, bootstrapDigest, 3,
        indexed, new Dictionary<Guid, string> { [first] = firstDigest, [second] = secondDigest }));
    RejectInvalid(() => RecoveryOwnerPointIndex.ValidateStrongIndex(owner, library, 2, bootstrapDigest, 3,
        [.. indexed, indexed[0]], discovered));
}

static async Task OwnerPointIndexBootstrapChecksAsync()
{
    var ownerA = "owner-a";
    var libraryA = "main";
    var ownerB = "owner-b";
    var libraryB = "archive";
    var pointA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    var pointB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    var pointC = Guid.Parse("20000000-0000-0000-0000-000000000003");
    var digestA = new string('a', 64);
    var digestB = new string('b', 64);
    var digestC = new string('c', 64);
    var seed = new[]
    {
        LegacyCopy(pointA, ownerA, libraryA, digestA),
        LegacyCopy(pointB, ownerA, libraryA, digestB),
        LegacyCopy(pointC, ownerB, libraryB, digestC)
    };
    var client = RecoveryIndexDynamoFake.Create(seed, pageSize: 1, out var fake);
    await RecoveryPointIndexBootstrapRunner.BootstrapAsync(client, "control", "owner-empty", "main", CancellationToken.None);
    Check(fake.ScanCalls >= seed.Length && fake.QueryCalls >= 3,
        "Index bootstrap follows every small Scan and strongly consistent Query page.");

    var markerA = fake.Read(RecoveryOwnerPointIndex.PartitionKey(ownerA, libraryA), "INDEX")!;
    var markerB = fake.Read(RecoveryOwnerPointIndex.PartitionKey(ownerB, libraryB), "INDEX")!;
    var emptyMarker = fake.Read(RecoveryOwnerPointIndex.PartitionKey("owner-empty", "main"), "INDEX")!;
    Check(markerA["status"].S == "complete" && markerA["bootstrapPointCount"].N == "2" &&
          markerA["indexedPointCount"].N == "2" &&
          markerA["bootstrapPointSetSha256"].S == RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(ownerA, libraryA, [pointA, pointB]),
        "Bootstrap stores the discovered baseline membership count and canonical digest.");
    Check(markerB["bootstrapPointCount"].N == "1" && markerB["indexedPointCount"].N == "1" &&
          emptyMarker["bootstrapPointCount"].N == "0" && emptyMarker["indexedPointCount"].N == "0",
        "Bootstrap indexes all discovered scopes and only an explicitly configured empty scope.");

    var newPoint = Guid.Parse("10000000-0000-0000-0000-000000000004");
    var newActions = RecoveryPointIndexBootstrapRunner.BuildPointIndexAdmissionActions("control", ownerA,
        libraryA, newPoint, new string('d', 64), alreadyIndexed: false);
    var combined = CopyRunner.BuildPointLeaseAdmissionTransactionItems(new Update
    {
        TableName = "control",
        Key = RecoveryPointIndexBootstrapRunner.OwnerPointKey(ownerA, libraryA, newPoint),
        UpdateExpression = "SET leaseToken=:token"
    }, newActions);
    Check(combined.Count == 3 && combined[0].Update is not null && combined[1].Put is not null &&
          combined[1].Put.ConditionExpression == "attribute_not_exists(pk)" &&
          combined[2].Update?.UpdateExpression == "ADD indexedPointCount :one",
        "New copy lease, immutable owner membership, and marker increment are one three-action transaction.");

    var existingActions = RecoveryPointIndexBootstrapRunner.BuildPointIndexAdmissionActions("control", ownerA,
        libraryA, pointA, digestA, alreadyIndexed: true);
    Check(existingActions.Count == 2 && existingActions.All(item => item.ConditionCheck is not null) &&
          existingActions.All(item => item.Update is null && item.Put is null),
        "Replay of an indexed point checks its marker and exact membership without incrementing the count.");

    var missingClient = RecoveryIndexDynamoFake.Create([], pageSize: 1, out var missingFake);
    await RejectAsync<RecoveryWorkerException>(() => RecoveryPointIndexBootstrapRunner.BuildNewPointAdmissionAsync(
        missingClient, "control", ownerA, libraryA, pointA, digestA, CancellationToken.None));
    Check(missingFake.ScanCalls == 0,
        "Copy admission fails closed on a missing marker and never falls back to bootstrap Scan.");

    fake.Remove(RecoveryOwnerPointIndex.PartitionKey(ownerA, libraryA), RecoveryOwnerPointIndex.PointSortKey(pointA));
    await RejectAsync<RecoveryWorkerException>(() => RecoveryPointIndexBootstrapRunner.BootstrapAsync(
        client, "control", "owner-empty", "main", CancellationToken.None));
}

static Dictionary<string, AttributeValue> LegacyCopy(Guid pointId, string ownerId, string libraryId, string digest) => new()
{
    ["pk"] = new AttributeValue { S = "POINT#" + pointId.ToString("D") },
    ["sk"] = new AttributeValue { S = "COPY" },
    ["pointId"] = new AttributeValue { S = pointId.ToString("D") },
    ["ownerId"] = new AttributeValue { S = ownerId },
    ["libraryId"] = new AttributeValue { S = libraryId },
    ["sourceManifestSha256"] = new AttributeValue { S = digest },
    ["state"] = new AttributeValue { S = "copied" }
};

static void EvidenceContractChecks()
{
    using var rsa = RSA.Create(2048);
    var publicKey = rsa.ExportSubjectPublicKeyInfo();
    var keyId = "arn:aws:kms:ap-southeast-2:222222222222:key/11111111-1111-1111-1111-111111111111";
    var fingerprint = RecoveryEvidenceCodec.Hash(publicKey);
    var trust = new RecoverySigningTrust(keyId, Convert.ToBase64String(publicKey), fingerprint);
    var now = DateTimeOffset.Parse("2026-10-05T10:20:30Z");

    var catalog = Catalog(now);
    var catalogBytes = RecoveryCatalogCodec.SerializePayload(catalog);
    var catalogSignature = Sign(rsa, catalogBytes, keyId, fingerprint);
    var catalogEnvelope = RecoveryCatalogCodec.CreateEnvelope(catalog, "recovery-copy", catalogSignature);
    var catalogEnvelopeBytes = RecoveryCatalogCodec.SerializeEnvelope(catalogEnvelope);
    Check(catalogEnvelopeBytes.Length < 16 * 1024, "Safe point catalog stays bounded independently of object inventory size.");
    var parsedCatalogEnvelope = RecoveryCatalogCodec.ParseEnvelope(catalogEnvelopeBytes);
    var verifiedCatalog = RecoveryCatalogCodec.Verify(parsedCatalogEnvelope, trust);
    Check(verifiedCatalog.Payload.FullManifestSha256 != verifiedCatalog.PayloadSha256,
        "Safe catalog digest and full private manifest digest are separate signed identities.");
    using (var catalogDocument = JsonDocument.Parse(verifiedCatalog.CanonicalPayloadBytes))
    {
        var root = catalogDocument.RootElement;
        Check(!root.TryGetProperty("objects", out _) && !root.TryGetProperty("sourceKey", out _) &&
            !root.TryGetProperty("destinationKey", out _), "Mirrored catalog contains no per-object locators or closure.");
        Check(root.GetProperty("coverage").GetArrayLength() == RecoveryEvidenceCodec.ObservationClasses.Length,
            "Mirrored catalog includes only fixed-size per-class coverage.");
    }
    RejectInvalid(() => RecoveryCatalogCodec.Verify(parsedCatalogEnvelope with { SafeCatalogPayloadSha256 = new string('0', 64) }, trust));
    RejectInvalid(() => RecoveryCatalogCodec.Verify(parsedCatalogEnvelope with { SigningKeyId = "untrusted" }, trust));
    RejectInvalid(() => RecoveryCatalogCodec.SerializePayload(catalog with { Coverage = catalog.Coverage.Reverse().ToArray() }));
    RejectInvalid(() => RecoveryCatalogCodec.ParseEnvelope(catalogEnvelopeBytes.Concat(" "u8.ToArray()).ToArray()));

    var observation = new RecoveryObservationPayloadV1(1, Guid.NewGuid(), catalog.RecoveryPointId, catalog.OwnerId,
        catalog.LibraryId, "originals", "healthy", now, 1, 1, 16, 16, catalog.FullManifestSha256, [], null);
    var observationBytes = observation.SerializeCanonical();
    var observationEnvelope = RecoveryEvidenceCodec.CreateObservationEnvelope(
        observation, "recovery-copy", Sign(rsa, observationBytes, keyId, fingerprint));
    Check(RecoveryEvidenceCodec.ParseObservationEnvelope(RecoveryEvidenceCodec.SerializeObservationEnvelope(observationEnvelope)).MessageType == "observation",
        "Observation queue envelope is explicitly discriminated.");
    Check(RecoveryEvidenceCodec.VerifyObservation(observationEnvelope, trust).Payload.ObservedItemCount == 1,
        "Signed per-class observations verify against the pinned SPKI key.");
    RejectInvalid(() => RecoveryEvidenceCodec.VerifyObservation(observationEnvelope with { SigningPublicKeySha256 = new string('0', 64) }, trust));

    var cycle = new RecoveryCycleEventV1(1, Guid.NewGuid(), catalog.CaptureId, catalog.RecoveryPointId,
        catalog.OwnerId, catalog.LibraryId, now, "verification", "xmp", "corrupt", "checksum_mismatch",
        catalog.RecoveryWorkerBuildDigest);
    var cycleBytes = RecoveryCycleCodec.SerializePayload(cycle);
    var cycleEnvelope = RecoveryCycleCodec.CreateEnvelope(cycle, "recovery-copy", Sign(rsa, cycleBytes, keyId, fingerprint));
    Check(RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(RecoveryCycleCodec.SerializeEnvelope(cycleEnvelope)), trust).Payload.SafeCode == "checksum_mismatch",
        "Signed failure summaries are verifiable without a preceding successful recovery point.");
    RejectInvalid(() => RecoveryCycleCodec.SerializePayload(cycle with { SafeCode = "private/path" }));

    var qualification = Qualification(now);
    var checkpointPayloadBytes = RecoveryEvidenceCodec.SerializeCanonical(new
    {
        schemaVersion = 1,
        qualification.Point.PointId,
        qualification.Point.CaptureId,
        qualification.Point.ManifestSha256
    });
    var checkpointSignature = Sign(rsa, checkpointPayloadBytes, keyId, fingerprint);
    qualification = qualification with
    {
        Point = qualification.Point with
        {
            CheckpointSha256 = RecoveryEvidenceCodec.Hash(checkpointPayloadBytes),
            CheckpointKeyId = keyId,
            CheckpointSignatureBase64 = Convert.ToBase64String(checkpointSignature.Signature)
        }
    };
    var qualificationBytes = RecoveryQualificationCodec.SerializePayload(qualification);
    var qualificationEnvelope = RecoveryQualificationCodec.CreateEnvelope(qualification,
        Sign(rsa, qualificationBytes, keyId, fingerprint));
    var verifiedQualification = RecoveryQualificationCodec.Verify(qualificationEnvelope, trust);
    var binding = new RecoveryQualificationBindingV1(qualification.SourceRevision,
        qualification.RecoveryTemplateDigest, qualification.ApiBuildDigest, qualification.RecoveryWorkerBuildDigest,
        qualification.IngestWorkerBuildDigest, qualification.RendererBuildDigest, qualification.MigrationDigest,
        qualification.ConfigurationDigest, qualification.OwnerAccessEvidenceSha256);
    RecoveryQualificationReleaseGate.RequireReady(verifiedQualification, trust, binding, now);
    var previousCheckpointHash = new string('0', 64);
    var checkpointPayload = new RecoveryAuditCheckpointPayloadV1(1, 1, Guid.NewGuid(), previousCheckpointHash,
        now, qualification.QualificationId, qualification.RunId, RecoveryEvidenceCodec.Hash(RecoveryQualificationCodec.SerializeEnvelope(qualificationEnvelope)),
        verifiedQualification.PayloadSha256, qualification.Point.PointId, qualification.Point.ManifestSha256,
        qualification.Point.CheckpointSha256);
    var checkpointBytes = RecoveryAuditCheckpointCodec.SerializePayload(checkpointPayload);
    var auditEnvelope = RecoveryAuditCheckpointCodec.CreateEnvelope(checkpointPayload, Sign(rsa, checkpointBytes, keyId, fingerprint));
    var auditArtifactBytes = RecoveryAuditCheckpointCodec.SerializeArtifact(auditEnvelope);
    var auditAnchor = new RecoveryOfflineAuditAnchorV1(1, keyId, Convert.ToBase64String(publicKey), fingerprint,
        0, previousCheckpointHash, 1, RecoveryEvidenceCodec.Hash(auditArtifactBytes), now);
    var verifiedAudit = RecoveryAuditCheckpointCodec.Verify(
        RecoveryAuditCheckpointCodec.ParseArtifact(auditArtifactBytes).Checkpoint, trust);
    RecoveryAuditCheckpointCodec.RequireQualificationBinding(verifiedAudit, auditAnchor, verifiedQualification,
        RecoveryEvidenceCodec.Hash(RecoveryQualificationCodec.SerializeEnvelope(qualificationEnvelope)), now);
    RejectInvalid(() => RecoveryAuditCheckpointCodec.RequireQualificationBinding(verifiedAudit,
        auditAnchor with { HeadCheckpointSha256 = new string('f', 64) }, verifiedQualification,
        RecoveryEvidenceCodec.Hash(RecoveryQualificationCodec.SerializeEnvelope(qualificationEnvelope)), now));
    RejectInvalid(() => RecoveryAuditCheckpointCodec.ParseAnchor(RecoveryAuditCheckpointCodec.SerializeAnchor(auditAnchor)
        .Concat(" "u8.ToArray()).ToArray()));
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(verifiedQualification, trust,
        binding with { ApiBuildDigest = new string('f', 64) }, now));
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(verifiedQualification, trust, binding, now.AddMonths(4)));
    var wrongRegion = qualification with { Recovery = qualification.Recovery with { Region = "us-east-1" } };
    var wrongRegionBytes = RecoveryEvidenceCodec.SerializeCanonical(wrongRegion);
    var wrongRegionSignature = Sign(rsa, wrongRegionBytes, keyId, fingerprint);
    var signedWrongRegion = new VerifiedRecoveryQualification(wrongRegion, wrongRegionBytes,
        RecoveryEvidenceCodec.Hash(wrongRegionBytes), keyId, fingerprint, wrongRegionSignature.Signature);
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(signedWrongRegion, trust, binding, now));
    var falseRpo = qualification with { Restore = qualification.Restore with { RpoSeconds = 1 } };
    var falseRpoEnvelope = RecoveryQualificationCodec.CreateEnvelope(falseRpo,
        Sign(rsa, RecoveryQualificationCodec.SerializePayload(falseRpo), keyId, fingerprint));
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(
        RecoveryQualificationCodec.Verify(falseRpoEnvelope, trust), trust, binding, now));
    var undersized = qualification with { Workload = qualification.Workload with { ImageCount = 1 } };
    var undersizedEnvelope = RecoveryQualificationCodec.CreateEnvelope(undersized,
        Sign(rsa, RecoveryQualificationCodec.SerializePayload(undersized), keyId, fingerprint));
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(
        RecoveryQualificationCodec.Verify(undersizedEnvelope, trust), trust, binding, now));
    var unresolved = qualification with { UnresolvedBlockingCorrections = ["restore_auth_unproven"] };
    var unresolvedEnvelope = RecoveryQualificationCodec.CreateEnvelope(unresolved,
        Sign(rsa, RecoveryQualificationCodec.SerializePayload(unresolved), keyId, fingerprint));
    RejectInvalid(() => RecoveryQualificationReleaseGate.RequireReady(
        RecoveryQualificationCodec.Verify(unresolvedEnvelope, trust), trust, binding, now));
}

static RecoveryEvidenceSignature Sign(RSA rsa, byte[] bytes, string keyId, string fingerprint) => new(
    keyId, fingerprint, rsa.SignHash(SHA256.HashData(bytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

static RecoveryCatalogPayloadV1 Catalog(DateTimeOffset now)
{
    var coverage = RecoveryEvidenceCodec.ObservationClasses.Select(dataClass => new RecoveryCatalogCoverageV1(
        dataClass, dataClass == "originals" ? 1 : 0, dataClass == "originals" ? 1 : 0,
        dataClass == "originals" ? 16 : 0, dataClass == "originals" ? 16 : 0, "healthy", [])).ToArray();
    return new RecoveryCatalogPayloadV1(1, Guid.NewGuid(), Guid.NewGuid(), "owner-a", "library-a",
        "111111111111", "ap-southeast-1", "222222222222", "ap-southeast-2",
        now, now.AddMinutes(-1), "00000003-0000001B-1", "16/B374D848", 3, now.AddMinutes(-2),
        new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
        new string('e', 64), new string('f', 64), new string('1', 64), new string('2', 64),
        now.AddMinutes(5), "copy-build", coverage);
}

static RecoveryQualificationV1 Qualification(DateTimeOffset now)
{
    var classes = RecoveryEvidenceCodec.ObservationClasses.Select(dataClass => new RecoveryQualifiedClassCoverageV1(
        dataClass, dataClass == "originals" ? 1 : 0, dataClass == "originals" ? 1 : 0,
        dataClass == "originals" ? 16 : 0, dataClass == "originals" ? 16 : 0, "healthy")).ToArray();
    var digest = new string('a', 64);
    var threshold = now.AddMinutes(-20);
    return new RecoveryQualificationV1(1, Guid.NewGuid(), Guid.NewGuid(), now, "f".PadLeft(40, '0'),
        digest, digest, digest, digest, digest, digest, digest, digest,
        new("111111111111", "primary-alias", "ap-southeast-1"),
        new("222222222222", "recovery-alias", "ap-southeast-2"),
        new(Guid.NewGuid(), Guid.NewGuid(), digest, digest, "checkpoint-key", Convert.ToBase64String([1, 2, 3]),
            now.AddMinutes(-40), "16/B374D848", 3, now.AddHours(-3), classes),
        new(100_000, 1_000_000, 100_000_000, 100_000_000, "approved-budget", "approval-1"),
        new(true, now.AddMinutes(-31), digest),
        new(now.AddMinutes(-30), now.AddMinutes(-29), now.AddMinutes(-10), 600, 1200, digest),
        new(true, true, true, true, true, true, true, true, true, true, true),
        [new(Guid.NewGuid(), threshold, threshold.AddSeconds(10), threshold.AddMinutes(10), "delivered", digest)],
        new(90, "GLACIER_IR", now.AddMinutes(-8), true),
        new(true, true, true), [], []);
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

    byte[] artifact = [3, 1, 4, 1, 5, 9];
    var artifactHash = Address(artifact).Sha256;
    source.Add("evidence", "points/point/source-manifest", "manifest-v1", artifact);
    var exactArtifact = await RecoveryVerifierRunner.ReadExactBytesAsync(source, "evidence",
        "points/point/source-manifest", "manifest-v1", artifactHash, artifact.LongLength, 1024,
        CancellationToken.None);
    Check(exactArtifact.SequenceEqual(artifact), "Verifier reads and hashes the requested exact S3 version successfully.");
    source.ReturnedVersionId = "manifest-v2";
    await RejectAsync<RecoveryWorkerException>(() => RecoveryVerifierRunner.ReadExactBytesAsync(source, "evidence",
        "points/point/source-manifest", "manifest-v1", artifactHash, artifact.LongLength, 1024,
        CancellationToken.None));
    source.ReturnedVersionId = null;
    await RejectAsync<RecoveryWorkerException>(() => RecoveryVerifierRunner.ReadExactBytesAsync(source, "evidence",
        "points/point/source-manifest", "manifest-v1", new string('0', 64), artifact.LongLength, 1024,
        CancellationToken.None));

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

static async Task FenceIntentChecksAsync()
{
    var source = new RecoveryObjectReference(RecoveryDataClass.DatabaseSnapshot, "owner", "library",
        "capture/snapshot.dump", "source-v1", new string('a', 64), 123, []);
    var pointId = Guid.NewGuid();
    var scopeId = RecoveryFenceScope.SnapshotScopeId("owner", "library");

    var admittedStore = new TestDynamo();
    var admitted = await new DynamoFenceIntentStore(admittedStore, "fences", "intents").AdmitComponentAsync(
        pointId, source, "capture", "payload", "snapshots/point.dump", "worker",
        DateTimeOffset.UtcNow, CancellationToken.None);
    var transaction = admittedStore.LastTransaction?.TransactItems
        ?? throw new InvalidOperationException("Expected snapshot fence transaction.");
    Check(admitted.ScopeId == scopeId && transaction?.Count == 2 &&
        transaction.Count(item => item.ConditionCheck is not null) == 1 &&
        transaction.Count(item => item.Put is not null) == 1 &&
        transaction.Single(item => item.ConditionCheck is not null).ConditionCheck!.TableName == "fences" &&
        transaction.Single(item => item.ConditionCheck is not null).ConditionCheck!.Key["scopeId"].S == scopeId &&
        transaction.Single(item => item.ConditionCheck is not null).ConditionCheck!.ConditionExpression == "attribute_not_exists(recordId)",
        "Database snapshot admission atomically checks the library fence while persisting the write intent.");

    var preexistingFence = new TestDynamo();
    preexistingFence.Fences.Add(scopeId);
    await RejectAsync<RecoveryWorkerException>(() => new DynamoFenceIntentStore(preexistingFence, "fences", "intents")
        .AdmitComponentAsync(pointId, source, "capture", "payload", "snapshots/point.dump", "worker",
            DateTimeOffset.UtcNow, CancellationToken.None));
    Check(preexistingFence.TransactionCount == 0,
        "A library purge fence blocks new snapshot writes before the provider transaction.");

    var racingFence = new TestDynamo { AddFenceDuringNextTransaction = scopeId };
    await RejectAsync<RecoveryWorkerException>(() => new DynamoFenceIntentStore(racingFence, "fences", "intents")
        .AdmitComponentAsync(pointId, source, "capture", "payload", "snapshots/point.dump", "worker",
            DateTimeOffset.UtcNow, CancellationToken.None));
    Check(racingFence.TransactionCount == 1 && racingFence.Fences.Contains(scopeId),
        "The transaction condition rejects a purge fence added after the preliminary read.");
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

static void RejectInvalidData(Action action)
{
    try { action(); }
    catch (RecoveryWorkerException) { return; }
    throw new InvalidOperationException("Expected invalid recovery worker input.");
}

static void RejectInvalid(Action action, [CallerLineNumber] int line = 0)
{
    if (!ThrowsInvalid(action)) throw new InvalidOperationException($"Expected InvalidDataException at test line {line}.");
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
        BlockPublicAcls = true,
        IgnorePublicAcls = true,
        BlockPublicPolicy = true,
        RestrictPublicBuckets = true
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

sealed class TestDynamo : AmazonDynamoDBClient
{
    public TestDynamo() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:1" }) { }

    public HashSet<string> Fences { get; } = new(StringComparer.Ordinal);
    public string? AddFenceDuringNextTransaction { get; set; }
    public int TransactionCount { get; private set; }
    public TransactWriteItemsRequest? LastTransaction { get; private set; }

    public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
    {
        var scopeId = request.Key.TryGetValue("scopeId", out var scope) ? scope.S : null;
        var recordId = request.Key.TryGetValue("recordId", out var record) ? record.S : null;
        var item = request.TableName == "fences" && recordId == "FENCE" && scopeId is not null && Fences.Contains(scopeId)
            ? new Dictionary<string, AttributeValue> { ["scopeId"] = new() { S = scopeId }, ["recordId"] = new() { S = "FENCE" } }
            : new Dictionary<string, AttributeValue>();
        return Task.FromResult(new GetItemResponse { Item = item });
    }

    public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
        TransactWriteItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        TransactionCount++;
        LastTransaction = request;
        if (AddFenceDuringNextTransaction is { } scopeId)
        {
            Fences.Add(scopeId);
            AddFenceDuringNextTransaction = null;
        }
        foreach (var item in request.TransactItems)
        {
            if (item.ConditionCheck is { TableName: "fences" } condition &&
                condition.Key.TryGetValue("scopeId", out var scope) && scope.S is { } value && Fences.Contains(value))
                throw new TransactionCanceledException("fixture fence condition failed");
        }
        return Task.FromResult(new TransactWriteItemsResponse());
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
