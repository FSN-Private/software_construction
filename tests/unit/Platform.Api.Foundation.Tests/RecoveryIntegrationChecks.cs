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
        await TamperChecksAsync(db, health, next, evidenceRsa, evidenceKeyId, evidenceFingerprint);
        Console.WriteLine("Recovery integration checks passed: signed compact catalogs, queue ordering/replay, health, cycle failure and identity binding.");
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
        RecoveryCatalogPayloadV1 catalog, RSA rsa, string keyId, string fingerprint)
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
