using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryLatestPointDiscoveryChecks
{
    public static async Task RunAsync()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        const string signingKeyId = "latest-discovery-test-key";
        var fingerprint = RecoveryEvidenceCodec.Hash(publicKey);
        var trust = new RecoverySigningTrust(signingKeyId, Convert.ToBase64String(publicKey), fingerprint);
        var factory = new FixtureFactory(rsa, trust, signingKeyId, fingerprint);
        var first = factory.Create(1, DateTimeOffset.Parse("2026-10-07T01:00:00Z"), 96, "access_incomplete");
        var second = factory.Create(2, DateTimeOffset.Parse("2026-10-07T01:15:00Z"), 97, "copied");

        var largeHistory = new LatestDynamo(factory.Rows(first, historyCount: 8_639));
        var s3 = factory.S3(first);
        var latest = await RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(largeHistory.Client, s3,
            "control", FixtureFactory.Owner, FixtureFactory.Library, trust, CancellationToken.None);
        Check(latest.Pointer.PointId == first.PointId && latest.Pointer.State == "access_incomplete" &&
              latest.Manifest.ChangeWatermark.LastSequence == 96,
            "The sealed head admits a signed seed restore before owner access proof exists.");
        var initialMarker = largeHistory.Read(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner,
            FixtureFactory.Library), "INDEX")!;
        Check(initialMarker.ContainsKey("latestSealedPointId") && !initialMarker.ContainsKey("latestCopiedPointId"),
            "The bootstrap candidate is not mislabeled as a fully copied receipt-reuse head.");
        Check(largeHistory.GetCalls == 4 && largeHistory.QueryCalls == 0 && largeHistory.ScanCalls == 0 &&
              s3.GetCalls == 2,
            "Discovery request count is constant with 8,640 indexed points and reads only the latest checkpoint and manifest.");
        Check(largeHistory.GetKeys.Select(key => key.Sk).SequenceEqual(
                ["INDEX", RecoveryOwnerPointIndex.PointSortKey(first.PointId), "COPY", "INDEX"]),
            "The bounded read touches only the index head, exact membership, exact copy pointer, and head recheck.");

        var selectedDatabase = new LatestDynamo(factory.Rows(first, second));
        selectedDatabase.ReplaceIndex(factory.Index(second, 2));
        var selected = await RecoveryLatestPointDiscovery.ReadIndexedPointAsync(selectedDatabase.Client,
            factory.S3(first, second), "control", FixtureFactory.Owner, FixtureFactory.Library, first.PointId,
            trust, CancellationToken.None);
        Check(selected.Pointer.PointId == first.PointId && selectedDatabase.GetCalls == 3 &&
            selectedDatabase.QueryCalls == 0 && selectedDatabase.ScanCalls == 0 && selectedDatabase.UpdateCalls == 0,
            "Explicit historical-point discovery followed the latest head or enumerated/wrote history.");
        var historyNames = new[] { "Environment", "RecoveryWorkerBuildDigest", "OwnerId", "LibraryId", "ControlTableName", "MigrationDigest",
            "IngestWorkerBuildDigest", "RendererBuildDigest", "SourceRevision", "ConfigurationDigest", "ApiBuildDigest", "DatabaseRootCertificateSha256" };
        var priorHistoryEnvironment = historyNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        try
        {
            var digest = new string('a', 64);
            foreach (var (name, value) in new[] { ("Environment", "fixture"), ("RecoveryWorkerBuildDigest", digest),
                ("OwnerId", FixtureFactory.Owner), ("LibraryId", FixtureFactory.Library), ("ControlTableName", "control"), ("MigrationDigest", digest),
                ("IngestWorkerBuildDigest", new string('d', 64)), ("RendererBuildDigest", new string('e', 64)), ("SourceRevision", "revision"),
                ("ConfigurationDigest", digest), ("ApiBuildDigest", digest), ("DatabaseRootCertificateSha256", digest) })
                Environment.SetEnvironmentVariable("Recovery__" + name, value);
            Dictionary<string, string> Pins(string migration) => new()
            {
                ["ConfigurationDigest"] = digest,
                ["ApiBuildDigest"] = digest,
                ["SourceRevision"] = "revision",
                ["MigrationDigest"] = migration,
                ["RecoveryWorkerBuildDigest"] = digest,
                ["RecoveryTemplateDigest"] = digest,
                ["SourceTemplateDigest"] = digest,
                ["OwnerUserPoolId"] = "pool",
                ["OwnerUserPoolClientId"] = "client",
                ["OwnerUserPoolIssuer"] = "https://issuer.invalid",
                ["MigrationDirectory"] = "private-migrations",
                ["IngestWorkerBuildDigest"] = digest,
                ["RendererBuildDigest"] = digest,
                ["DatabaseRootCertificateSha256"] = digest
            };
            var bindings = new[] { new RecoveryLifecycleHistoryBinding(first.PointId, Pins(digest)),
                new RecoveryLifecycleHistoryBinding(second.PointId, Pins(new string('b', 64))) };
            var config = RecoveryWorkerConfiguration.Load("hold-release");
            var scoped = new RecoveryWorkerConfiguration("hold-release", bindings[1].Pins);
            Check(config.RequiredHash("MigrationDigest") != scoped.RequiredHash("MigrationDigest") && scoped.Required("OwnerId") == FixtureFactory.Owner,
                "Historical pins changed owner authority or mutated the runtime configuration");
            var historicalConfiguration = new RecoveryWorkerConfiguration("hold-release", bindings[0].Pins);
            var historicalRelease = new CaptureReleaseBindingV1(1, digest, digest, digest, digest, "revision");
            var historicalSource = new CaptureConfigurationV1(1, "fixture", "revision", "111111111111", "ap-southeast-1", digest, digest, digest);
            CopyRunner.ValidateBuildBinding(historicalConfiguration, first.Manifest with { MigrationDigest = digest },
                historicalRelease, historicalSource);
            await ExpectSafeCode(() =>
            {
                CopyRunner.ValidateBuildBinding(config, first.Manifest with { MigrationDigest = digest }, historicalRelease, historicalSource);
                return Task.CompletedTask;
            }, "release_binding_mismatch");
            var currentRelease = historicalRelease with { IngestWorkerBuildDigest = new string('d', 64), RendererBuildDigest = new string('e', 64) };
            CopyRunner.ValidateBuildBinding(config, first.Manifest with { MigrationDigest = digest }, currentRelease, historicalSource);
            await ExpectSafeCode(() =>
            {
                CopyRunner.ValidateBuildBinding(historicalConfiguration, first.Manifest with { MigrationDigest = digest }, currentRelease, historicalSource);
                return Task.CompletedTask;
            }, "release_binding_mismatch");
            var historyDatabase = new LatestDynamo(factory.Rows(first, second));
            historyDatabase.ReplaceIndex(factory.Index(second, 2));
            var inventory = await RecoveryLifecycleHistoryBindings.ReadCompleteInventoryAsync(config,
                historyDatabase.Client, factory.S3(first, second), trust, bindings, default);
            Check(inventory.Count == 2 && historyDatabase.QueryCalls == 0 && historyDatabase.ScanCalls == 0 && historyDatabase.UpdateCalls == 0,
                "Lifecycle history exceeded verifier GetItem-only authority");
            await ExpectSafeCode(() => RecoveryLifecycleHistoryBindings.ReadCompleteInventoryAsync(config,
                historyDatabase.Client, factory.S3(first, second), trust, [bindings[0]], default), "hold_history_inventory_incomplete");
            await ExpectSafeCode(() => RecoveryLifecycleHistoryBindings.ReadCompleteInventoryAsync(config,
                historyDatabase.Client, factory.S3(first, second), trust, [bindings[0], bindings[0]], default), "hold_history_bindings_invalid");
            var forbidden = bindings[0] with { Pins = new(bindings[0].Pins) { ["OwnerId"] = "foreign" } };
            await ExpectSafeCode(() => { RecoveryLifecycleHistoryBindings.Validate([forbidden]); return Task.CompletedTask; },
                "hold_history_bindings_invalid");
        }
        finally
        {
            foreach (var name in historyNames) Environment.SetEnvironmentVariable("Recovery__" + name, priorHistoryEnvironment[name]);
        }
        var noMember = new LatestDynamo(factory.Rows(first));
        noMember.Remove(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner, FixtureFactory.Library), RecoveryOwnerPointIndex.PointSortKey(first.PointId));
        var noMemberS3 = factory.S3(first);
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadIndexedPointAsync(noMember.Client,
            noMemberS3, "control", FixtureFactory.Owner, FixtureFactory.Library, first.PointId, trust, CancellationToken.None),
            "selected_copy_membership_missing_or_foreign");
        Check(noMemberS3.GetCalls == 0 && noMember.GetCalls == 2,
            "Missing/foreign selected membership caused payload reads or cross-owner discovery.");
        RecoveryLatestPointDiscovery.RequireSelectedPoint(first.PointId, first.PointId);
        RecoveryLatestPointDiscovery.RequireSelectedPoint(null, second.PointId);
        try
        {
            RecoveryLatestPointDiscovery.RequireSelectedPoint(second.PointId, first.PointId);
            throw new InvalidOperationException("Selected-point change reused an unrelated completed/launch journal.");
        }
        catch (RecoveryWorkerException error) when (error.SafeCode == "drill_run_selected_point_conflict") { }

        var future = factory.Create(3, DateTimeOffset.UtcNow.AddYears(1), 98, "copied");
        var futureDynamo = new LatestDynamo(factory.Rows(future));
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(futureDynamo.Client,
            factory.S3(future), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None), "recovery_point_time_in_future");
        Check(futureDynamo.UpdateCalls == 0,
            "A signed primary timestamp cannot poison or advance the recovery latest-point head.");

        var staleMembership = new LatestDynamo(factory.Rows(first));
        var badMembership = staleMembership.Read(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner,
            FixtureFactory.Library), RecoveryOwnerPointIndex.PointSortKey(first.PointId))!;
        badMembership["sourceManifestSha256"] = S(new string('b', 64));
        staleMembership.Replace(badMembership);
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(staleMembership.Client,
            factory.S3(first), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None), "latest_copy_membership_binding_mismatch");
        Check(staleMembership.GetCalls == 2, "A stale owner membership is rejected before any S3 read.");

        var missingCopy = new LatestDynamo(factory.Rows(first));
        missingCopy.Remove("POINT#" + first.PointId.ToString("D").ToLowerInvariant(), "COPY");
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(missingCopy.Client,
            factory.S3(first), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None), "scope_index_point_missing");

        var foreignCopy = new LatestDynamo(factory.Rows(first));
        var alteredCopy = foreignCopy.Read("POINT#" + first.PointId.ToString("D").ToLowerInvariant(), "COPY")!;
        alteredCopy["ownerId"] = S("foreign-owner");
        foreignCopy.Replace(alteredCopy);
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(foreignCopy.Client,
            factory.S3(first), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None), "scope_index_point_binding_mismatch");
        Check(foreignCopy.GetCalls == 3, "A foreign point pointer is rejected before any S3 read.");

        var retryDynamo = new LatestDynamo(factory.Rows(first, second));
        var indexReads = 0;
        retryDynamo.BeforeGet = request =>
        {
            if (IsIndex(request) && ++indexReads == 2) retryDynamo.ReplaceIndex(factory.Index(second, 2));
        };
        var afterRace = await RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(retryDynamo.Client,
            factory.S3(first, second), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None);
        Check(afterRace.Pointer.PointId == second.PointId && retryDynamo.GetCalls == 8 &&
              retryDynamo.QueryCalls == 0 && retryDynamo.ScanCalls == 0,
            "A changed head is retried once and converges on the newly sealed point with a fixed eight Gets.");

        var unstableDynamo = new LatestDynamo(factory.Rows(first, second));
        indexReads = 0;
        unstableDynamo.BeforeGet = request =>
        {
            if (!IsIndex(request)) return;
            indexReads++;
            if (indexReads == 2) unstableDynamo.ReplaceIndex(factory.Index(second, 2));
            else if (indexReads == 4) unstableDynamo.ReplaceIndex(factory.Index(first, 2));
        };
        await ExpectSafeCode(() => RecoveryLatestPointDiscovery.ReadLatestSealedPointAsync(unstableDynamo.Client,
            factory.S3(first, second), "control", FixtureFactory.Owner, FixtureFactory.Library, trust,
            CancellationToken.None), "latest_copy_head_unstable");
        Check(unstableDynamo.GetCalls == 8 && unstableDynamo.QueryCalls == 0 && unstableDynamo.ScanCalls == 0,
            "Repeated head movement fails closed after a fixed retry bound.");

        var writable = new LatestDynamo(factory.Rows(first));
        var emptyIndex = factory.Index(first);
        foreach (var name in new[] { "latestSealedPointId", "latestSealedSourceManifestSha256", "latestSealedSnapshotCutoffUtc", "latestSealedChangeSequence" })
            emptyIndex.Remove(name);
        foreach (var name in new[] { "latestCopiedPointId", "latestSourceManifestSha256", "latestSnapshotCutoffUtc", "latestChangeSequence" })
            emptyIndex.Remove(name);
        writable.ReplaceIndex(emptyIndex);
        await RecoveryLatestPointDiscovery.AdvanceLatestSealedPointAsync(writable.Client, "control",
            first.Manifest, first.ManifestSha256, first.Manifest.CapturedAtUtc.AddMinutes(1), CancellationToken.None);
        await RecoveryLatestPointDiscovery.AdvanceLatestSealedPointAsync(writable.Client, "control",
            second.Manifest, second.ManifestSha256, second.Manifest.CapturedAtUtc.AddMinutes(1), CancellationToken.None);
        await RecoveryLatestPointDiscovery.AdvanceLatestSealedPointAsync(writable.Client, "control",
            first.Manifest, first.ManifestSha256, first.Manifest.CapturedAtUtc.AddMinutes(1), CancellationToken.None);
        var advanced = writable.Read(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner,
            FixtureFactory.Library), "INDEX")!;
        Check(advanced["latestSealedPointId"].S == second.PointId.ToString("D").ToLowerInvariant() &&
              !advanced.ContainsKey("latestCopiedPointId"),
            "The sealed/deferred head uses chronological CAS without claiming copy-reuse eligibility.");

        var crashRepair = new LatestDynamo(factory.Rows(first));
        var crashIndex = crashRepair.Read(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner,
            FixtureFactory.Library), "INDEX")!;
        foreach (var name in new[] { "latestSealedPointId", "latestSealedSourceManifestSha256",
                     "latestSealedSnapshotCutoffUtc", "latestSealedChangeSequence", "latestCopiedPointId",
                     "latestSourceManifestSha256", "latestSnapshotCutoffUtc", "latestChangeSequence" })
            crashIndex.Remove(name);
        crashRepair.ReplaceIndex(crashIndex);
        var repaired = await RecoveryLatestPointDiscovery.RepairFinalizedPointAsync(crashRepair.Client,
            factory.S3(first), "control", first.Manifest, first.ManifestSha256, trust, CancellationToken.None);
        var repairedMarker = crashRepair.Read(RecoveryOwnerPointIndex.PartitionKey(FixtureFactory.Owner,
            FixtureFactory.Library), "INDEX")!;
        Check(repaired?.Pointer.State == "access_incomplete" &&
              repairedMarker["latestSealedPointId"].S == first.PointId.ToString("D").ToLowerInvariant() &&
              !repairedMarker.ContainsKey("latestCopiedPointId"),
            "A sealed deferred point repairs the crash gap without becoming a healthy copy-reuse head.");

        var copyingRows = factory.Rows(second);
        var copyingItem = copyingRows.Single(item => item["pk"].S == "POINT#" + second.PointId.ToString("D").ToLowerInvariant());
        copyingItem["state"] = S("copying");
        var activeCopy = new LatestDynamo(copyingRows);
        var notRepaired = await RecoveryLatestPointDiscovery.RepairFinalizedPointAsync(activeCopy.Client,
            factory.S3(second), "control", second.Manifest, second.ManifestSha256, trust, CancellationToken.None);
        Check(notRepaired is null && activeCopy.UpdateCalls == 0,
            "An active lease is never treated as a finalized copy during crash repair.");

        await RecoveryCaptureCompletionReceiptChecks.RunAsync();
    }

    private static bool IsIndex(GetItemRequest request) => request.Key["sk"].S == "INDEX";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectSafeCode(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == expected) { return; }
        throw new InvalidOperationException("Expected recovery discovery failure: " + expected);
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private sealed class FixtureFactory(RSA rsa, RecoverySigningTrust trust, string signingKeyId, string fingerprint)
    {
        public const string Owner = "latest-discovery-owner";
        public const string Library = "main";
        private const string CaptureBucket = "primary-capture";
        private const string EvidenceBucket = "recovery-evidence";

        public PointFixture Create(int ordinal, DateTimeOffset cutoffUtc, long sequence, string state)
        {
            var pointId = Guid.Parse($"71000000-0000-0000-0000-{ordinal:D12}");
            var captureId = Guid.Parse($"72000000-0000-0000-0000-{ordinal:D12}");
            var fileDigest = new string('c', 64);
            var migrationDigest = new string('d', 64);
            var manifest = new RecoveryPointManifestV1(RecoveryPointManifestV1.CurrentSchemaVersion,
                pointId, captureId, Owner, Library, "ap-southeast-1", cutoffUtc.AddMinutes(2), cutoffUtc,
                "snapshot-" + ordinal, "16/B374D848", migrationDigest,
                new RecoveryChangeWatermark(sequence, cutoffUtc.AddMinutes(-1)),
                [
                    new RecoveryComponentReference("database", RecoveryDataClass.DatabaseSnapshot,
                        $"captures/{captureId:D}/database.dump", "db-v1", fileDigest, 0),
                    new RecoveryComponentReference("inventory", RecoveryDataClass.ObjectInventory,
                        $"captures/{captureId:D}/inventory.json", "inventory-v1", fileDigest, 0)
                ], [], Enum.GetValues<RecoveryDataClass>()
                    .Where(dataClass => dataClass != RecoveryDataClass.PreviewDerivative)
                    .Select(dataClass => new RecoveryClassCoverage(dataClass, 0, 0)).ToArray());
            var manifestBytes = manifest.SerializeCanonical();
            var manifestSha = RecoveryEvidenceCodec.Hash(manifestBytes);
            var sourceKey = $"captures/{captureId:D}/scopes/{RecoveryVerifierRunner.ScopeHash(Owner, Library)}/manifest.json";
            var copiedManifestKey = $"points/{pointId:D}/source-manifests/{manifestSha}.json";
            var sourceReceipt = new PrivateArtifactReceiptV1(CaptureBucket, sourceKey, "source-v1",
                EvidenceBucket, copiedManifestKey, "copied-source-v1", manifestSha, manifestBytes.LongLength);
            var finalReceipt = state == "copied" ? new PrivateArtifactReceiptV1(CaptureBucket, sourceKey,
                "source-v1", EvidenceBucket, $"points/{pointId:D}/manifests/final.json", "final-v1",
                new string('a', 64), 1) : null;
            var ownerReceipt = state == "copied" ? new PrivateArtifactReceiptV1(CaptureBucket, "owner-proof",
                "owner-source-v1", EvidenceBucket, $"points/{pointId:D}/owner-proof.json", "owner-v1",
                new string('b', 64), 1) : null;
            var payload = new PrivateCopyCheckpointPayloadV1(1, captureId, pointId, Owner, Library, manifestSha,
                finalReceipt?.Sha256, "ap-southeast-1", "ap-southeast-2", "444455556666", cutoffUtc.AddMinutes(3),
                new string('e', 64), 0, [], sourceReceipt, ownerReceipt, finalReceipt);
            var payloadBytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
            var signature = rsa.SignHash(SHA256.HashData(payloadBytes), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            var envelope = new PrivateSignedCheckpointV1(1, "recovery_copy_checkpoint", "copy-worker",
                signingKeyId, fingerprint, RecoveryEvidenceCodec.Hash(payloadBytes),
                Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature));
            var checkpointBytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
            var checkpointSha = RecoveryEvidenceCodec.Hash(checkpointBytes);
            var checkpointKey = $"points/{pointId:D}/checkpoints/{checkpointSha}.json";
            return new(pointId, captureId, manifest, manifestBytes, manifestSha, sourceKey, copiedManifestKey,
                checkpointKey, checkpointBytes, checkpointSha, trust, state);
        }

        public List<Dictionary<string, AttributeValue>> Rows(PointFixture first, PointFixture? second = null,
            int historyCount = 0)
        {
            var points = second is null ? new[] { first } : new[] { first, second };
            var rows = new List<Dictionary<string, AttributeValue>> { Index(first, points.Length + historyCount) };
            foreach (var point in points) AddPoint(rows, point);
            for (var index = 0; index < historyCount; index++)
            {
                var historical = Guid.Parse($"73000000-0000-0000-0000-{index + 1:D12}");
                if (points.Any(point => point.PointId == historical)) continue;
                rows.Add(Member(historical, new string('f', 64)));
            }
            return rows;
        }

        public Dictionary<string, AttributeValue> Index(PointFixture point, long indexedPointCount = 1)
        {
            var marker = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S(RecoveryOwnerPointIndex.PartitionKey(Owner, Library)),
                ["sk"] = S("INDEX"),
                ["status"] = S("complete"),
                ["indexVersion"] = N(1),
                ["bootstrapPointCount"] = N(0),
                ["bootstrapPointSetSha256"] = S(RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(Owner, Library, [])),
                ["indexedPointCount"] = N(indexedPointCount),
                ["latestSealedPointId"] = S(point.PointId.ToString("D").ToLowerInvariant()),
                ["latestSealedSourceManifestSha256"] = S(point.ManifestSha256),
                ["latestSealedSnapshotCutoffUtc"] = S(point.Manifest.SnapshotCutoffUtc.ToString("O")),
                ["latestSealedChangeSequence"] = N(point.Manifest.ChangeWatermark.LastSequence)
            };
            if (point.State == "copied")
            {
                marker["latestCopiedPointId"] = S(point.PointId.ToString("D").ToLowerInvariant());
                marker["latestSourceManifestSha256"] = S(point.ManifestSha256);
                marker["latestSnapshotCutoffUtc"] = S(point.Manifest.SnapshotCutoffUtc.ToString("O"));
                marker["latestChangeSequence"] = N(point.Manifest.ChangeWatermark.LastSequence);
            }
            return marker;
        }

        public DiscoveryS3 S3(params PointFixture[] points)
        {
            var client = new DiscoveryS3();
            foreach (var point in points)
            {
                client.Add(EvidenceBucket, point.CopiedManifestKey, "copied-source-v1", point.ManifestBytes);
                client.Add(EvidenceBucket, point.CheckpointKey, "checkpoint-v1", point.CheckpointBytes);
            }
            return client;
        }

        private static void AddPoint(List<Dictionary<string, AttributeValue>> rows, PointFixture point)
        {
            rows.Add(Member(point.PointId, point.ManifestSha256));
            rows.Add(new Dictionary<string, AttributeValue>
            {
                ["pk"] = S("POINT#" + point.PointId.ToString("D").ToLowerInvariant()),
                ["sk"] = S("COPY"),
                ["captureId"] = S(point.CaptureId.ToString("D").ToLowerInvariant()),
                ["ownerId"] = S(Owner),
                ["libraryId"] = S(Library),
                ["sourceAccountId"] = S("111122223333"),
                ["sourceRegion"] = S("ap-southeast-1"),
                ["recoveryAccountId"] = S("444455556666"),
                ["recoveryRegion"] = S("ap-southeast-2"),
                ["sourceManifestBucket"] = S(CaptureBucket),
                ["sourceManifestKey"] = S(point.SourceKey),
                ["sourceManifestVersionId"] = S("source-v1"),
                ["sourceManifestSha256"] = S(point.ManifestSha256),
                ["evidenceBucket"] = S(EvidenceBucket),
                ["manifestKey"] = S($"points/{point.PointId:D}/manifests/final.json"),
                ["manifestVersionId"] = S("final-v1"),
                ["manifestSha256"] = S(new string('a', 64)),
                ["checkpointKey"] = S(point.CheckpointKey),
                ["checkpointVersionId"] = S("checkpoint-v1"),
                ["checkpointSha256"] = S(point.CheckpointSha256),
                ["checkpointByteLength"] = N(point.CheckpointBytes.LongLength),
                ["copiedAtUtc"] = S(point.Manifest.CapturedAtUtc.AddMinutes(1).ToString("O")),
                ["state"] = S(point.State)
            });
        }

        private static Dictionary<string, AttributeValue> Member(Guid pointId, string manifestSha) => new()
        {
            ["pk"] = S(RecoveryOwnerPointIndex.PartitionKey(Owner, Library)),
            ["sk"] = S(RecoveryOwnerPointIndex.PointSortKey(pointId)),
            ["pointId"] = S(pointId.ToString("D").ToLowerInvariant()),
            ["sourceManifestSha256"] = S(manifestSha),
            ["bootstrapMember"] = new() { BOOL = false }
        };
    }

    private sealed record PointFixture(Guid PointId, Guid CaptureId, RecoveryPointManifestV1 Manifest,
        byte[] ManifestBytes, string ManifestSha256, string SourceKey, string CopiedManifestKey,
        string CheckpointKey, byte[] CheckpointBytes, string CheckpointSha256, RecoverySigningTrust Trust, string State);

    private sealed class LatestDynamo : AmazonDynamoDBClient
    {
        private readonly Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> items = [];

        public IAmazonDynamoDB Client => this;
        public int GetCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public int ScanCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public List<(string Pk, string Sk)> GetKeys { get; } = [];
        public Action<GetItemRequest>? BeforeGet { get; set; }

        public LatestDynamo(List<Dictionary<string, AttributeValue>> seed) : base(new AnonymousAWSCredentials(),
            new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:1" })
        {
            foreach (var item in seed) items[Key(item)] = Clone(item);
        }

        public Dictionary<string, AttributeValue>? Read(string pk, string sk) =>
            items.TryGetValue((pk, sk), out var item) ? Clone(item) : null;

        public void Replace(Dictionary<string, AttributeValue> item) => items[Key(item)] = Clone(item);
        public void ReplaceIndex(Dictionary<string, AttributeValue> item) => Replace(item);
        public void Remove(string pk, string sk) => items.Remove((pk, sk));

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ConsistentRead != true) throw new InvalidOperationException("latest_point_get_not_strong");
            GetCalls++;
            var key = Key(request.Key);
            GetKeys.Add(key);
            BeforeGet?.Invoke(request);
            return Task.FromResult(new GetItemResponse
            {
                Item = items.TryGetValue(key, out var item) ? Clone(item) : new Dictionary<string, AttributeValue>()
            });
        }

        public override Task<QueryResponse> QueryAsync(QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            throw new InvalidOperationException("latest_point_discovery_must_not_query_history");
        }

        public override Task<ScanResponse> ScanAsync(ScanRequest request,
            CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            throw new InvalidOperationException("latest_point_discovery_must_not_scan_history");
        }

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            var key = Key(request.Key);
            if (!items.TryGetValue(key, out var marker) || marker["status"].S != "complete" ||
                marker["indexVersion"].N != "1")
                throw new ConditionalCheckFailedException("index_not_complete");
            var nextCutoff = DateTimeOffset.Parse(request.ExpressionAttributeValues[":cutoff"].S!,
                System.Globalization.CultureInfo.InvariantCulture);
            var nextSequence = long.Parse(request.ExpressionAttributeValues[":sequence"].N!,
                System.Globalization.CultureInfo.InvariantCulture);
            if (marker.TryGetValue("latestSealedSnapshotCutoffUtc", out var currentCutoffValue))
            {
                var currentCutoff = DateTimeOffset.Parse(currentCutoffValue.S!,
                    System.Globalization.CultureInfo.InvariantCulture);
                var currentSequence = long.Parse(marker["latestSealedChangeSequence"].N!,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (currentCutoff > nextCutoff || currentCutoff == nextCutoff && currentSequence >= nextSequence)
                    throw new ConditionalCheckFailedException("chronological_condition_failed");
            }
            var updateValues = new Dictionary<string, (string Name, string Value)>
            {
                ["latestSealedPointId"] = ("S", request.ExpressionAttributeValues[":point"].S!),
                ["latestSealedSourceManifestSha256"] = ("S", request.ExpressionAttributeValues[":manifest"].S!),
                ["latestSealedSnapshotCutoffUtc"] = ("S", request.ExpressionAttributeValues[":cutoff"].S!),
                ["latestSealedChangeSequence"] = ("N", request.ExpressionAttributeValues[":sequence"].N!)
            };
            foreach (var (name, value) in updateValues)
                marker[name] = value.Name == "S" ? S(value.Value) : N(long.Parse(value.Value,
                    System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(new UpdateItemResponse { Attributes = Clone(marker) });
        }

        private static (string Pk, string Sk) Key(Dictionary<string, AttributeValue> item) =>
            (item["pk"].S!, item["sk"].S!);

        private static Dictionary<string, AttributeValue> Clone(Dictionary<string, AttributeValue> item) =>
            item.ToDictionary(pair => pair.Key, pair => new AttributeValue
            { S = pair.Value.S, N = pair.Value.N, BOOL = pair.Value.BOOL }, StringComparer.Ordinal);
    }

    private sealed class DiscoveryS3 : AmazonS3Client
    {
        private readonly Dictionary<(string Bucket, string Key, string Version), byte[]> objects = [];
        public int GetCalls { get; private set; }

        public DiscoveryS3() : base(new AnonymousAWSCredentials(), new AmazonS3Config
        { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
        { }

        public void Add(string bucket, string key, string version, byte[] bytes) =>
            objects[(bucket, key, version)] = bytes;

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            if (!objects.TryGetValue((request.BucketName, request.Key, request.VersionId ?? string.Empty), out var bytes))
                throw new AmazonS3Exception("fixture exact version missing") { StatusCode = System.Net.HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectResponse
            {
                BucketName = request.BucketName,
                Key = request.Key,
                VersionId = request.VersionId,
                ContentLength = bytes.LongLength,
                ResponseStream = new MemoryStream(bytes, writable: false)
            });
        }
    }
}
