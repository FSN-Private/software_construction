using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryHoldReleaseChecks
{
    internal static async Task RunAsync()
    {
        using var owner = RSA.Create(2048);
        using var verifier = RSA.Create(2048);
        var ownerTrust = Trust(owner, "owner-key");
        var verifierTrust = Trust(verifier, "recovery-key");
        var hash = new string('a', 64);
        var now = DateTimeOffset.UtcNow;
        var created = now.AddDays(-120);
        var observed = now.AddDays(-100);
        var pointId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var approval = new RecoveryHoldReleaseApprovalV1(1, Guid.NewGuid(), "owner", "library", holdId,
            "recovery", hash, created, "independent-mfa", observed.AddMinutes(-1), observed.AddMinutes(10));
        var ownerEnvelope = Sign(approval, "recovery_hold_release_approval", owner, ownerTrust);
        var proof = new RecoveryHoldReleaseProofV1(1, pointId, Guid.NewGuid(), "owner", "library", holdId,
            "recovery", hash, created, hash, observed, "verifier", hash, ownerEnvelope);
        var envelope = Sign(proof, "recovery_hold_release", verifier, verifierTrust);
        var release = RecoveryHoldReleaseCodec.VerifyProof(envelope, verifierTrust, ownerTrust, hash, now);
        Require(release.Payload.ObservedAtUtc == observed, "historical_original_release_time_preserved");
        Reject(() => RecoveryHoldReleaseCodec.VerifyApproval(ownerEnvelope, ownerTrust, now));
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(envelope, ownerTrust, ownerTrust, hash, now));
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(envelope, verifierTrust, verifierTrust, hash, now));
        var sameKeyApproval = Sign(approval, "recovery_hold_release_approval", verifier, verifierTrust);
        var sameKeyProof = Sign(proof with { OwnerApproval = sameKeyApproval }, "recovery_hold_release", verifier, verifierTrust);
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(sameKeyProof, verifierTrust, verifierTrust, hash, now));
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(envelope with { MessageType = "recovery_full_media_verification" },
            verifierTrust, ownerTrust, hash, now));
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(envelope with { PayloadSha256 = new string('b', 64) },
            verifierTrust, ownerTrust, hash, now));
        Reject(() => RecoveryHoldReleaseCodec.VerifyProof(envelope, verifierTrust, ownerTrust, new string('b', 64), now));
        foreach (var changed in new[] { proof with { OwnerId = "foreign" }, proof with { DefinitionSha256 = new string('b', 64) },
                     proof with { CreatedAtUtc = created.AddSeconds(1) }, proof with { ObservedAtUtc = now.AddMinutes(1) },
                     proof with { ObservedAtUtc = approval.ApprovedAtUtc.AddSeconds(-1) },
                     proof with { ObservedAtUtc = approval.ExpiresAtUtc } })
            Reject(() => RecoveryHoldReleaseCodec.VerifyProof(Sign(changed, "recovery_hold_release", verifier, verifierTrust),
                verifierTrust, ownerTrust, hash, now));
        foreach (var changed in new[] { approval with { IndependentMfaId = "" },
                     approval with { ExpiresAtUtc = approval.ApprovedAtUtc.AddMinutes(16) } })
            Reject(() => RecoveryHoldReleaseCodec.VerifyApproval(Sign(changed, "recovery_hold_release_approval", owner, ownerTrust),
                ownerTrust, observed));
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
        Require(RecoveryHoldReleaseCodec.ParseEnvelope(bytes) == envelope, "canonical_private_handoff_changed");
        Reject(() => RecoveryHoldReleaseCodec.ParseEnvelope([.. bytes, (byte)' ']));
        Reject(() => RecoveryHoldReleaseCodec.ParseEnvelope(new byte[64 * 1024 + 1]));

        var hold = new RecoveryRetentionHoldObservationV1(holdId, "recovery", hash, null, true);
        var first = new RecoveryRetentionPointObservationV1(pointId, created, observed.AddMinutes(-2), hash, hash,
            "recovery-key", "recoverable-deleted", 2, 0, hash, null, [hold]);
        var last = first with
        {
            RecoveryPointId = Guid.NewGuid(),
            SnapshotCutoffUtc = now.AddMinutes(-3),
            VerifiedAtUtc = now.AddMinutes(-2),
            Holds = []
        };
        RecoveryRetentionHistoryProofV1 History(IReadOnlyList<VerifiedRecoveryHoldRelease>? releases = null,
            RecoveryRetentionPointObservationV1? tail = null) => RetentionService.VerifyHistory("owner", "library", assetId,
                null, "original", hash, "recovery-key", [first.RecoveryPointId, last.RecoveryPointId],
                [first, tail ?? last], now, releases);
        Require(History().HasActiveHold, "removed_hold_without_private_proof_stays_active");
        var released = History([release]);
        Require(!released.HasActiveHold && released.EligibleAtUtc == observed.AddDays(90) && released.SchemaVersion == 2,
            "independent_original_observation_starts_ninety_days");
        Require(released.HistorySha256 != History().HistorySha256 && released.HoldSetSha256 != History().HoldSetSha256,
            "release_proof_not_bound_to_history");
        Require(History([release], last with { Holds = [hold] }).HasActiveHold, "later_active_hold_overrides_release");
        Reject(() => History([release, release]));
        var foreign = proof with { PointId = Guid.NewGuid() };
        var foreignRelease = RecoveryHoldReleaseCodec.VerifyProof(Sign(foreign, "recovery_hold_release", verifier, verifierTrust),
            verifierTrust, ownerTrust, hash, now);
        Reject(() => History([foreignRelease]));
        Reject(() => History([release], last with { Holds = [hold with { ScopeSha256 = new string('b', 64) }] }));
        RetentionService.ValidateHistoryProof(released, "owner", "library", assetId, null, "original", hash, 2,
            released.CompletePointSetSha256);
        var lifecycleApproval = Sign(approval with { Kind = "lifecycle" }, "recovery_hold_release_approval", owner, ownerTrust);
        var lifecycleProof = proof with { Kind = "lifecycle", OwnerApproval = lifecycleApproval };
        var lifecycleRelease = RecoveryHoldReleaseCodec.VerifyProof(Sign(lifecycleProof, "recovery_hold_release", verifier, verifierTrust),
            verifierTrust, ownerTrust, hash, now);
        var lifecycleFirst = first with { Holds = [hold with { Kind = "lifecycle" }] };
        var lifecycleHistory = RetentionService.VerifyHistory("owner", "library", assetId, null, "original", hash,
            "recovery-key", [first.RecoveryPointId, last.RecoveryPointId], [lifecycleFirst, last], now, [lifecycleRelease]);
        Require(!lifecycleHistory.HasActiveHold && lifecycleHistory.EligibleAtUtc == observed.AddDays(90),
            "legacy_creation_shortened_independent_release_ninety_days");
        var backdatedApproval = Sign(approval with { Kind = "lifecycle", CreatedAtUtc = created.AddDays(-1) },
            "recovery_hold_release_approval", owner, ownerTrust);
        var backdatedRelease = RecoveryHoldReleaseCodec.VerifyProof(Sign(lifecycleProof with
        { CreatedAtUtc = created.AddDays(-1), OwnerApproval = backdatedApproval }, "recovery_hold_release", verifier, verifierTrust),
            verifierTrust, ownerTrust, hash, now);
        Reject(() => RetentionService.VerifyHistory("owner", "library", assetId, null, "original", hash,
            "recovery-key", [first.RecoveryPointId, last.RecoveryPointId], [lifecycleFirst, last], now, [backdatedRelease]));
        await StoreChecksAsync(owner, verifier, ownerTrust, verifierTrust, hash);
        LifecycleCreationChecks(first, last, hold, hash);
    }

    private static void LifecycleCreationChecks(RecoveryRetentionPointObservationV1 first,
        RecoveryRetentionPointObservationV1 last, RecoveryRetentionHoldObservationV1 hold, string hash)
    {
        var legacy = hold with { Kind = "lifecycle" };
        first = first with { Holds = [legacy] };
        last = last with { Holds = [legacy] };
        var origin = RetentionService.ResolveLifecycleHoldCreation(hold.HoldId, hash, [last, first]);
        Require(origin == (first.RecoveryPointId, first.SnapshotCutoffUtc), "legacy_origin_not_earliest_trusted_snapshot");
        var actual = first.SnapshotCutoffUtc.AddDays(-1);
        var recorded = legacy with { CreatedAtUtc = actual };
        Require(RetentionService.ResolveLifecycleHoldCreation(hold.HoldId, hash,
            [first with { Holds = [recorded] }, last with { Holds = [recorded] }]).CreatedAtUtc == actual,
            "new_hold_creation_utc_replaced_by_observation");
        Reject(() => RetentionService.ResolveLifecycleHoldCreation(hold.HoldId, hash, []));
        Reject(() => RetentionService.ResolveLifecycleHoldCreation(Guid.NewGuid(), hash, [first, last]));
        Reject(() => RetentionService.ResolveLifecycleHoldCreation(hold.HoldId, hash,
            [first, last with { Holds = [legacy with { ScopeSha256 = new string('b', 64) }] }]));
        Reject(() => RetentionService.ResolveLifecycleHoldCreation(hold.HoldId, hash,
            [first, last with { Holds = [recorded] }]));
        var row = new Dictionary<string, string?>
        {
            ["id"] = hold.HoldId.ToString("D"),
            ["asset_id"] = Guid.NewGuid().ToString("D"),
            ["reason"] = "restore",
            ["source_reference"] = "private",
            ["expires_at"] = null
        };
        var before = RecoveryPurgeRunner.ReadHoldObservation(row, "lifecycle", first.SnapshotCutoffUtc);
        row["created_at_utc"] = null;
        Require(RecoveryPurgeRunner.ReadHoldObservation(row, "lifecycle", first.SnapshotCutoffUtc) == before,
            "nullable_creation_migration_rebound_legacy_definition");
        row["created_at_utc"] = actual.ToString("O");
        Require(RecoveryPurgeRunner.ReadHoldObservation(row, "lifecycle", first.SnapshotCutoffUtc).ScopeSha256 != before.ScopeSha256,
            "real_creation_not_bound_to_new_hold_definition");
    }

    private static async Task StoreChecksAsync(RSA owner, RSA verifier, RecoverySigningTrust ownerTrust,
        RecoverySigningTrust recoveryTrust, string hash)
    {
        var names = new[] { "Environment", "RecoveryWorkerBuildDigest", "VerificationTableName", "OwnerId", "LibraryId",
            "OwnerApprovalTrust__KeyId", "OwnerApprovalTrust__PublicKeySpkiBase64", "OwnerApprovalTrust__PublicKeySha256", "HoldReleaseEvidenceFiles" };
        var old = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("Recovery__" + name));
        try
        {
            var values = new[] { "fixture", hash, "verification", "owner", "library", ownerTrust.KeyId,
                ownerTrust.PublicKeySpkiBase64, ownerTrust.PublicKeySha256 };
            for (var i = 0; i < values.Length; i++) Environment.SetEnvironmentVariable("Recovery__" + names[i], values[i]);
            Environment.SetEnvironmentVariable("Recovery__HoldReleaseEvidenceFiles", null);
            var configuration = RecoveryWorkerConfiguration.Load("hold-release");
            var now = DateTimeOffset.UtcNow;
            var holdId = Guid.NewGuid();
            var row = new Dictionary<string, string?>
            {
                ["hold_id"] = holdId.ToString("D"),
                ["owner_id"] = "owner",
                ["library_id"] = "library",
                ["created_at_utc"] = now.AddDays(-3).ToString("O"),
                ["expires_at_utc"] = null,
                ["released_at_utc"] = null,
                ["asset_id"] = Guid.NewGuid().ToString("D")
            };
            var captured = RecoveryPurgeRunner.ReadHoldObservation(row, "recovery", now.AddDays(-1));
            var payload = new RecoveryHoldReleaseApprovalV1(1, Guid.NewGuid(), "owner", "library", holdId,
                "recovery", captured.ScopeSha256, now.AddDays(-3), "mfa", now.AddMinutes(-1), now.AddMinutes(10));
            var approval = Sign(payload, "recovery_hold_release_approval", owner, ownerTrust);
            var pointer = new CopyPointer(Guid.NewGuid(), Guid.NewGuid(), "owner", "library", "111111111111", "ap-southeast-1",
                "222222222222", "ap-southeast-2", "capture", "manifest", "v1", hash, "evidence", "final", "v1", hash,
                "checkpoint", "v1", hash, 1, now.AddDays(-1), "copied");
            var manifest = new RecoveryPointManifestV1(1, pointer.PointId, pointer.CaptureId, "owner", "library",
                "ap-southeast-1", now.AddDays(-1), now.AddDays(-1), "snapshot", "lsn", hash, null!, [], [], []);
            var point = new VerifiedCopiedPoint(pointer, manifest, null, null!, [], hash, hash, null);
            var signer = new FixtureSigner(verifier, recoveryTrust);
            using (var lifecycleDatabase = new HoldDatabase())
            {
                var lifecycleRow = new Dictionary<string, string?>
                {
                    ["id"] = holdId.ToString("D"),
                    ["asset_id"] = row["asset_id"],
                    ["reason"] = "restore",
                    ["source_reference"] = "private",
                    ["expires_at"] = null
                };
                var lifecycleHold = RecoveryPurgeRunner.ReadHoldObservation(lifecycleRow, "lifecycle", manifest.SnapshotCutoffUtc);
                var lifecycleApproval = Sign(payload with
                {
                    Kind = "lifecycle",
                    DefinitionSha256 = lifecycleHold.ScopeSha256,
                    CreatedAtUtc = manifest.SnapshotCutoffUtc
                }, "recovery_hold_release_approval", owner, ownerTrust);
                await RejectAsync(() => RecoveryHoldReleaseStore.PersistAsync(lifecycleDatabase, configuration, signer,
                    recoveryTrust, ownerTrust, point, lifecycleRow, lifecycleApproval, default));
                await RejectAsync(() => RecoveryHoldReleaseStore.PersistAsync(lifecycleDatabase, configuration, signer,
                    recoveryTrust, ownerTrust, point, lifecycleRow, lifecycleApproval, default,
                    (pointer.PointId, manifest.SnapshotCutoffUtc), "foreign"));
                var storedLifecycle = await RecoveryHoldReleaseStore.PersistAsync(lifecycleDatabase, configuration, signer,
                    recoveryTrust, ownerTrust, point, lifecycleRow, lifecycleApproval, default,
                    (pointer.PointId, manifest.SnapshotCutoffUtc), "owner");
                var release = RecoveryHoldReleaseCodec.VerifyProof(storedLifecycle, recoveryTrust, ownerTrust, hash, DateTimeOffset.UtcNow);
                if (release.Payload.CreatedAtUtc != manifest.SnapshotCutoffUtc || lifecycleDatabase.Writes != 1)
                    throw new InvalidOperationException("Lifecycle release lost trusted conservative origin");
            }
            foreach (var lost in new[] { false, true })
            {
                using var database = new HoldDatabase { LoseWrite = lost };
                if (lost) await RejectAsync(() => RecoveryHoldReleaseStore.PersistAsync(database, configuration, signer,
                    recoveryTrust, ownerTrust, point, row, approval, default));
                var stored = await RecoveryHoldReleaseStore.PersistAsync(database, configuration, signer,
                    recoveryTrust, ownerTrust, point, row, approval, default);
                var original = RecoveryEvidenceCodec.SerializeCanonical(stored);
                var replay = await RecoveryHoldReleaseStore.PersistAsync(database, configuration, signer,
                    recoveryTrust, ownerTrust, point, row, approval, default);
                if (database.Writes != 1 || !RecoveryEvidenceCodec.SerializeCanonical(replay).AsSpan().SequenceEqual(original))
                    throw new InvalidOperationException("Hold release retry rewrote original evidence/time");
                if (await RecoveryHoldReleaseStore.ExportAsync(database, configuration, pointer.PointId, captured.ScopeSha256,
                    recoveryTrust, ownerTrust, default) != stored) throw new InvalidOperationException("Private export changed release");
                var file = Path.GetTempFileName();
                try
                {
                    await File.WriteAllBytesAsync(file, original);
                    Environment.SetEnvironmentVariable("Recovery__HoldReleaseEvidenceFiles",
                        System.Text.Json.JsonSerializer.Serialize(new[] { file }));
                    var observation = new RecoveryRetentionPointObservationV1(pointer.PointId, manifest.SnapshotCutoffUtc,
                        pointer.CopiedAtUtc, hash, hash, recoveryTrust.KeyId, "recoverable-deleted", 2, 0, hash, null, [captured]);
                    var received = await RecoveryPurgeRunner.ReadHoldReleaseFilesAsync(configuration, recoveryTrust,
                        [observation], DateTimeOffset.UtcNow, default);
                    if (received.Count != 1 || received[0].PayloadSha256 != stored.PayloadSha256)
                        throw new InvalidOperationException("Bounded private handoff lost signed release binding");
                    Environment.SetEnvironmentVariable("Recovery__HoldReleaseEvidenceFiles",
                        System.Text.Json.JsonSerializer.Serialize(new[] { file, file }));
                    await RejectAsync(async () =>
                    {
                        _ = await RecoveryPurgeRunner.ReadHoldReleaseFilesAsync(configuration,
                        recoveryTrust, [observation], DateTimeOffset.UtcNow, default);
                    });
                    Environment.SetEnvironmentVariable("Recovery__HoldReleaseEvidenceFiles", null);
                    if ((await RecoveryPurgeRunner.ReadHoldReleaseFilesAsync(configuration, recoveryTrust,
                        [observation], DateTimeOffset.UtcNow, default)).Count != 0)
                        throw new InvalidOperationException("Missing handoff manufactured release authority");
                }
                finally { File.Delete(file); Environment.SetEnvironmentVariable("Recovery__HoldReleaseEvidenceFiles", null); }
                var changed = new Dictionary<string, string?>(row) { ["created_at_utc"] = now.AddDays(-2).ToString("O") };
                await RejectAsync(() => RecoveryHoldReleaseStore.PersistAsync(database, configuration, signer,
                    recoveryTrust, ownerTrust, point, changed, approval, default));
                await RejectAsync(() => RecoveryHoldReleaseStore.PersistAsync(database, configuration, signer,
                    recoveryTrust, ownerTrust, point with { Pointer = pointer with { CaptureId = Guid.NewGuid() } }, row, approval, default));
                database.Items.Values.Single()["verifiedAtUtc"] = new() { S = now.AddYears(-1).ToString("O") };
                await RejectAsync(async () =>
                {
                    _ = await RecoveryHoldReleaseStore.ExportAsync(database, configuration,
                    pointer.PointId, captured.ScopeSha256, recoveryTrust, ownerTrust, default);
                });
            }
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable("Recovery__" + name, old[name]); }
    }

    private sealed class FixtureSigner(RSA key, RecoverySigningTrust trust) : IRecoveryEvidenceSigner
    {
        public string KeyId => trust.KeyId;
        public string PublicKeySha256 => trust.PublicKeySha256;
        public Task<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken ct) =>
            Task.FromResult(key.SignHash(digest.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    private sealed class HoldDatabase() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),
        new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:1" })
    {
        internal Dictionary<string, Dictionary<string, AttributeValue>> Items { get; } = [];
        internal bool LoseWrite { get; init; }
        internal int Writes;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            if (request.TableName != "verification" || request.ConsistentRead != true)
                throw new InvalidOperationException("Hold proof read is not strongly scoped");
            return Task.FromResult(new GetItemResponse { Item = Items.GetValueOrDefault(request.Key["pk"].S + request.Key["sk"].S) ?? [] });
        }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken ct = default)
        {
            if (request.TableName != "verification" || request.ConditionExpression != "attribute_not_exists(pk)")
                throw new InvalidOperationException("Hold proof write is not immutable");
            if (!Items.TryAdd(request.Item["pk"].S + request.Item["sk"].S, request.Item)) throw new ConditionalCheckFailedException("exists");
            Writes++;
            if (LoseWrite) throw new IOException("Accepted but lost private hold write");
            return Task.FromResult(new PutItemResponse());
        }
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or RecoveryWorkerException or IOException) { return; }
        throw new InvalidOperationException("Untrusted private hold operation was accepted");
    }

    private static RecoverySigningTrust Trust(RSA key, string id)
    {
        var bytes = key.ExportSubjectPublicKeyInfo();
        return new(id, Convert.ToBase64String(bytes), RecoveryEvidenceCodec.Hash(bytes));
    }
    private static RecoveryHoldReleaseEnvelopeV1 Sign<T>(T payload, string type, RSA key, RecoverySigningTrust trust)
    {
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
        return new(1, type, trust.KeyId, trust.PublicKeySha256, RecoveryEvidenceCodec.Hash(bytes), Convert.ToBase64String(bytes),
            Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
    private static void Require(bool result, string code)
    { if (!result) throw new InvalidOperationException(code); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { return; }
        throw new InvalidOperationException("Untrusted hold release was accepted");
    }
}
