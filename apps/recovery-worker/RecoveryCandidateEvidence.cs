using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryCandidateEvidence
{
    internal static async Task<PublishedS3Object> PersistAsync(IAmazonS3 s3, NpgsqlConnection connection,
        RecoveryWorkerConfiguration configuration, Guid candidateId, VerifiedCopiedPoint point,
        IReadOnlyList<RecoveryMaterializedObjectV1> materialized, IRecoveryEvidenceSigner signer,
        RecoverySigningTrust trust,
        CancellationToken cancellationToken)
    {
        await RecoveryCandidateStore.RebindAsync(connection, candidateId, materialized, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT to_jsonb(c)::text,v.validated_at_utc FROM recovery_candidate_sessions c
            JOIN recovery_candidate_validation_revisions v ON v.candidate_id=c.candidate_id AND v.revision=1 AND v.state='verified'
            WHERE c.candidate_id=$1 AND c.state='owner_access_pending' AND c.expires_at_utc>now()
              AND c.source_manifest_sha256=$2 AND c.target_database_name=current_database()
            """, connection);
        command.Parameters.AddWithValue(candidateId);
        command.Parameters.AddWithValue(point.SourceManifest.Digest());
        string json;
        DateTimeOffset verifiedAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new RecoveryWorkerException("candidate_evidence_session_missing");
            json = reader.GetString(0);
            verifiedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }
        using var session = JsonDocument.Parse(json);
        var sourceClasses = point.SourceManifest.Objects.ToDictionary(item => (item.SourceKey, item.SourceVersionId),
            item => item.DataClass.ToString());
        var writer = new ReceiptJournalWriter(s3, configuration.Required("EvidenceBucket"),
            configuration.Required("EvidenceKmsKeyId"), candidateId);
        foreach (var receipt in materialized)
        {
            receipt.Validate();
            if (!sourceClasses.TryGetValue((receipt.SourceKey, receipt.SourceVersionId), out var dataClass))
                throw new RecoveryWorkerException("candidate_evidence_source_mismatch");
            await writer.AppendAsync(new(receipt.SourceBucket, receipt.SourceKey, receipt.SourceVersionId,
                receipt.Sha256, receipt.ByteLength, receipt.DestinationBucket, receipt.DestinationKey,
                receipt.DestinationVersionId, dataClass, verifiedAt), cancellationToken);
        }
        var journal = await writer.CompleteAsync(cancellationToken);
        var payload = RecoveryEvidenceCodec.SerializeCanonical(new
        {
            schemaVersion = 1, candidateId, state = "owner_access_pending", verifiedAtUtc = verifiedAt,
            session = session.RootElement, sourceCheckpointSha256 = point.CheckpointEnvelopeSha256,
            receiptCount = journal.Count, receiptChunks = journal.Chunks,
            recoveryWorkerBuildDigest = configuration.WorkerBuildDigest
        });
        // Base64 plus envelope fields must fit the same bounded retry reader.
        if (payload.Length > 3 * 1024 * 1024 - 8192)
            throw new RecoveryWorkerException("candidate_evidence_size_limit");
        var key = $"evidence/drills/{candidateId:D}/candidate.v1.json";
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = configuration.Required("EvidenceBucket"), Key = key }, cancellationToken);
            if (string.IsNullOrWhiteSpace(metadata.VersionId) || metadata.VersionId == "null" ||
                metadata.ContentLength is < 1 or > 4 * 1024 * 1024)
                throw new RecoveryWorkerException("candidate_evidence_existing_invalid");
            using var response = await s3.GetObjectAsync(new GetObjectRequest
                { BucketName = configuration.Required("EvidenceBucket"), Key = key, VersionId = metadata.VersionId }, cancellationToken);
            if (response.VersionId != metadata.VersionId || response.ContentLength != metadata.ContentLength)
                throw new RecoveryWorkerException("candidate_evidence_existing_invalid");
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + read > metadata.ContentLength) throw new RecoveryWorkerException("candidate_evidence_existing_invalid");
                body.Write(buffer, 0, read);
            }
            var bytes = body.ToArray();
            var existingEnvelope = JsonSerializer.Deserialize<PrivateSignedCheckpointV1>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (bytes.LongLength != metadata.ContentLength || existingEnvelope is null || existingEnvelope.SchemaVersion != 1 ||
                existingEnvelope.MessageType != "recovery_unqualified_candidate" || existingEnvelope.WorkerId != configuration.WorkerId ||
                existingEnvelope.SigningKeyId != signer.KeyId || existingEnvelope.SigningPublicKeySha256 != signer.PublicKeySha256 ||
                !RecoveryEvidenceCodec.SerializeCanonical(existingEnvelope).AsSpan().SequenceEqual(bytes) ||
                !Convert.FromBase64String(existingEnvelope.CanonicalPayloadBase64).AsSpan().SequenceEqual(payload))
                throw new RecoveryWorkerException("candidate_evidence_replay_mismatch");
            _ = RecoveryEvidenceCodec.VerifyDetached(payload, existingEnvelope.PayloadSha256, existingEnvelope.SignatureBase64,
                existingEnvelope.SigningKeyId, existingEnvelope.SigningPublicKeySha256, trust);
            return new(key, metadata.VersionId, RecoveryEvidenceCodec.Hash(bytes), bytes.LongLength);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey") { }
        var signature = await signer.SignDigestAsync(SHA256.HashData(payload), cancellationToken);
        var envelope = new PrivateSignedCheckpointV1(1, "recovery_unqualified_candidate", configuration.WorkerId,
            signer.KeyId, signer.PublicKeySha256, RecoveryEvidenceCodec.Hash(payload),
            Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        // Copy checkpoint parsers reject this distinct message type. It has no healthy
        // catalog, promotion or primary publication authority. Exact receipts survive
        // the ephemeral clone's cleanup in the encrypted retained evidence bucket.
        var envelopeBytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
        if (envelopeBytes.Length > 4 * 1024 * 1024)
            throw new RecoveryWorkerException("candidate_evidence_size_limit");
        return await S3ArtifactWriter.PutBytesAsync(s3, configuration.Required("EvidenceBucket"),
            key, envelopeBytes,
            configuration.Required("EvidenceKmsKeyId"), cancellationToken);
    }
}
