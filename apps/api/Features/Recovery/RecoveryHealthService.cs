using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Platform.Recovery;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Recovery;

public sealed record RecoveryHealthClass(
    string DataClass,
    string State,
    DateTimeOffset? LastVerifiedAtUtc,
    double? RecoveryPointAgeSeconds,
    double? Coverage,
    string Integrity);

public sealed record RecoveryHealthView(
    string Status,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<RecoveryHealthClass> Classes,
    IReadOnlyList<string> Exceptions,
    DateTimeOffset? LastAlertAtUtc);

public sealed class RecoveryUnavailableException() : Exception("recovery_unavailable");

public sealed class RecoveryHealthService(NpgsqlDataSource source, IConfiguration configuration, TimeProvider? clock = null)
{
    private static readonly string[] Classes = ["originals", "xmp", "version_graph", "metadata", "audit", "configuration", "recovery_records"];
    private static readonly HashSet<string> SafeCodes = new(StringComparer.Ordinal)
    {
        "capture_overdue", "class_missing", "manifest_mismatch", "bytes_mismatch", "graph_mismatch",
        "audit_chain_mismatch", "access_policy_failed", "kms_unavailable", "cold_retrieval_pending",
        "copy_corrupt", "alert_delivery_failed", "observation_rejected", "recovery_point_incomplete",
        "recovery_point_integrity_failed", "recovery_point_unavailable", "recovery_copy_pending", "future_timestamp",
        "catalog_missing", "catalog_invalid", "capture_failed", "copy_failed", "verification_failed",
        "publication_failed", "snapshot_failed", "coverage_incomplete", "recovery_cycle_invalid"
    };

    public static string? ConfiguredLibrary(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["Recovery:LibraryId"]) ? config["Gallery:LibraryId"] : config["Recovery:LibraryId"];

    public static bool IsEnabled(IConfiguration config) =>
        config.GetValue<bool>("Recovery:Enabled") && !string.IsNullOrWhiteSpace(ConfiguredLibrary(config));

    public async Task<RecoveryHealthView> GetAsync(string owner, CancellationToken ct)
    {
        if (!IsEnabled(configuration) || !TryGetTrust(configuration, out var trust)) throw new RecoveryUnavailableException();
        var library = ConfiguredLibrary(configuration)!;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await using var connection = await source.OpenConnectionAsync(ct);

        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        var overdueAfter = Math.Clamp(configuration.GetValue("Recovery:OverdueAfterSeconds", 2700), 1, 2700);
        var cycle = await ReadLatestCycleAsync(connection, owner, library, trust, now, ct);
        var point = await ReadLatestPoint(connection, owner, library, ct);
        if (point is null)
        {
            var emptyCodes = new HashSet<string>(StringComparer.Ordinal) { "recovery_point_unavailable" };
            if (cycle is { } noPointCycle)
            {
                emptyCodes.Add(noPointCycle.Valid ? noPointCycle.SafeCode : "recovery_cycle_invalid");
                if (noPointCycle.ObservedAtUtc > now) emptyCodes.Add("future_timestamp");
            }
            return Empty(now, emptyCodes, cycle is { Valid: true } ? cycle.FailedDataClass : null);
        }

        var cutoff = point.SnapshotCutoffUtc;
        var futureTimestamp = cutoff > now || point.VerifiedAtUtc > now;
        var ageSeconds = futureTimestamp ? (double?)null : Math.Max(0, (now - cutoff).TotalSeconds);
        if (futureTimestamp) exceptions.Add("future_timestamp");
        if (ageSeconds is { } age && age >= overdueAfter) exceptions.Add("capture_overdue");

        VerifiedRecoveryCatalog? catalog = null;
        try
        {
            if (point.Attestation is not null)
            {
                catalog = RecoveryCatalogCodec.Verify(point.Attestation, trust);
                if (!MatchesPoint(catalog.Payload, point) || catalog.Payload.VerifiedAtUtc > now ||
                    catalog.Payload.CapturedAtUtc > now || catalog.Payload.SnapshotCutoffUtc > now)
                {
                    catalog = null;
                    exceptions.Add("catalog_invalid");
                    if (point.VerifiedAtUtc > now || point.SnapshotCutoffUtc > now) exceptions.Add("future_timestamp");
                }
            }
            else
            {
                exceptions.Add("catalog_missing");
            }
        }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
        {
            exceptions.Add("catalog_invalid");
        }

        string? activeCycleClass = null;
        if (cycle is { } latestCycle &&
            (!latestCycle.Valid || catalog is null || latestCycle.ObservedAtUtc > catalog.Payload.VerifiedAtUtc || latestCycle.ObservedAtUtc > now))
        {
            exceptions.Add(latestCycle.Valid ? latestCycle.SafeCode : "recovery_cycle_invalid");
            activeCycleClass = latestCycle.FailedDataClass;
            if (latestCycle.ObservedAtUtc > now) exceptions.Add("future_timestamp");
        }

        var expected = catalog is null ? null : catalog.Payload.Coverage.ToDictionary(
            coverage => coverage.DataClass,
            coverage => new ExpectedCoverage(coverage.ExpectedItemCount, coverage.ExpectedByteLength,
                coverage.ObservedItemCount, coverage.ObservedByteLength, coverage.Outcome),
            StringComparer.Ordinal);

        var observations = new Dictionary<string, Observation>(StringComparer.Ordinal);
        DateTimeOffset? lastAlertAt = null;
        if (catalog is not null && expected is not null)
        {
            var digest = catalog.Payload.FullManifestSha256;
            await using (var observationsCommand = Command(connection, """
                SELECT DISTINCT ON (data_class) worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,
                       canonical_payload,signature,data_class
                FROM recovery_observations
                WHERE recovery_point_id=$1 AND owner_id=$2 AND library_id=$3
                  AND manifest_sha256=$4 AND import_state='verified'
                ORDER BY data_class,observed_at_utc DESC,observation_id DESC
                """, point.PointId, owner, library, digest))
            await using (var reader = await observationsCommand.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var dataClass = reader.GetString(6);
                    if (!Classes.Contains(dataClass, StringComparer.Ordinal)) continue;
                    try
                    {
                        var verified = RecoveryEvidenceCodec.VerifyObservation(ReadObservationEnvelope(reader), trust);
                        var payload = verified.Payload;
                        if (payload.RecoveryPointId != point.PointId || payload.OwnerId != owner || payload.LibraryId != library ||
                            payload.ManifestSha256 != digest || payload.DataClass != dataClass || payload.ObservedAtUtc > now)
                        {
                            var code = payload.ObservedAtUtc > now ? "future_timestamp" : "observation_rejected";
                            exceptions.Add(code);
                            observations[dataClass] = Observation.Rejected(code);
                            continue;
                        }
                        if (!expected.TryGetValue(dataClass, out var target) ||
                            payload.ExpectedItemCount != target.ItemCount || payload.ExpectedByteLength != target.ByteLength ||
                            payload.ExpectedItemCount != target.CatalogObservedCount || payload.ExpectedByteLength != target.CatalogObservedBytes ||
                            payload.ObservedItemCount != target.CatalogObservedCount || payload.ObservedByteLength != target.CatalogObservedBytes ||
                            payload.Outcome != target.Outcome)
                        {
                            exceptions.Add("observation_rejected");
                            observations[dataClass] = Observation.Rejected("observation_rejected");
                            continue;
                        }
                        var safe = payload.SafeCodes.Where(SafeCodes.Contains).Distinct(StringComparer.Ordinal).ToArray();
                        observations[dataClass] = new(payload.Outcome, payload.ObservedAtUtc, payload.ObservedItemCount,
                            payload.ObservedByteLength, safe, true, null);
                        foreach (var code in safe) exceptions.Add(code);
                    }
                    catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
                    {
                        exceptions.Add("observation_rejected");
                        observations[dataClass] = Observation.Rejected("observation_rejected");
                    }
                }
            }

            await using var alertCommand = Command(connection, """
                SELECT worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature
                FROM recovery_observations
                WHERE recovery_point_id=$1 AND owner_id=$2 AND library_id=$3 AND manifest_sha256=$4
                  AND import_state='verified' AND alert_delivery IS NOT NULL
                ORDER BY observed_at_utc DESC,observation_id DESC LIMIT 1
                """, point.PointId, owner, library, digest);
            await using var alertReader = await alertCommand.ExecuteReaderAsync(ct);
            if (await alertReader.ReadAsync(ct))
            {
                try
                {
                    var payload = RecoveryEvidenceCodec.VerifyObservation(ReadObservationEnvelope(alertReader), trust).Payload;
                    if (payload.RecoveryPointId == point.PointId && payload.OwnerId == owner && payload.LibraryId == library &&
                        payload.ManifestSha256 == digest && payload.ObservedAtUtc <= now && payload.AlertDelivery is { } alert)
                    {
                        if (alert.DeliveryAtUtc is { } delivered && delivered > now ||
                            alert.ProviderAcceptedAtUtc is { } accepted && accepted > now || alert.ThresholdAtUtc > now)
                            exceptions.Add("future_timestamp");
                        else if (alert.DeliveryStatus == "delivered") lastAlertAt = alert.DeliveryAtUtc;
                    }
                    else if (payload.ObservedAtUtc > now) exceptions.Add("future_timestamp");
                }
                catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
                {
                    exceptions.Add("observation_rejected");
                }
            }
        }

        var result = new List<RecoveryHealthClass>(Classes.Length);
        foreach (var dataClass in Classes)
        {
            if (activeCycleClass == dataClass)
            {
                result.Add(new(dataClass, "incomplete", null, ageSeconds, 0d, "unknown"));
                continue;
            }
            if (catalog is null || expected is null || !expected.TryGetValue(dataClass, out var target))
            {
                result.Add(new(dataClass, "integrity_failed", null, ageSeconds, null, "failed"));
                continue;
            }
            if (!observations.TryGetValue(dataClass, out var observed))
            {
                result.Add(new(dataClass, "incomplete", null, ageSeconds, 0d, "unknown"));
                exceptions.Add("class_missing");
                continue;
            }

            if (!observed.SignatureVerified)
            {
                result.Add(new(dataClass, "integrity_failed", null, ageSeconds, 0d, "failed"));
                continue;
            }
            var coverage = target.ItemCount == 0
                ? (observed.ItemCount == 0 && target.ByteLength == 0 && observed.ByteLength == 0 ? 1d : 0d)
                : Math.Clamp((double)observed.ItemCount / target.ItemCount, 0d, 1d);
            var exact = observed.ItemCount == target.CatalogObservedCount && observed.ByteLength == target.CatalogObservedBytes &&
                target.ItemCount == target.CatalogObservedCount && target.ByteLength == target.CatalogObservedBytes;
            var classIntegrity = observed.Outcome == "corrupt" || observed.SafeCodes.Contains("copy_corrupt", StringComparer.Ordinal) ? "failed" : observed.Outcome == "healthy" && exact ? "verified" : "unknown";
            var classState = classIntegrity == "failed" ? "integrity_failed"
                : observed.Outcome != "healthy" || !exact ? "incomplete"
                : ageSeconds is { } classAge && classAge >= overdueAfter ? "overdue" : "verified";
            if (!exact) exceptions.Add("bytes_mismatch");
            result.Add(new(dataClass, classState, observed.ObservedAtUtc, ageSeconds, coverage, classIntegrity));
        }

        if (result.Any(item => item.State == "incomplete") && observations.Count < Classes.Length) exceptions.Add("class_missing");
        var status = result.All(item => item.State == "verified") && exceptions.Count == 0 ? "healthy" : "degraded";
        return new(status, now, result, exceptions.Order(StringComparer.Ordinal).ToArray(), lastAlertAt);
    }

    private static async Task<RecoveryPointRow?> ReadLatestPoint(NpgsqlConnection connection, string owner, string library, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT p.point_id,p.capture_id,p.owner_id,p.library_id,p.source_region,p.captured_at_utc,
                   p.snapshot_id,p.snapshot_lsn::text,p.snapshot_cutoff_utc,p.change_sequence,p.latest_changed_at_utc,
                   p.migration_digest,p.manifest_schema_version,p.manifest_sha256,p.state,p.verified_at_utc,
                   a.worker_id,a.signing_key_id,a.signing_public_key_sha256,a.payload_sha256,a.canonical_payload,a.signature
            FROM recovery_points p
            LEFT JOIN recovery_point_attestations a ON a.point_id=p.point_id
            WHERE p.owner_id=$1 AND p.library_id=$2
            ORDER BY p.captured_at_utc DESC,p.point_id DESC LIMIT 1
            """, owner, library);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        RecoveryCatalogEnvelopeV1? attestation = null;
        if (!reader.IsDBNull(16))
        {
            attestation = new RecoveryCatalogEnvelopeV1(
                1,
                "recovery_point",
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18).Trim(),
                reader.GetString(19).Trim(),
                Convert.ToBase64String((byte[])reader[20]),
                Convert.ToBase64String((byte[])reader[21]));
        }
        return new RecoveryPointRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetString(11).Trim(), reader.GetInt32(12), reader.GetString(13).Trim(), reader.GetString(14),
            reader.IsDBNull(15) ? default : reader.GetFieldValue<DateTimeOffset>(15), attestation);
    }

    private static async Task<RecoveryCycleSummary?> ReadLatestCycleAsync(
        NpgsqlConnection connection, string owner, string library, RecoverySigningTrust trust, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature,
                   event_id,capture_id,recovery_point_id,observed_at_utc,stage,failed_data_class,outcome,safe_code,
                   recovery_worker_build_digest
            FROM recovery_cycle_statuses
            WHERE owner_id=$1 AND library_id=$2
            ORDER BY observed_at_utc DESC,event_id DESC LIMIT 1
            """, owner, library);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        try
        {
            var envelope = new RecoveryCycleEnvelopeV1(1, "recovery_cycle", reader.GetString(0), reader.GetString(1),
                reader.GetString(2).Trim(), reader.GetString(3).Trim(), Convert.ToBase64String((byte[])reader[4]),
                Convert.ToBase64String((byte[])reader[5]));
            var verified = RecoveryCycleCodec.Verify(envelope, trust);
            var payload = verified.Payload;
            var matches = payload.EventId == reader.GetGuid(6) &&
                payload.CaptureId == (reader.IsDBNull(7) ? null : reader.GetGuid(7)) &&
                payload.RecoveryPointId == (reader.IsDBNull(8) ? null : reader.GetGuid(8)) &&
                SamePostgresTimestamp(payload.ObservedAtUtc, reader.GetFieldValue<DateTimeOffset>(9)) &&
                payload.Stage == reader.GetString(10) && payload.FailedDataClass == (reader.IsDBNull(11) ? null : reader.GetString(11)) &&
                payload.Outcome == reader.GetString(12) && NormalizeCycleCode(payload) == reader.GetString(13) &&
                payload.RecoveryWorkerBuildDigest == reader.GetString(14).Trim() && payload.OwnerId == owner && payload.LibraryId == library;
            return new(payload.ObservedAtUtc, payload.FailedDataClass,
                NormalizeCycleCode(payload), matches);
        }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
        {
            return new(DateTimeOffset.MinValue, null, "recovery_cycle_invalid", false);
        }
    }

    private static bool MatchesPoint(RecoveryCatalogPayloadV1 payload, RecoveryPointRow point) =>
        payload.RecoveryPointId == point.PointId &&
        payload.CaptureId == point.CaptureId &&
        payload.OwnerId == point.OwnerId &&
        payload.LibraryId == point.LibraryId &&
        payload.SourceRegion == point.SourceRegion &&
        SamePostgresTimestamp(payload.CapturedAtUtc, point.CapturedAtUtc) &&
        payload.SnapshotId == point.SnapshotId &&
        payload.SnapshotLsn == point.SnapshotLsn &&
        SamePostgresTimestamp(payload.SnapshotCutoffUtc, point.SnapshotCutoffUtc) &&
        payload.ChangeSequence == point.ChangeSequence &&
        SameNullablePostgresTimestamp(payload.LatestChangedAtUtc, point.LatestChangedAtUtc) &&
        payload.MigrationDigest == point.MigrationDigest &&
        point.ManifestSchemaVersion == RecoveryCatalogPayloadV1.CurrentSchemaVersion &&
        payload.FullManifestSha256 == point.ManifestSha256 &&
        point.State == "verified" &&
        SamePostgresTimestamp(payload.VerifiedAtUtc, point.VerifiedAtUtc);

    private static bool SameNullablePostgresTimestamp(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right is null : right is not null && SamePostgresTimestamp(left.Value, right.Value);

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime().Ticks / 10 == right.ToUniversalTime().Ticks / 10;

    private static string NormalizeCycleCode(RecoveryCycleEventV1 payload) => payload.SafeCode switch
    {
        "capture_overdue" or "class_missing" or "manifest_mismatch" or "bytes_mismatch" or "graph_mismatch" or
            "audit_chain_mismatch" or "access_policy_failed" or "kms_unavailable" or "cold_retrieval_pending" or
            "copy_corrupt" or "alert_delivery_failed" or "observation_rejected" or "recovery_point_incomplete" or
            "recovery_point_integrity_failed" or "recovery_point_unavailable" or "recovery_copy_pending" or
            "future_timestamp" or "catalog_missing" or "catalog_invalid" or "capture_failed" or "copy_failed" or
            "verification_failed" or "publication_failed" or "snapshot_failed" or "coverage_incomplete" => payload.SafeCode,
        _ => payload.Stage switch
        {
            "capture" => "capture_failed",
            "copy" => "copy_failed",
            "verification" => "verification_failed",
            "publication" => "publication_failed",
            _ => "capture_failed"
        }
    };

    private static RecoveryObservationEnvelopeV1 ReadObservationEnvelope(NpgsqlDataReader reader) => new(
        1,
        "observation",
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2).Trim(),
        reader.GetString(3).Trim(),
        Convert.ToBase64String((byte[])reader[4]),
        Convert.ToBase64String((byte[])reader[5]));

    public static bool TryGetTrust(IConfiguration config, out RecoverySigningTrust trust)
    {
        var keyId = config["Recovery:ObservationTrust:KeyId"];
        var publicKey = config["Recovery:ObservationTrust:PublicKeySpkiBase64"];
        var fingerprint = config["Recovery:ObservationTrust:PublicKeySha256"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(fingerprint))
        {
            trust = null!;
            return false;
        }
        trust = new RecoverySigningTrust(keyId, publicKey, fingerprint);
        try { _ = trust.ValidateAndGetPublicKey(); return true; }
        catch (InvalidDataException) { trust = null!; return false; }
    }

    private static RecoveryHealthView Empty(DateTimeOffset now, IReadOnlySet<string> codes, string? failedClass = null) => new("degraded", now,
        Classes.Select(dataClass => new RecoveryHealthClass(dataClass, "incomplete", null, null, null, "unknown")).ToArray(),
        codes.Order(StringComparer.Ordinal).ToArray(), null);

    private sealed record RecoveryPointRow(
        Guid PointId, Guid CaptureId, string OwnerId, string LibraryId, string SourceRegion, DateTimeOffset CapturedAtUtc,
        string SnapshotId, string SnapshotLsn, DateTimeOffset SnapshotCutoffUtc, long ChangeSequence, DateTimeOffset? LatestChangedAtUtc,
        string MigrationDigest, int ManifestSchemaVersion, string ManifestSha256, string State, DateTimeOffset VerifiedAtUtc,
        RecoveryCatalogEnvelopeV1? Attestation);
    private sealed record RecoveryCycleSummary(DateTimeOffset ObservedAtUtc, string? FailedDataClass, string SafeCode, bool Valid);
    internal sealed record ExpectedCoverage(long ItemCount, long ByteLength, long CatalogObservedCount, long CatalogObservedBytes, string Outcome);
    private sealed record Observation(string Outcome, DateTimeOffset ObservedAtUtc, long ItemCount, long ByteLength,
        IReadOnlyList<string> SafeCodes, bool SignatureVerified, string? RejectionCode)
    {
        public static Observation Rejected(string code) => new("failed", default, 0, 0, [], false, code);
    }
}
