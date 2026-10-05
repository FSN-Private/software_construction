using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Platform.Recovery;
using Platform.Storage;

namespace Platform.Api.Features.Recovery;

public sealed class RecoveryCatalogPendingException() : Exception("recovery_catalog_pending");

public sealed class RecoveryObservationImporter(
    NpgsqlDataSource source,
    IConfiguration configuration,
    TimeProvider? timeProvider = null)
{
    private const int MaximumEnvelopeBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task ImportAsync(string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));
        if (bodyBytes.Length > MaximumEnvelopeBytes)
        {
            await RejectAsync(bodyHash, "invalid_envelope", cancellationToken);
            return;
        }

        if (!RecoveryHealthService.TryGetTrust(configuration, out var trust))
            throw new InvalidOperationException("recovery_observation_trust_unconfigured");

        string messageType;
        try
        {
            using var document = JsonDocument.Parse(bodyBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("messageType", out var discriminator) ||
                discriminator.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("invalid_message_discriminator");
            messageType = discriminator.GetString()!;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            await RejectAsync(bodyHash, "invalid_envelope", cancellationToken);
            return;
        }

        switch (messageType)
        {
            case "recovery_point":
                await ImportCatalogAsync(bodyBytes, bodyHash, trust, cancellationToken);
                return;
            case "recovery_cycle":
                await ImportCycleAsync(bodyBytes, bodyHash, trust, cancellationToken);
                return;
            case "observation":
                await ImportObservationAsync(bodyBytes, bodyHash, trust, cancellationToken);
                return;
            default:
                await RejectAsync(bodyHash, "invalid_envelope", cancellationToken);
                return;
        }
    }

    private async Task ImportCatalogAsync(byte[] bodyBytes, string bodyHash, RecoverySigningTrust trust, CancellationToken ct)
    {
        VerifiedRecoveryCatalog verified;
        try { verified = RecoveryCatalogCodec.Verify(RecoveryCatalogCodec.ParseEnvelope(bodyBytes), trust); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
        {
            await RejectAsync(bodyHash, InvalidCode(error), ct);
            return;
        }

        var payload = verified.Payload;
        var now = clock.GetUtcNow();
        if (payload.CapturedAtUtc > now || payload.SnapshotCutoffUtc > now || payload.VerifiedAtUtc > now)
        {
            await RejectAsync(bodyHash, "invalid_payload", ct);
            return;
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var existing = new NpgsqlCommand("""
            SELECT p.point_id,p.capture_id,p.owner_id,p.library_id,p.source_region,p.captured_at_utc,
                   p.snapshot_id,p.snapshot_lsn::text,p.snapshot_cutoff_utc,p.change_sequence,p.latest_changed_at_utc,
                   p.migration_digest,p.manifest_schema_version,p.manifest_sha256,p.manifest_bytes,p.state,p.verified_at_utc,
                   a.worker_id,a.signing_key_id,a.signing_public_key_sha256,a.payload_sha256,a.canonical_payload,a.signature
            FROM recovery_points p
            LEFT JOIN recovery_point_attestations a ON a.point_id=p.point_id
            WHERE p.point_id=$1 OR p.capture_id=$2
            FOR UPDATE OF p
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue(payload.RecoveryPointId);
            existing.Parameters.AddWithValue(payload.CaptureId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var same = await ExistingCatalogMatchesAsync(reader, verified, payload, ct);
                await reader.DisposeAsync();
                if (!same)
                {
                    await RejectAsync(connection, bodyHash, "replay_conflict", ct, transaction);
                    await transaction.CommitAsync(ct);
                    return;
                }
                await transaction.CommitAsync(ct);
                return;
            }
        }

        var safeJson = Encoding.UTF8.GetString(verified.CanonicalPayloadBytes);
        await using (var insertPoint = new NpgsqlCommand("""
            INSERT INTO recovery_points(
                point_id,capture_id,owner_id,library_id,source_region,captured_at_utc,snapshot_id,snapshot_lsn,
                snapshot_cutoff_utc,change_sequence,latest_changed_at_utc,migration_digest,manifest_schema_version,
                manifest_sha256,manifest_bytes,manifest,state,verified_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8::pg_lsn,$9,$10,$11,$12,$13,$14,$15,$16,'verified',$17)
            """, connection, transaction))
        {
            insertPoint.Parameters.AddWithValue(payload.RecoveryPointId);
            insertPoint.Parameters.AddWithValue(payload.CaptureId);
            insertPoint.Parameters.AddWithValue(payload.OwnerId);
            insertPoint.Parameters.AddWithValue(payload.LibraryId);
            insertPoint.Parameters.AddWithValue(payload.SourceRegion);
            insertPoint.Parameters.AddWithValue(payload.CapturedAtUtc);
            insertPoint.Parameters.AddWithValue(payload.SnapshotId);
            insertPoint.Parameters.AddWithValue(payload.SnapshotLsn);
            insertPoint.Parameters.AddWithValue(payload.SnapshotCutoffUtc);
            insertPoint.Parameters.AddWithValue(payload.ChangeSequence);
            insertPoint.Parameters.AddWithValue(payload.LatestChangedAtUtc is null ? DBNull.Value : payload.LatestChangedAtUtc.Value);
            insertPoint.Parameters.AddWithValue(payload.MigrationDigest);
            insertPoint.Parameters.AddWithValue(payload.SchemaVersion);
            insertPoint.Parameters.AddWithValue(payload.FullManifestSha256);
            insertPoint.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
            insertPoint.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = safeJson });
            insertPoint.Parameters.AddWithValue(payload.VerifiedAtUtc);
            await insertPoint.ExecuteNonQueryAsync(ct);
        }

        await using (var insertAttestation = new NpgsqlCommand("""
            INSERT INTO recovery_point_attestations(
                point_id,worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            """, connection, transaction))
        {
            insertAttestation.Parameters.AddWithValue(payload.RecoveryPointId);
            insertAttestation.Parameters.AddWithValue(verified.WorkerId);
            insertAttestation.Parameters.AddWithValue(verified.SigningKeyId);
            insertAttestation.Parameters.AddWithValue(verified.SigningPublicKeySha256);
            insertAttestation.Parameters.AddWithValue(verified.PayloadSha256);
            insertAttestation.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
            insertAttestation.Parameters.AddWithValue(verified.Signature);
            await insertAttestation.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> ExistingCatalogMatchesAsync(
        NpgsqlDataReader reader, VerifiedRecoveryCatalog verified, RecoveryCatalogPayloadV1 payload, CancellationToken ct)
    {
        if (reader.IsDBNull(17)) return false;
        var exact = reader.GetGuid(0) == payload.RecoveryPointId &&
            reader.GetGuid(1) == payload.CaptureId &&
            reader.GetString(2) == payload.OwnerId &&
            reader.GetString(3) == payload.LibraryId &&
            reader.GetString(4) == payload.SourceRegion &&
            SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(5), payload.CapturedAtUtc) &&
            reader.GetString(6) == payload.SnapshotId &&
            reader.GetString(7) == payload.SnapshotLsn &&
            SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(8), payload.SnapshotCutoffUtc) &&
            reader.GetInt64(9) == payload.ChangeSequence &&
            SameNullablePostgresTimestamp(reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10), payload.LatestChangedAtUtc) &&
            reader.GetString(11).Trim() == payload.MigrationDigest &&
            reader.GetInt32(12) == payload.SchemaVersion &&
            reader.GetString(13).Trim() == payload.FullManifestSha256 &&
            ((byte[])reader[14]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) &&
            reader.GetString(15) == "verified" &&
            SamePostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(16), payload.VerifiedAtUtc) &&
            reader.GetString(17) == verified.WorkerId &&
            reader.GetString(18) == verified.SigningKeyId &&
            reader.GetString(19).Trim() == verified.SigningPublicKeySha256 &&
            reader.GetString(20).Trim() == verified.PayloadSha256 &&
            ((byte[])reader[21]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) &&
            ((byte[])reader[22]).AsSpan().SequenceEqual(verified.Signature);
        await Task.CompletedTask;
        return exact;
    }

    private async Task ImportCycleAsync(byte[] bodyBytes, string bodyHash, RecoverySigningTrust trust, CancellationToken ct)
    {
        VerifiedRecoveryCycle verified;
        try { verified = RecoveryCycleCodec.Verify(RecoveryCycleCodec.ParseEnvelope(bodyBytes), trust); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException)
        {
            await RejectAsync(bodyHash, InvalidCode(error), ct);
            return;
        }

        var payload = verified.Payload;
        if (payload.ObservedAtUtc > clock.GetUtcNow())
        {
            await RejectAsync(bodyHash, "invalid_payload", ct);
            return;
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO recovery_cycle_statuses(
                event_id,worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature,
                capture_id,recovery_point_id,owner_id,library_id,observed_at_utc,stage,failed_data_class,outcome,
                safe_code,recovery_worker_build_digest)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
            ON CONFLICT (event_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(payload.EventId);
            insert.Parameters.AddWithValue(verified.WorkerId);
            insert.Parameters.AddWithValue(verified.SigningKeyId);
            insert.Parameters.AddWithValue(verified.SigningPublicKeySha256);
            insert.Parameters.AddWithValue(verified.PayloadSha256);
            insert.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
            insert.Parameters.AddWithValue(verified.Signature);
            insert.Parameters.AddWithValue(payload.CaptureId is null ? DBNull.Value : payload.CaptureId.Value);
            insert.Parameters.AddWithValue(payload.RecoveryPointId is null ? DBNull.Value : payload.RecoveryPointId.Value);
            insert.Parameters.AddWithValue(payload.OwnerId);
            insert.Parameters.AddWithValue(payload.LibraryId);
            insert.Parameters.AddWithValue(payload.ObservedAtUtc);
            insert.Parameters.AddWithValue(payload.Stage);
            insert.Parameters.AddWithValue(payload.FailedDataClass is null ? DBNull.Value : payload.FailedDataClass);
            insert.Parameters.AddWithValue(payload.Outcome);
            insert.Parameters.AddWithValue(NormalizeCycleCode(payload));
            insert.Parameters.AddWithValue(payload.RecoveryWorkerBuildDigest);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var existing = new NpgsqlCommand("""
            SELECT worker_id,signing_key_id,signing_public_key_sha256,payload_sha256,canonical_payload,signature
            FROM recovery_cycle_statuses WHERE event_id=$1
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue(payload.EventId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) ||
                reader.GetString(0) != verified.WorkerId ||
                reader.GetString(1) != verified.SigningKeyId ||
                reader.GetString(2).Trim() != verified.SigningPublicKeySha256 ||
                reader.GetString(3).Trim() != verified.PayloadSha256 ||
                !((byte[])reader[4]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) ||
                !((byte[])reader[5]).AsSpan().SequenceEqual(verified.Signature))
            {
                await reader.DisposeAsync();
                await RejectAsync(connection, bodyHash, "replay_conflict", ct, transaction);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private async Task ImportObservationAsync(byte[] bodyBytes, string bodyHash, RecoverySigningTrust trust, CancellationToken ct)
    {
        RecoveryObservationEnvelopeV1 envelope;
        VerifiedRecoveryObservation verified;
        try
        {
            envelope = RecoveryEvidenceCodec.ParseObservationEnvelope(bodyBytes);
            verified = RecoveryEvidenceCodec.VerifyObservation(envelope, trust);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or CryptographicException or FormatException)
        {
            await RejectAsync(bodyHash, InvalidCode(error), ct);
            return;
        }

        var payload = verified.Payload;
        if (payload.ObservedAtUtc > clock.GetUtcNow())
        {
            await RejectAsync(bodyHash, "invalid_payload", ct);
            return;
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        var catalog = await ReadCatalogAsync(connection, payload.RecoveryPointId, payload.OwnerId, payload.LibraryId, trust, ct);
        if (catalog is null)
            throw new RecoveryCatalogPendingException();

        if (payload.ManifestSha256 != catalog.Payload.FullManifestSha256 ||
            payload.ObservedAtUtc < catalog.Payload.SnapshotCutoffUtc ||
            !catalog.Payload.Coverage.Any(target => target.DataClass == payload.DataClass &&
                target.ExpectedItemCount == payload.ExpectedItemCount &&
                target.ExpectedByteLength == payload.ExpectedByteLength &&
                target.ObservedItemCount == payload.ObservedItemCount &&
                target.ObservedByteLength == payload.ObservedByteLength &&
                target.Outcome == payload.Outcome &&
                target.SafeCodes.SequenceEqual(payload.SafeCodes, StringComparer.Ordinal)))
        {
            await RejectAsync(connection, bodyHash, "manifest_mismatch", ct);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        var canonicalJson = Encoding.UTF8.GetString(verified.CanonicalPayloadBytes);
        var alertJson = payload.AlertDelivery is null ? null : JsonSerializer.Serialize(payload.AlertDelivery, JsonOptions);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO recovery_observations(
                observation_id,worker_id,signing_key_id,signing_public_key_sha256,observed_at_utc,
                recovery_point_id,owner_id,library_id,manifest_sha256,data_class,outcome,
                expected_item_count,expected_byte_length,item_count,byte_length,safe_codes,alert_delivery,
                payload,canonical_payload,payload_sha256,signature,import_state,safe_result_code)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,'verified','verified')
            ON CONFLICT DO NOTHING
            RETURNING observation_id
            """, connection, transaction);
        insert.Parameters.AddWithValue(payload.ObservationId);
        insert.Parameters.AddWithValue(verified.WorkerId);
        insert.Parameters.AddWithValue(verified.SigningKeyId);
        insert.Parameters.AddWithValue(verified.SigningPublicKeySha256);
        insert.Parameters.AddWithValue(payload.ObservedAtUtc);
        insert.Parameters.AddWithValue(payload.RecoveryPointId);
        insert.Parameters.AddWithValue(payload.OwnerId);
        insert.Parameters.AddWithValue(payload.LibraryId);
        insert.Parameters.AddWithValue(payload.ManifestSha256);
        insert.Parameters.AddWithValue(payload.DataClass);
        insert.Parameters.AddWithValue(payload.Outcome);
        insert.Parameters.AddWithValue(payload.ExpectedItemCount);
        insert.Parameters.AddWithValue(payload.ExpectedByteLength);
        insert.Parameters.AddWithValue(payload.ObservedItemCount);
        insert.Parameters.AddWithValue(payload.ObservedByteLength);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(payload.SafeCodes, JsonOptions) });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = alertJson is null ? DBNull.Value : alertJson });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = canonicalJson });
        insert.Parameters.AddWithValue(verified.CanonicalPayloadBytes);
        insert.Parameters.AddWithValue(verified.PayloadSha256);
        insert.Parameters.AddWithValue(verified.Signature);
        var inserted = await insert.ExecuteScalarAsync(ct);
        if (inserted is null)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT worker_id,canonical_payload,payload_sha256,signature,signing_key_id,signing_public_key_sha256
                FROM recovery_observations WHERE observation_id=$1
                """, connection, transaction);
            existing.Parameters.AddWithValue(payload.ObservationId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            var same = await reader.ReadAsync(ct) &&
                reader.GetString(0) == verified.WorkerId &&
                ((byte[])reader[1]).AsSpan().SequenceEqual(verified.CanonicalPayloadBytes) &&
                reader.GetString(2).Trim() == verified.PayloadSha256 &&
                ((byte[])reader[3]).AsSpan().SequenceEqual(verified.Signature) &&
                reader.GetString(4) == verified.SigningKeyId &&
                reader.GetString(5).Trim() == verified.SigningPublicKeySha256;
            await reader.DisposeAsync();
            if (!same) await RejectAsync(connection, bodyHash, "replay_conflict", ct, transaction);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<VerifiedRecoveryCatalog?> ReadCatalogAsync(
        NpgsqlConnection connection, Guid pointId, string owner, string library, RecoverySigningTrust trust, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.manifest_sha256,p.state,p.owner_id,p.library_id,p.capture_id,p.snapshot_cutoff_utc,
                   a.worker_id,a.signing_key_id,a.signing_public_key_sha256,a.payload_sha256,a.canonical_payload,a.signature
            FROM recovery_points p
            LEFT JOIN recovery_point_attestations a ON a.point_id=p.point_id
            WHERE p.point_id=$1 AND p.owner_id=$2 AND p.library_id=$3
            """, connection);
        command.Parameters.AddWithValue(pointId);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(library);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(6) || reader.GetString(1) != "verified") return null;

        var envelope = new RecoveryCatalogEnvelopeV1(1, "recovery_point", reader.GetString(6), reader.GetString(7),
            reader.GetString(8).Trim(), reader.GetString(9).Trim(),
            Convert.ToBase64String((byte[])reader[10]), Convert.ToBase64String((byte[])reader[11]));
        VerifiedRecoveryCatalog catalog;
        try { catalog = RecoveryCatalogCodec.Verify(envelope, trust); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException or FormatException or JsonException) { return null; }
        var payload = catalog.Payload;
        if (payload.RecoveryPointId != pointId || payload.OwnerId != owner || payload.LibraryId != library ||
            payload.CaptureId != reader.GetGuid(4) || payload.FullManifestSha256 != reader.GetString(0).Trim() ||
            payload.SnapshotCutoffUtc.ToUniversalTime().Ticks / 10 != reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime().Ticks / 10)
            return null;
        return catalog;
    }

    private static bool SameNullablePostgresTimestamp(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right is null : right is not null &&
        left.Value.ToUniversalTime().Ticks / 10 == right.Value.ToUniversalTime().Ticks / 10;

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

    private async Task RejectAsync(string bodyHash, string code, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await RejectAsync(connection, bodyHash, code, cancellationToken);
    }

    private static async Task RejectAsync(NpgsqlConnection connection, string bodyHash, string code,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO recovery_observation_rejections(rejection_id,body_sha256,safe_code)
            VALUES ($1,$2,$3) ON CONFLICT (body_sha256,safe_code) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(bodyHash);
        command.Parameters.AddWithValue(code);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string InvalidCode(Exception error) => error.Message switch
    {
        "untrusted_evidence_signer" or "trusted_public_key_fingerprint_mismatch" or "invalid_trusted_public_key" => "untrusted_signer",
        "evidence_signature_invalid" or "invalid_evidence_signature" => "invalid_signature",
        "evidence_payload_digest_mismatch" => "digest_mismatch",
        "observation_payload_noncanonical" or "observation_payload_invalid_json" or "invalid_observation_identity" or
            "catalog_payload_noncanonical" or "catalog_payload_invalid_json" or "recovery_cycle_payload_noncanonical" or
            "recovery_cycle_payload_invalid_json" => "invalid_payload",
        _ => "invalid_envelope",
    };
}

public sealed class RecoveryObservationQueueWorker(
    IAmazonSQS sqs,
    RecoveryObservationImporter importer,
    IConfiguration configuration,
    ILogger<RecoveryObservationQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueUrl = configuration["Recovery:ObservationQueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiveMessageResponse response;
            try
            {
                response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                    VisibilityTimeout = 300,
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (AmazonClientException)
            {
                logger.LogWarning("Recovery observation queue unavailable {RecoveryCode}", "queue_unavailable");
                await Delay(stoppingToken);
                continue;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Recovery observation queue unavailable {RecoveryCode}", "queue_unavailable");
                await Delay(stoppingToken);
                continue;
            }

            foreach (var message in response.Messages ?? [])
            {
                try
                {
                    await importer.ImportAsync(message.Body, stoppingToken);
                    await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (RecoveryCatalogPendingException)
                {
                    logger.LogInformation("Recovery observation awaits signed point catalog {RecoveryCode}", "catalog_pending");
                }
                catch (Exception error) when (error is NpgsqlException or InvalidOperationException or AmazonClientException or TimeoutException)
                {
                    logger.LogWarning("Recovery observation import deferred {RecoveryCode}", "import_deferred");
                }
            }
        }
    }

    private static Task Delay(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
}
