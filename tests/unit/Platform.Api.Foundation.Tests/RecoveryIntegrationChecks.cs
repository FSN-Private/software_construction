using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.Recovery;
using Platform.Contracts;
using Platform.Recovery;
using Platform.Storage;

internal static class RecoveryIntegrationChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        using var evidenceRsa = RSA.Create(2048);
        var evidenceSpki = evidenceRsa.ExportSubjectPublicKeyInfo();
        var evidenceKeyId = "recovery-evidence-key-test";
        var evidenceFingerprint = RecoveryEvidenceCodec.Hash(evidenceSpki);
        var trust = new RecoverySigningTrust(evidenceKeyId, Convert.ToBase64String(evidenceSpki), evidenceFingerprint);
        var now = TruncateToMicrosecond(DateTimeOffset.UtcNow);
        var clock = new FixedTimeProvider(now.AddMinutes(2));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recovery:Enabled"] = "true",
            ["Recovery:RecoveryOnly"] = "false",
            ["Recovery:LibraryId"] = "main",
            ["Recovery:OverdueAfterSeconds"] = "2700",
            ["Recovery:ObservationTrust:KeyId"] = evidenceKeyId,
            ["Recovery:ObservationTrust:PublicKeySpkiBase64"] = Convert.ToBase64String(evidenceSpki),
            ["Recovery:ObservationTrust:PublicKeySha256"] = evidenceFingerprint,
        }).Build();
        var importer = new RecoveryObservationImporter(db, configuration, clock);
        var first = Catalog("test-owner", "main", now, Guid.NewGuid(), Guid.NewGuid());
        var catalogEnvelope = SignCatalog(first, evidenceRsa, evidenceKeyId, evidenceFingerprint);
        var catalogBytes = RecoveryCatalogCodec.SerializeEnvelope(catalogEnvelope);
        Check(catalogBytes.Length < 16 * 1024, "Primary recovery catalog is bounded independently of private object inventory size.");
        var catalogText = Encoding.UTF8.GetString(RecoveryCatalogCodec.SerializePayload(first));
        Check(!catalogText.Contains("sourceKey", StringComparison.Ordinal) &&
            !catalogText.Contains("destinationKey", StringComparison.Ordinal) &&
            !catalogText.Contains("filename", StringComparison.Ordinal) &&
            !catalogText.Contains("path", StringComparison.Ordinal),
            "Primary signed catalog has no exact object locators, paths or filenames.");
        await importer.ImportAsync(Encoding.UTF8.GetString(catalogBytes), default);
        await importer.ImportAsync(Encoding.UTF8.GetString(catalogBytes), default);
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_points WHERE point_id=$1", first.RecoveryPointId))! == 1,
            "Catalog import and exact queue replay create one safe primary recovery point.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_point_attestations WHERE point_id=$1", first.RecoveryPointId))! == 1,
            "Catalog signature and canonical payload persist once for health-time re-verification.");
        var summaryBytes = await ScalarAsync(db, "SELECT manifest_bytes FROM recovery_points WHERE point_id=$1", first.RecoveryPointId) as byte[]
            ?? throw new InvalidOperationException("Expected recovery catalog payload.");
        using (var summary = JsonDocument.Parse(summaryBytes))
            Check(!summary.RootElement.TryGetProperty("objects", out _) && summary.RootElement.GetProperty("coverage").GetArrayLength() == 7,
                "Legacy manifest byte column contains only compact signed summary data.");

        await ImportObservations(importer, first, evidenceRsa, evidenceKeyId, evidenceFingerprint, now);
        var health = new RecoveryHealthService(db, configuration, clock);
        var healthy = await health.GetAsync("test-owner", default);
        Check(healthy.Status == "healthy" && healthy.Classes.Count == 7 && healthy.Classes.All(item => item.State == "verified" && item.Integrity == "verified"),
            "Health re-verifies the compact catalog and all seven independent signed observations.");
        var publicHealth = JsonSerializer.Serialize(healthy);
        var operationQuery = new RecoveryOperationQueryService(db, configuration, clock);
        await RestoreRequestChecksAsync(db, configuration, clock, first);
        var points = await operationQuery.ListPointsAsync("test-owner", null, default);
        Check(points.Items.Any(item => item.RecoveryPointId == first.RecoveryPointId) &&
            (await operationQuery.ListPointsAsync("foreign-owner", null, default)).Items.Count == 0 &&
            (await operationQuery.ListPointsAsync("test-owner", Guid.NewGuid(), default)).Items.Count == 0,
            "Point listing verifies signed catalogs and scopes both rows and pagination boundaries to the owner.");
        Check(!JsonSerializer.Serialize(points).Contains("sourceKey", StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(points).Contains("test-owner", StringComparison.Ordinal),
            "Point listing excludes private identities and object locators.");
        for (var index = 0; index < 101; index++)
        {
            var pagePoint = Catalog("pagination-owner", "main", now.AddMilliseconds(-index), Guid.NewGuid(), Guid.NewGuid());
            await importer.ImportAsync(Encoding.UTF8.GetString(RecoveryCatalogCodec.SerializeEnvelope(
                SignCatalog(pagePoint, evidenceRsa, evidenceKeyId, evidenceFingerprint))), default);
        }
        var pageOne = await operationQuery.ListPointsAsync("pagination-owner", null, default);
        var pageTwo = await operationQuery.ListPointsAsync("pagination-owner", pageOne.NextAfterPointId, default);
        Check(pageOne.Items.Count == 100 && pageOne.NextAfterPointId is not null && pageTwo.Items.Count == 1 &&
            pageTwo.NextAfterPointId is null && !pageOne.Items.Select(item => item.RecoveryPointId)
                .Intersect(pageTwo.Items.Select(item => item.RecoveryPointId)).Any() &&
            (await operationQuery.ListPointsAsync("test-owner", pageOne.NextAfterPointId, default)).Items.Count == 0,
            "Bounded point pagination covers every attested row without duplicates or foreign cursor disclosure.");
        Check(!publicHealth.Contains("test-owner", StringComparison.Ordinal) && !publicHealth.Contains("main", StringComparison.Ordinal) &&
            !publicHealth.Contains("sourceKey", StringComparison.Ordinal),
            "Owner health response excludes identities and object locators.");

        var pendingCatalog = Catalog("test-owner", "main", now.AddMilliseconds(500), Guid.NewGuid(), Guid.NewGuid());
        var pendingPayload = Observation(pendingCatalog, pendingCatalog.Coverage[0], Guid.NewGuid(), now.AddMilliseconds(600));
        var pendingEnvelope = SignObservation(pendingPayload, evidenceRsa, evidenceKeyId, evidenceFingerprint);
        var pendingBody = Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeObservationEnvelope(pendingEnvelope));
        var pending = false;
        try { await importer.ImportAsync(pendingBody, default); }
        catch (RecoveryCatalogPendingException) { pending = true; }
        Check(pending, "An observation delivered before its compact signed catalog remains retryable.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_observation_rejections WHERE safe_code='manifest_mismatch'"))! == 0,
            "Catalog-order races are not permanently rejected.");
        await importer.ImportAsync(Encoding.UTF8.GetString(RecoveryCatalogCodec.SerializeEnvelope(
            SignCatalog(pendingCatalog, evidenceRsa, evidenceKeyId, evidenceFingerprint))), default);
        await importer.ImportAsync(pendingBody, default);
        Check((long)(await ScalarAsync(db,
            "SELECT count(*) FROM recovery_observations WHERE recovery_point_id=$1 AND observation_id=$2",
            pendingCatalog.RecoveryPointId, pendingPayload.ObservationId))! == 1,
            "The same observation successfully replays after its signed catalog arrives.");

        var invalid = catalogEnvelope with { SignatureBase64 = Convert.ToBase64String(new byte[256]) };
        var beforeInvalid = (long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_points"))!;
        await importer.ImportAsync(Encoding.UTF8.GetString(RecoveryCatalogCodec.SerializeEnvelope(invalid)), default);
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_points"))! == beforeInvalid &&
            (long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_observation_rejections WHERE safe_code='invalid_signature'"))! == 1,
            "Invalid catalog signatures are durably rejected without creating a health point.");

        var cycle = new RecoveryCycleEventV1(1, Guid.NewGuid(), first.CaptureId, first.RecoveryPointId,
            first.OwnerId, first.LibraryId, now.AddSeconds(1), "copy", "originals", "incomplete", "checksum_mismatch",
            first.RecoveryWorkerBuildDigest);
        var cycleBytes = RecoveryCycleCodec.SerializePayload(cycle);
        var cycleEnvelope = RecoveryCycleCodec.CreateEnvelope(cycle, "test-worker",
            Sign(evidenceRsa, cycleBytes, evidenceKeyId, evidenceFingerprint));
        await importer.ImportAsync(Encoding.UTF8.GetString(RecoveryCycleCodec.SerializeEnvelope(cycleEnvelope)), default);
        var degraded = await health.GetAsync("test-owner", default);
        Check(degraded.Status == "degraded" && degraded.Exceptions.Contains("copy_failed", StringComparer.Ordinal) &&
            degraded.Classes.Single(item => item.DataClass == "originals").State == "incomplete",
            "A newer signed failed cycle is visible without replacing the last successful point.");

        var next = Catalog("test-owner", "main", now.AddSeconds(2), Guid.NewGuid(), Guid.NewGuid());
        var nextEnvelope = SignCatalog(next, evidenceRsa, evidenceKeyId, evidenceFingerprint);
        await importer.ImportAsync(Encoding.UTF8.GetString(RecoveryCatalogCodec.SerializeEnvelope(nextEnvelope)), default);
        await ImportObservations(importer, next, evidenceRsa, evidenceKeyId, evidenceFingerprint, now.AddSeconds(2));
        var recovered = await health.GetAsync("test-owner", default);
        Check(recovered.Status == "healthy", "A later verified point resolves the earlier signed cycle failure.");

        await IdentityChecksAsync(db, storage, clock, now);
        await RestoreSessionChecksAsync(db, trust, evidenceRsa, evidenceKeyId, evidenceFingerprint, now);
        await PurgeProposalChecksAsync(db, storage, configuration, clock, now);
        await TamperChecksAsync(db, health, next, evidenceRsa, evidenceKeyId, evidenceFingerprint, operationQuery);
        if (Environment.GetEnvironmentVariable("RECOVERY_CAPACITY") == "1")
            await RecoveryHealthCapacityChecks.RunAsync(db, health, "test-owner");
        Console.WriteLine("Recovery integration checks passed: signed compact catalogs, queue ordering/replay, health, cycle failure and identity binding.");
    }

    private static async Task PurgeProposalChecksAsync(NpgsqlDataSource db, S3ObjectStore storage,
        IConfiguration configuration, FixedTimeProvider clock, DateTimeOffset now)
    {
        const string owner = "purge-proposal-owner";
        var assetId = Guid.NewGuid();
        var sharedAssetId = Guid.NewGuid();
        var payloadBytes = File.ReadAllBytes("tests/fixtures/ingest/sample.jpg");
        var contentSha = RecoveryEvidenceCodec.Hash(payloadBytes);
        string storageVersion;
        await using (var bytes = new MemoryStream(payloadBytes, writable: false))
            storageVersion = await storage.PublishVerifiedAsync(new(contentSha, payloadBytes.LongLength), bytes, default);
        await using (var insert = db.CreateCommand("""
            INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
            VALUES($1,$2,'jpeg',$3,$4,'authoritative')
            ON CONFLICT(sha256) DO UPDATE SET storage_key=EXCLUDED.storage_key,
                storage_version=EXCLUDED.storage_version,integrity_state='authoritative'
             WHERE content_objects.byte_length=EXCLUDED.byte_length AND content_objects.category=EXCLUDED.category
            """))
        {
            insert.Parameters.AddWithValue(contentSha);
            insert.Parameters.AddWithValue(payloadBytes.LongLength);
            insert.Parameters.AddWithValue("sha256/" + contentSha);
            insert.Parameters.AddWithValue(storageVersion);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var insert = db.CreateCommand("""
            INSERT INTO assets(id,owner_id,original_sha256,state,lifecycle_version,deleted_until,format)
            VALUES($1,$2,$3,'recoverable-deleted',5,$4,'jpeg')
            """))
        {
            insert.Parameters.AddWithValue(assetId);
            insert.Parameters.AddWithValue(owner);
            insert.Parameters.AddWithValue(contentSha);
            insert.Parameters.AddWithValue(now.AddDays(-100));
            await insert.ExecuteNonQueryAsync();
        }
        await using (var insert = db.CreateCommand("""
            INSERT INTO assets(id,owner_id,original_sha256,state,lifecycle_version,format)
            VALUES($1,'legitimate-foreign-owner',$2,'active',1,'jpeg')
            """))
        {
            insert.Parameters.AddWithValue(sharedAssetId);
            insert.Parameters.AddWithValue(contentSha);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var insertPath = db.CreateCommand("""
            INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active)
            VALUES($1,$2,'legitimate-foreign-owner','shared.jpg','shared.jpg',true)
            """))
        {
            insertPath.Parameters.AddWithValue(Guid.NewGuid());
            insertPath.Parameters.AddWithValue(sharedAssetId);
            await insertPath.ExecuteNonQueryAsync();
        }

        var configValues = configuration.AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var proposed = await RecoveryPurgeHttpChecks.RunAsync(db, storage, configValues, clock, assetId,
            sharedAssetId, contentSha, payloadBytes);
        Check(proposed.Phase == "awaiting_independent_approval" && proposed.Proposal.EligibleAtUtc == clock.GetUtcNow() &&
            RecoveryPurgeProposalV1.ParseCanonical(RecoveryEvidenceCodec.SerializeCanonical(proposed.Proposal)) == proposed.Proposal,
            "The owner API creates a canonical proposal only after retention eligibility and starts its approval window at request time.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_purge_proposals WHERE operation_id=$1", proposed.Proposal.OperationId))! == 1 &&
            (long)(await ScalarAsync(db, "SELECT count(*) FROM media_purge_tombstones WHERE event_id=$1", proposed.Proposal.EventId))! == 0 &&
            (long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_purge_journals WHERE operation_id=$1", proposed.Proposal.OperationId))! == 0,
            "A primary proposal is reversible and installs neither the permanent tombstone nor an execution journal.");
        var service = new RecoveryPurgeService(db, configuration, clock);
        Check((await service.GetProposalAsync(owner, proposed.Proposal.OperationId, default))?.Proposal == proposed.Proposal &&
            await service.GetProposalAsync("foreign-owner", proposed.Proposal.OperationId, default) is null,
            "Proposal reads are constrained to the canonical owner and configured library.");

        // Exercise the hold rejection with a different identity because the HTTP check above
        // has now frozen the first proposal and duplicate active proposals are rejected first.
        var heldAssetId = Guid.NewGuid();
        await using (var insertHeldAsset = db.CreateCommand("INSERT INTO assets(id,owner_id,original_sha256,state,lifecycle_version,deleted_until,format) VALUES($1,$2,$3,'recoverable-deleted',5,$4,'jpeg')"))
        {
            insertHeldAsset.Parameters.AddWithValue(heldAssetId);
            insertHeldAsset.Parameters.AddWithValue(owner);
            insertHeldAsset.Parameters.AddWithValue(contentSha);
            insertHeldAsset.Parameters.AddWithValue(now.AddDays(-100));
            await insertHeldAsset.ExecuteNonQueryAsync();
        }
        await using (var hold = db.CreateCommand("INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($1,$2,'legal_hold','test')"))
        {
            hold.Parameters.AddWithValue(Guid.NewGuid());
            hold.Parameters.AddWithValue(heldAssetId);
            await hold.ExecuteNonQueryAsync();
        }
        var rejectedByHold = false;
        try
        {
            await service.ProposeAsync(owner, new RecoveryPurgeProposalRequest(heldAssetId, null, "original"),
                "purge-proposal-held", default);
        }
        catch (RecoveryPurgeException error) when (error.Code == "purge_active_lifecycle_hold" && error.StatusCode == 409)
        {
            rejectedByHold = true;
        }
        Check(rejectedByHold && (long)(await ScalarAsync(db,
                "SELECT count(*) FROM recovery_purge_proposals WHERE asset_id=$1", heldAssetId))! == 0,
            "An active lifecycle hold blocks a later purge proposal without leaving an extra proposal row.");
        await using (var cleanup = db.CreateCommand("DELETE FROM lifecycle_holds WHERE asset_id=ANY($1)"))
        {
            cleanup.Parameters.AddWithValue(new[] { assetId, heldAssetId });
            await cleanup.ExecuteNonQueryAsync();
        }
        await using (var cleanup = db.CreateCommand("DELETE FROM asset_paths WHERE asset_id=ANY($1)"))
        {
            cleanup.Parameters.AddWithValue(new[] { assetId, sharedAssetId, heldAssetId });
            await cleanup.ExecuteNonQueryAsync();
        }
        await using (var cleanup = db.CreateCommand("DELETE FROM assets WHERE id=ANY($1)"))
        {
            cleanup.Parameters.AddWithValue(new[] { assetId, sharedAssetId, heldAssetId });
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task RestoreRequestChecksAsync(NpgsqlDataSource db, IConfiguration configuration,
        TimeProvider clock, RecoveryCatalogPayloadV1 point)
    {
        var service = new RecoveryRestoreRequestService(db, configuration, clock);
        var request = new RecoveryRestoreRequest(point.RecoveryPointId, new("library", null, null), "fixture recovery request");
        var key = Guid.NewGuid().ToString("N");
        var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.RequestAsync(point.OwnerId,
            request, key, "restore-request-fixture", default)));
        Check(requests.Select(item => item.SessionId).Distinct().Count() == 1 && requests.All(item => item.State == "requested"),
            "Concurrent idempotent requests create exactly one isolated pending session.");
        var session = requests[0];
        var laterService = new RecoveryRestoreRequestService(db, configuration, new FixedTimeProvider(clock.GetUtcNow().AddMinutes(5)));
        var laterReplay = await laterService.RequestAsync(point.OwnerId, request, key, "later-fixture", default);
        Check(laterReplay.SessionId == session.SessionId && laterReplay.ExpiresAtUtc == session.ExpiresAtUtc,
            "A later retry reuses the persisted expiry instead of refreshing request authority.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM audit_events WHERE resource_id=$1 AND event_type='restore_requested'", session.SessionId))! == 1 &&
            (long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_change_ledger WHERE entity_type='restore-session' AND entity_id=$1", session.SessionId.ToString("D")))! == 1,
            "The restore request and its audit/change evidence commit once in the same transaction.");
        await Reject(() => service.RequestAsync(point.OwnerId, request with { Reason = "changed reason" }, key, "fixture", default), "restore_request_conflict");
        await Reject(() => service.RequestAsync("foreign-owner", request, key, "fixture", default), "recovery_point_not_found");
        await Reject(() => service.RequestAsync(point.OwnerId, request, "", "fixture", default), "restore_request_invalid");
        Check(!System.Text.Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(new RecoveryRestoreScopeV1("full_library", null, null)))
            .Contains("reason", StringComparison.Ordinal), "Legacy null-reason scope bytes remain compatible.");
        async Task Reject(Func<Task<RecoverySessionView>> action, string code)
        {
            try { await action(); }
            catch (RecoveryRestoreRequestException error) when (error.Code == code) { return; }
            throw new InvalidOperationException("Expected restore request rejection: " + code);
        }
    }

    private static async Task RestoreSessionChecksAsync(
        NpgsqlDataSource db,
        RecoverySigningTrust trust,
        RSA evidenceRsa,
        string evidenceKeyId,
        string evidenceFingerprint,
        DateTimeOffset now)
    {
        var point = Catalog("restore-store-owner", "main", now.AddMilliseconds(-500), Guid.NewGuid(), Guid.NewGuid());
        var envelope = SignCatalog(point, evidenceRsa, evidenceKeyId, evidenceFingerprint);
        var proposal = RecoveryRestoreProposalV1.Create(Guid.NewGuid(), point.OwnerId, point.LibraryId,
            point.RecoveryPointId, new RecoveryRestoreScopeV1("full_library", null, null), now.AddHours(1));
        var sha = RecoveryEvidenceCodec.Hash("isolated restored object"u8);
        const long byteLength = 24;
        await using var connection = await db.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insertContent = new NpgsqlCommand("""
                INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
                VALUES ($1,$2,'original',$3,'source-exact-version','authoritative')
                """, connection, transaction);
            insertContent.Parameters.AddWithValue(sha);
            insertContent.Parameters.AddWithValue(byteLength);
            insertContent.Parameters.AddWithValue("sha256/" + sha);
            await insertContent.ExecuteNonQueryAsync();

            await RestoreSessionStore.ImportVerifiedPointAsync(connection, transaction, envelope, trust);
            await RestoreSessionStore.ImportVerifiedPointAsync(connection, transaction, envelope, trust);
            await RestoreSessionStore.ImportProposalAsync(connection, transaction, proposal);
            await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId,
                "requested", "restoring", DateTimeOffset.UtcNow);
            await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId,
                "restoring", "validating", DateTimeOffset.UtcNow);
            async Task RejectReady()
            {
                try
                {
                    await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId,
                        "validating", "ready", DateTimeOffset.UtcNow);
                }
                catch (InvalidOperationException error) when (error.Message == "restore_session_state_conflict") { return; }
                throw new InvalidOperationException("Unvalidated or failed restore became ready");
            }
            await RejectReady();
            Check(await RestoreSessionStore.AppendValidationAsync(connection, transaction, proposal.SessionId,
                point.FullManifestSha256, new string('b', 64), "failed", ["integrity_failed"], DateTimeOffset.UtcNow) == 1,
                "Failed restore validation is retained as an immutable revision.");
            await RejectReady();
            var materialized = new RecoveryMaterializedObjectV1("source-library", "sha256/" + sha,
                "source-exact-version", sha, byteLength, "isolated-target", "sha256/" + sha, "target-exact-version");
            Check(await RestoreSessionStore.RebindMaterializedObjectsAsync(connection, transaction,
                proposal.SessionId, [materialized]) == 1,
                "Isolated restore rebinds the exact source version to a distinct target version.");
            Check(await RestoreSessionStore.AppendValidationAsync(connection, transaction, proposal.SessionId,
                point.FullManifestSha256, new string('b', 64), "verified", [], DateTimeOffset.UtcNow) == 2,
                "Validation revision binds the source manifest to the target namespace digest.");
            await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId,
                "validating", "ready", DateTimeOffset.UtcNow);
            try
            {
                await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId,
                    "ready", "promoting", DateTimeOffset.UtcNow);
                throw new InvalidOperationException("A state update granted promotion without owner authority");
            }
            catch (InvalidDataException error) when (error.Message == "restore_promotion_authority_required") { }
            await RestoreSessionStore.ImportProposalAsync(connection, transaction, proposal);
            await transaction.CommitAsync();
        }

        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_point_attestations WHERE point_id=$1", point.RecoveryPointId))! == 1,
            "A recovery-only restore hydrates exactly one signed compact point before the session FK is written.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_sessions WHERE session_id=$1 AND state='ready' AND validation_digest IS NOT NULL", proposal.SessionId))! == 1,
            "The restored database owns the durable ready session and bound validation digest.");
        var queryConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Recovery:Enabled"] = "true", ["Recovery:LibraryId"] = point.LibraryId }).Build();
        var status = new RecoveryOperationQueryService(db, queryConfiguration);
        Check((await status.GetSessionAsync(point.OwnerId, proposal.SessionId, default))?.State == "ready" &&
            await status.GetSessionAsync("foreign-owner", proposal.SessionId, default) is null &&
            await status.GetSessionAsync(point.OwnerId, Guid.NewGuid(), default) is null,
            "Session status is owner scoped and reveals no existence to foreign principals.");
        Check((await new RecoveryOperationQueryService(db, queryConfiguration, new FixedTimeProvider(now.AddHours(2)))
            .GetSessionAsync(point.OwnerId, proposal.SessionId, default))?.State == "expired",
            "Expired session status never displays ready even before asynchronous expiry reconciliation.");
        Check((string)(await ScalarAsync(db, "SELECT storage_version FROM content_objects WHERE sha256=$1", sha))! == "target-exact-version",
            "Only the isolated content locator is rebound to the materialized target object version.");
        Check((long)(await ScalarAsync(db, "SELECT count(*) FROM recovery_materialization_receipts WHERE session_id=$1 AND source_version_id='source-exact-version' AND destination_version_id='target-exact-version'", proposal.SessionId))! == 1,
            "The exact source-to-target mapping remains as immutable recovery-local evidence.");
        Check((string)(await ScalarAsync(db, "SELECT target_digest FROM recovery_validation_revisions WHERE session_id=$1 AND revision=2", proposal.SessionId))! == new string('b', 64),
            "An immutable validation revision records the target digest explicitly.");
    }

    private static async Task IdentityChecksAsync(NpgsqlDataSource db, S3ObjectStore storage, FixedTimeProvider clock, DateTimeOffset now)
    {
        using var approvalRsa = RSA.Create(2048);
        var spki = approvalRsa.ExportSubjectPublicKeyInfo();
        var keyId = "independent-owner-approval-test-key";
        var fingerprint = RecoveryEvidenceCodec.Hash(spki);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recovery:RecoveryOnly"] = "true",
            ["Recovery:OwnerApprovalTrust:KeyId"] = keyId,
            ["Recovery:OwnerApprovalTrust:PublicKeySpkiBase64"] = Convert.ToBase64String(spki),
            ["Recovery:OwnerApprovalTrust:PublicKeySha256"] = fingerprint,
        }).Build();
        var mappingId = Guid.NewGuid();
        var approvedAt = TruncateToMicrosecond(now.AddSeconds(-10));
        var approval = new RecoveryIdentityApprovalV1(1, mappingId, "https://recovery.example/issuer",
            "recovery-client", "new-cognito-subject", "test-owner", "independent-owner", keyId, approvedAt);
        var receipt = approval.SerializeForTest(approvalRsa);
        await using (var insert = db.CreateCommand("""
            INSERT INTO recovery_identity_mappings(
                mapping_id,issuer,audience,subject,owner_id,approved_by,approval_key_id,approval_receipt,approved_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """))
        {
            insert.Parameters.AddWithValue(mappingId);
            insert.Parameters.AddWithValue(approval.Issuer);
            insert.Parameters.AddWithValue(approval.Audience);
            insert.Parameters.AddWithValue(approval.Subject);
            insert.Parameters.AddWithValue(approval.PreservedOwnerId);
            insert.Parameters.AddWithValue(approval.ApprovedBy);
            insert.Parameters.AddWithValue(keyId);
            insert.Parameters.AddWithValue(receipt);
            insert.Parameters.AddWithValue(approvedAt);
            await insert.ExecuteNonQueryAsync();
        }

        var resolver = new RecoveryOwnerResolver(db, config, clock);
        var transform = new RecoveryOwnerClaimsTransformation(resolver, config, NullLogger<RecoveryOwnerClaimsTransformation>.Instance);
        var claims = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, approval.Subject),
            new Claim("iss", approval.Issuer),
            new Claim("aud", approval.Audience),
            new Claim("cognito:groups", "owner"),
        ], "test");
        var mapped = await transform.TransformAsync(new ClaimsPrincipal(claims));
        Check(OwnerAuthorization.TryGetOwner(mapped, out var mappedOwner) && mappedOwner == "test-owner",
            "A newly authenticated recovery subject receives the preserved owner identity from its signed mapping.");
        var query = new QueryCollection();
        var browse = new GalleryBrowseService(db, new EphemeralDataProtectionProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gallery:LibraryId"] = "main" }).Build());
        var mappedGallery = await browse.BrowseAsync(mappedOwner, "main", query, default);
        var originalGallery = await browse.BrowseAsync("test-owner", "main", query, default);
        Check(mappedGallery.Count > 0 && mappedGallery.Count == originalGallery.Count,
            "The mapped owner can read the same existing private gallery rows as the preserved owner.");
        var assets = await new AssetService(db, storage).ListAsync(mappedOwner, false, null, default);
        var originalAssets = await new AssetService(db, storage).ListAsync("test-owner", false, null, default);
        Check(assets.Count > 0 && assets.Select(item => item.Id).Order().SequenceEqual(originalAssets.Select(item => item.Id).Order()),
            "The mapped owner can read the same existing private asset rows as the preserved owner.");

        var foreign = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "foreign-subject"),
            new Claim("iss", approval.Issuer),
            new Claim("aud", approval.Audience),
            new Claim("cognito:groups", "owner"),
            new Claim("platform:recovery-identity-bound", "true"),
        ], "test");
        var denied = await transform.TransformAsync(new ClaimsPrincipal(foreign));
        Check(!OwnerAuthorization.TryGetOwner(denied, out _) &&
            !denied.Claims.Any(claim => claim.Type == "platform:recovery-identity-bound"),
            "A foreign owner and a forged custom binding marker cannot bypass independent mapping verification.");
    }

    private static async Task TamperChecksAsync(NpgsqlDataSource db, RecoveryHealthService health,
        RecoveryCatalogPayloadV1 catalog, RSA rsa, string keyId, string fingerprint, RecoveryOperationQueryService operationQuery)
    {
        var original = await ScalarAsync(db,
            "SELECT signature FROM recovery_point_attestations WHERE point_id=$1", catalog.RecoveryPointId) as byte[]
            ?? throw new InvalidOperationException("Expected persisted recovery catalog signature.");
        await using (var alter = db.CreateCommand("ALTER TABLE recovery_point_attestations DISABLE TRIGGER recovery_point_attestations_immutable"))
            await alter.ExecuteNonQueryAsync();
        try
        {
            await using var update = db.CreateCommand("UPDATE recovery_point_attestations SET signature=$2 WHERE point_id=$1");
            update.Parameters.AddWithValue(catalog.RecoveryPointId);
            update.Parameters.AddWithValue(new byte[256]);
            await update.ExecuteNonQueryAsync();
            var invalid = await health.GetAsync("test-owner", default);
            Check(!(await operationQuery.ListPointsAsync("test-owner", null, default)).Items.Any(item => item.RecoveryPointId == catalog.RecoveryPointId),
                "A tampered catalog must disappear from selectable point listings.");
            Check(invalid.Status == "degraded" && invalid.Exceptions.Contains("catalog_invalid", StringComparer.Ordinal) &&
                invalid.Classes.Any(item => item.State == "integrity_failed"),
                "Health detects a database operator changing the persisted compact catalog signature.");
        }
        finally
        {
            await using var restore = db.CreateCommand("UPDATE recovery_point_attestations SET signature=$2 WHERE point_id=$1");
            restore.Parameters.AddWithValue(catalog.RecoveryPointId);
            restore.Parameters.AddWithValue(original);
            await restore.ExecuteNonQueryAsync();
            await using var enable = db.CreateCommand("ALTER TABLE recovery_point_attestations ENABLE TRIGGER recovery_point_attestations_immutable");
            await enable.ExecuteNonQueryAsync();
        }
        Check((await health.GetAsync("test-owner", default)).Status == "healthy",
            "Restoring the trusted signature restores the health result.");
    }

    private static RecoveryCatalogPayloadV1 Catalog(string owner, string library, DateTimeOffset time, Guid pointId, Guid captureId)
    {
        var classes = RecoveryEvidenceCodec.ObservationClasses.Select(dataClass =>
            new RecoveryCatalogCoverageV1(dataClass, 1, 1, 16, 16, "healthy", [])).ToArray();
        var hash = new string('a', 64);
        var cutoff = time.AddMinutes(-1);
        return new RecoveryCatalogPayloadV1(1, pointId, captureId, owner, library,
            "111122223333", "ap-southeast-1", "444455556666", "ap-southeast-2",
            time, cutoff, $"exported-{captureId:N}", "0/12345", 0, null,
            hash, hash, hash, hash, hash, hash, hash, hash, time, "test-verifier", classes);
    }

    private static async Task ImportObservations(RecoveryObservationImporter importer, RecoveryCatalogPayloadV1 catalog,
        RSA rsa, string keyId, string fingerprint, DateTimeOffset observedAt)
    {
        foreach (var coverage in catalog.Coverage)
        {
            var payload = Observation(catalog, coverage, Guid.NewGuid(), observedAt);
            var envelope = SignObservation(payload, rsa, keyId, fingerprint);
            var body = RecoveryEvidenceCodec.SerializeObservationEnvelope(envelope);
            await importer.ImportAsync(Encoding.UTF8.GetString(body), default);
            await importer.ImportAsync(Encoding.UTF8.GetString(body), default);
        }
    }

    private static RecoveryObservationPayloadV1 Observation(RecoveryCatalogPayloadV1 catalog,
        RecoveryCatalogCoverageV1 coverage, Guid observationId, DateTimeOffset observedAt) =>
        new(1, observationId, catalog.RecoveryPointId, catalog.OwnerId, catalog.LibraryId, coverage.DataClass,
            "healthy", observedAt, coverage.ExpectedItemCount, coverage.ObservedItemCount,
            coverage.ExpectedByteLength, coverage.ObservedByteLength, catalog.FullManifestSha256, [], null);

    private static RecoveryCatalogEnvelopeV1 SignCatalog(RecoveryCatalogPayloadV1 payload, RSA rsa, string keyId, string fingerprint) =>
        RecoveryCatalogCodec.CreateEnvelope(payload, "test-worker", Sign(rsa, RecoveryCatalogCodec.SerializePayload(payload), keyId, fingerprint));

    private static RecoveryObservationEnvelopeV1 SignObservation(RecoveryObservationPayloadV1 payload, RSA rsa, string keyId, string fingerprint) =>
        RecoveryEvidenceCodec.CreateObservationEnvelope(payload, "test-worker",
            Sign(rsa, payload.SerializeCanonical(), keyId, fingerprint));

    private static RecoveryEvidenceSignature Sign(RSA rsa, byte[] bytes, string keyId, string fingerprint) =>
        new(keyId, fingerprint, rsa.SignHash(SHA256.HashData(bytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    private static DateTimeOffset TruncateToMicrosecond(DateTimeOffset value) =>
        new(value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % 10, TimeSpan.Zero);

    private static async Task<object?> ScalarAsync(NpgsqlDataSource db, string sql, params object?[] values)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal static class RecoveryIdentityApprovalTestExtensions
{
    public static byte[] SerializeForTest(this RecoveryIdentityApprovalV1 approval, RSA rsa)
    {
        var payload = RecoveryEvidenceCodec.SerializeCanonical(approval);
        return rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}
