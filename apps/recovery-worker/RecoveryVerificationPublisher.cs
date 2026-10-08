using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryVerificationPublisher
{
    private const int MaximumMessageBytes = 64 * 1024;
    private const int MaximumOutboxBytes = 300 * 1024;
    private static readonly JsonSerializerOptions OutboxJson = new(JsonSerializerDefaults.Web);

    internal static async Task<Dictionary<string, AttributeValue>?> ReadOwnerMarkerAsync(
        IAmazonDynamoDB dynamo, RecoveryWorkerConfiguration configuration, string ownerId, string libraryId,
        CancellationToken cancellationToken) => await GetAsync(dynamo, configuration.Required("VerificationTableName"),
        OwnerKey(ownerId, libraryId), cancellationToken);

    internal static async Task PublishAsync(IAmazonDynamoDB dynamo, IAmazonSQS sqs,
        RecoveryWorkerConfiguration configuration, AwsKmsEvidenceSigner signer,
        VerifiedCopiedPoint point, RecoveryAuditHeadV1 auditHead, CancellationToken cancellationToken)
        => await PublishAsync(dynamo, sqs, configuration, signer, signer.Trust, point, auditHead, cancellationToken);

    internal static async Task PublishAsync(IAmazonDynamoDB dynamo, IAmazonSQS sqs,
        RecoveryWorkerConfiguration configuration, IRecoveryEvidenceSigner signer, RecoverySigningTrust trust,
        VerifiedCopiedPoint point, RecoveryAuditHeadV1 auditHead, CancellationToken cancellationToken)
    {
        if (signer.KeyId != trust.KeyId || signer.PublicKeySha256 != trust.PublicKeySha256)
            throw new RecoveryWorkerException("verification_signer_mismatch");
        if (point.Pointer.State != "copied" || point.FinalManifest is null || point.OwnerAccess is null)
            throw new RecoveryWorkerException("owner_access_proof_required_for_healthy_catalog");
        var queueUrl = ValidateDestination(configuration);
        var table = configuration.Required("VerificationTableName");
        var key = PointKey(point.Pointer.PointId);
        var existing = await GetAsync(dynamo, table, key, cancellationToken);
        List<string> messages;
        if (existing is not null)
        {
            ValidatePointMarker(existing, configuration, point, auditHead, trust);
            var state = String(existing, "state");
            if (state == "pending") messages = ReadAndVerifyOutbox(existing, trust, point);
            else if (state == "delivered") return;
            else throw new RecoveryWorkerException("verification_outbox_state_invalid");
        }
        else
        {
            messages = [];
        }

        if (messages.Count == 0)
        {
            var verifiedAtUtc = DateTimeOffset.UtcNow;
            var catalog = BuildCatalog(configuration, point, verifiedAtUtc);
                var bodies = await BuildOutboxMessagesAsync(catalog, point, configuration, signer, cancellationToken);
            var outboxBytes = JsonSerializer.SerializeToUtf8Bytes(bodies, OutboxJson);
            if (outboxBytes.Length > MaximumOutboxBytes)
                throw new RecoveryWorkerException("verification_outbox_size_limit");
            messages = bodies;
            var item = PointMarker(configuration, point, auditHead, catalog, bodies, outboxBytes);
            try
            {
                await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems = new List<TransactWriteItem>
                    {
                        new()
                        {
                            Put = new Put
                            {
                                TableName = table,
                                Item = item,
                                ConditionExpression = "attribute_not_exists(pk) OR #state=:delivered",
                                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":delivered"] = S("delivered") }
                            }
                        },
                        new() { Update = VerifiedOwnerUpdate(configuration, point, auditHead, catalog, messages[0]) }
                    }
                }, cancellationToken);
            }
            catch (TransactionCanceledException exception)
            {
                existing = await GetAsync(dynamo, table, key, cancellationToken);
                if (existing is null) throw new RecoveryWorkerException("verified_point_marker_monotonic_conflict", exception);
                ValidatePointMarker(existing, configuration, point, auditHead, trust);
                if (String(existing, "state") == "delivered") return;
                if (String(existing, "state") != "pending")
                    throw new RecoveryWorkerException("verification_outbox_state_invalid", exception);
                messages = ReadAndVerifyOutbox(existing, trust, point);
            }
        }

        var latest = await GetAsync(dynamo, table, OwnerKey(point.Pointer.OwnerId, point.Pointer.LibraryId), cancellationToken);
        if (latest is null || String(latest, "pointId") != point.Pointer.PointId.ToString("D").ToLowerInvariant())
        {
            if (latest is not null && IsLater(latest, point.SourceManifest))
            {
                await MarkSupersededAsync(dynamo, table, point.Pointer.PointId, cancellationToken);
                return;
            }
            throw new RecoveryWorkerException("verified_owner_marker_missing");
        }

        for (var i = 0; i < messages.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messages[i]
            }, cancellationToken);
        }
        await MarkDeliveredAsync(dynamo, table, point.Pointer.PointId, point.Pointer.SourceManifestSha256,
            point.FinalManifest?.Digest() ?? point.SourceManifest.Digest(), cancellationToken);
    }

    internal static RecoveryCatalogPayloadV1 BuildCatalog(RecoveryWorkerConfiguration configuration,
        VerifiedCopiedPoint point, DateTimeOffset verifiedAtUtc)
    {
        if (point.Pointer.State != "copied" || point.FinalManifest is null || point.OwnerAccess is null)
            throw new RecoveryWorkerException("owner_access_proof_required_for_healthy_catalog");
        var manifest = point.SourceManifest;
        var finalDigest = point.Pointer.ManifestSha256;
        var combined = RecoveryEvidenceCodec.ObservationClasses.ToDictionary(name => name,
            _ => new RecoveryClassTotal(0, 0), StringComparer.Ordinal);
        foreach (var item in point.FinalManifest.Coverage)
        {
            var name = item.DataClass switch
            {
                RecoveryDataClass.OriginalImage => "originals",
                RecoveryDataClass.XmpSidecar => "xmp",
                RecoveryDataClass.VersionGraph => "version_graph",
                RecoveryDataClass.LibraryMetadata => "metadata",
                RecoveryDataClass.AuditRecord => "audit",
                RecoveryDataClass.Configuration => "configuration",
                RecoveryDataClass.PreviewDerivative => throw new RecoveryWorkerException("disposable_class_in_manifest"),
                _ => "recovery_records"
            };
            var previous = combined[name];
            combined[name] = new(checked(previous.Count + item.ItemCount), checked(previous.Bytes + item.ByteLength));
        }
        var coverage = RecoveryEvidenceCodec.ObservationClasses.Select(name =>
        {
            var total = combined[name];
            return new RecoveryCatalogCoverageV1(name, total.Count, total.Count, total.Bytes, total.Bytes,
                "healthy", []);
        }).ToArray();

        return new RecoveryCatalogPayloadV1(RecoveryCatalogPayloadV1.CurrentSchemaVersion,
            point.Pointer.PointId, point.Pointer.CaptureId, point.Pointer.OwnerId, point.Pointer.LibraryId,
            configuration.RequiredAccountId("SourceAccountId"), configuration.RequiredRegion("SourceRegion"),
            configuration.RequiredAccountId("DestinationAccountId"), configuration.RequiredRegion("DestinationRegion"),
            manifest.CapturedAtUtc, manifest.SnapshotCutoffUtc, manifest.SnapshotId, manifest.SnapshotLsn,
            manifest.ChangeWatermark.LastSequence, manifest.ChangeWatermark.LatestChangedAtUtc,
            manifest.MigrationDigest, configuration.RequiredHash("ApiBuildDigest"),
            configuration.RequiredHash("IngestWorkerBuildDigest"), configuration.WorkerBuildDigest,
            configuration.RequiredHash("RendererBuildDigest"), configuration.RequiredHash("RecoveryTemplateDigest"),
            finalDigest, point.CheckpointPayloadSha256, verifiedAtUtc,
            configuration.Optional("VerifierBuild") ?? configuration.WorkerBuildDigest, coverage);
    }

    internal static RecoveryAuditHeadRequirementV1 ReadAuditHeadRequirement(
        RecoveryWorkerConfiguration configuration, IReadOnlyDictionary<string, AttributeValue>? verifiedMarker)
    {
        if (!long.TryParse(configuration.Required("AuditMinimumSequence"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var minimumSequence) || minimumSequence < 0)
            throw new RecoveryWorkerException("audit_minimum_sequence_invalid");
        var minimum = new RecoveryAuditHeadV1(minimumSequence,
            configuration.RequiredHash("AuditMinimumEventSha256"));
        RecoveryAuditHeadV1? previous = null;
        if (verifiedMarker is not null)
        {
            if (Number(verifiedMarker, "auditHeadSequence") is null || String(verifiedMarker, "auditHeadEventSha256") is null ||
                !long.TryParse(Number(verifiedMarker, "auditHeadSequence"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var priorSequence))
                throw new RecoveryWorkerException("verified_audit_head_missing");
            previous = new(priorSequence, String(verifiedMarker, "auditHeadEventSha256")!);
        }
        var requirement = new RecoveryAuditHeadRequirementV1(minimum, previous);
        try { requirement.Validate(); }
        catch (InvalidDataException exception) { throw new RecoveryWorkerException("audit_head_requirement_invalid", exception); }
        return requirement;
    }

    internal static string ValidateDestination(RecoveryWorkerConfiguration configuration)
    {
        var sourceAccount = configuration.RequiredAccountId("SourceAccountId");
        var sourceRegion = configuration.RequiredRegion("SourceRegion");
        var queueName = $"platform-{configuration.EnvironmentName}-recovery-observations";
        var expectedArn = $"arn:aws:sqs:{sourceRegion}:{sourceAccount}:{queueName}";
        if (configuration.Required("ObservationQueueArn") != expectedArn)
            throw new RecoveryWorkerException("observation_queue_arn_mismatch");
        var url = configuration.Required("ObservationQueueUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != $"sqs.{sourceRegion}.amazonaws.com" || uri.AbsolutePath != $"/{sourceAccount}/{queueName}" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new RecoveryWorkerException("observation_queue_url_mismatch");
        return url;
    }

    private static async Task<List<string>> BuildOutboxMessagesAsync(RecoveryCatalogPayloadV1 catalog,
        VerifiedCopiedPoint point, RecoveryWorkerConfiguration configuration, IRecoveryEvidenceSigner signer,
        CancellationToken cancellationToken)
    {
        var catalogBytes = RecoveryCatalogCodec.SerializePayload(catalog);
        var catalogSignature = await signer.SignDigestAsync(SHA256.HashData(catalogBytes), cancellationToken);
        var catalogEnvelope = RecoveryCatalogCodec.CreateEnvelope(catalog, configuration.WorkerId,
            new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256, catalogSignature));
        var catalogBody = RecoveryCatalogCodec.SerializeEnvelope(catalogEnvelope);
        if (catalogBody.Length > MaximumMessageBytes) throw new RecoveryWorkerException("catalog_message_size_limit");
        var messages = new List<string>(RecoveryEvidenceCodec.ObservationClasses.Length + 1)
        {
            Encoding.UTF8.GetString(catalogBody)
        };
        foreach (var coverage in catalog.Coverage)
        {
            var observation = new RecoveryObservationPayloadV1(1,
                ObservationId(point.Pointer.OwnerId, point.Pointer.LibraryId, point.Pointer.PointId,
                    coverage.DataClass, catalog.FullManifestSha256, catalog.VerifiedAtUtc),
                point.Pointer.PointId, point.Pointer.OwnerId, point.Pointer.LibraryId, coverage.DataClass,
                "healthy", catalog.VerifiedAtUtc, coverage.ExpectedItemCount, coverage.ObservedItemCount,
                coverage.ExpectedByteLength, coverage.ObservedByteLength, catalog.FullManifestSha256, [], null);
            var payload = observation.SerializeCanonical();
            var signature = await signer.SignDigestAsync(SHA256.HashData(payload), cancellationToken);
            var envelope = RecoveryEvidenceCodec.CreateObservationEnvelope(observation, configuration.WorkerId,
                new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256, signature));
            var body = RecoveryEvidenceCodec.SerializeObservationEnvelope(envelope);
            if (body.Length > MaximumMessageBytes) throw new RecoveryWorkerException("observation_message_size_limit");
            messages.Add(Encoding.UTF8.GetString(body));
        }
        return messages;
    }

    private static Dictionary<string, AttributeValue> PointMarker(RecoveryWorkerConfiguration configuration,
        VerifiedCopiedPoint point, RecoveryAuditHeadV1 auditHead, RecoveryCatalogPayloadV1 catalog,
        IReadOnlyList<string> messages, byte[] outboxBytes)
    {
        var finalDigest = point.Pointer.ManifestSha256;
        var marker = new Dictionary<string, AttributeValue>
        {
            ["pk"] = S("POINT#" + point.Pointer.PointId.ToString("D").ToLowerInvariant()),
            ["sk"] = S("VERIFY"),
            ["ownerId"] = S(point.Pointer.OwnerId),
            ["libraryId"] = S(point.Pointer.LibraryId),
            ["pointId"] = S(point.Pointer.PointId.ToString("D").ToLowerInvariant()),
            ["captureId"] = S(point.Pointer.CaptureId.ToString("D").ToLowerInvariant()),
            ["sourceManifestSha256"] = S(point.Pointer.SourceManifestSha256),
            ["fullManifestSha256"] = S(finalDigest),
            ["checkpointSha256"] = S(point.CheckpointPayloadSha256),
            ["auditHeadSequence"] = N(auditHead.Sequence),
            ["auditHeadEventSha256"] = S(auditHead.EventSha256),
            ["snapshotCutoffUtc"] = S(point.SourceManifest.SnapshotCutoffUtc.ToString("O", CultureInfo.InvariantCulture)),
            ["changeSequence"] = N(point.SourceManifest.ChangeWatermark.LastSequence),
            ["verifiedAtUtc"] = S(catalog.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ["payloadSha256"] = S(RecoveryCatalogCodec.ParseEnvelope(Encoding.UTF8.GetBytes(messages[0])).SafeCatalogPayloadSha256),
            ["outboxSha256"] = S(RecoveryEvidenceCodec.Hash(outboxBytes)),
            ["messageCount"] = N(messages.Count),
            ["messageBodies"] = S(Encoding.UTF8.GetString(outboxBytes)),
            ["state"] = S("pending"),
            ["safeStatus"] = S("healthy"),
            ["safeClassCoverage"] = S(JsonSerializer.Serialize(catalog.Coverage, OutboxJson)),
            ["workerBuildDigest"] = S(configuration.WorkerBuildDigest)
        };
        return marker;
    }

    private static Update VerifiedOwnerUpdate(RecoveryWorkerConfiguration configuration,
        VerifiedCopiedPoint point, RecoveryAuditHeadV1 auditHead, RecoveryCatalogPayloadV1 catalog,
        string catalogEnvelopeBody)
    {
        return new Update
        {
            TableName = configuration.Required("VerificationTableName"),
            Key = OwnerKey(point.Pointer.OwnerId, point.Pointer.LibraryId),
            UpdateExpression = "SET ownerId=:owner, libraryId=:library, pointId=:point, sourceManifestSha256=:sourceManifest, fullManifestSha256=:fullManifest, checkpointSha256=:checkpoint, snapshotCutoffUtc=:cutoff, changeSequence=:changeSequence, verifiedAtUtc=:verifiedAt, auditHeadSequence=:auditSequence, auditHeadEventSha256=:auditSha, payloadSha256=:payloadSha, safeStatus=:healthy, safeClassCoverage=:coverage, workerBuildDigest=:worker",
            ConditionExpression = "(attribute_not_exists(snapshotCutoffUtc) OR snapshotCutoffUtc < :cutoff OR (snapshotCutoffUtc = :cutoff AND changeSequence < :changeSequence) OR (snapshotCutoffUtc = :cutoff AND changeSequence = :changeSequence AND fullManifestSha256=:fullManifest)) AND (attribute_not_exists(auditHeadSequence) OR auditHeadSequence < :auditSequence OR (auditHeadSequence=:auditSequence AND auditHeadEventSha256=:auditSha))",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":owner"] = S(point.Pointer.OwnerId),
                [":library"] = S(point.Pointer.LibraryId),
                [":point"] = S(point.Pointer.PointId.ToString("D").ToLowerInvariant()),
                [":sourceManifest"] = S(point.Pointer.SourceManifestSha256),
                [":fullManifest"] = S(point.Pointer.ManifestSha256),
                [":checkpoint"] = S(point.CheckpointPayloadSha256),
                [":cutoff"] = S(point.SourceManifest.SnapshotCutoffUtc.ToString("O", CultureInfo.InvariantCulture)),
                [":changeSequence"] = N(point.SourceManifest.ChangeWatermark.LastSequence),
                [":verifiedAt"] = S(catalog.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                [":auditSequence"] = N(auditHead.Sequence),
                [":auditSha"] = S(auditHead.EventSha256),
                [":payloadSha"] = S(RecoveryCatalogCodec.ParseEnvelope(Encoding.UTF8.GetBytes(catalogEnvelopeBody)).SafeCatalogPayloadSha256),
                [":healthy"] = S("healthy"),
                [":coverage"] = S(JsonSerializer.Serialize(catalog.Coverage, OutboxJson)),
                [":worker"] = S(configuration.WorkerBuildDigest)
            }
        };
    }

    private static List<string> ReadAndVerifyOutbox(IReadOnlyDictionary<string, AttributeValue> item,
        RecoverySigningTrust trust, VerifiedCopiedPoint point)
    {
        var (catalog,messages)=ReadSignedOutbox(item,trust);
        if (catalog.RecoveryPointId != point.Pointer.PointId || catalog.OwnerId != point.Pointer.OwnerId ||
            catalog.LibraryId != point.Pointer.LibraryId || catalog.FullManifestSha256 != point.Pointer.ManifestSha256 ||
            catalog.CheckpointSha256 != point.CheckpointPayloadSha256 ||
            catalog.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture) != String(item, "verifiedAtUtc"))
            throw new RecoveryWorkerException("verification_outbox_catalog_binding_invalid");
        return messages;
    }

    internal static (RecoveryCatalogPayloadV1 Catalog,List<string> Messages) ReadSignedOutbox(
        IReadOnlyDictionary<string, AttributeValue> item,RecoverySigningTrust trust)
    {
        var bytes = String(item, "messageBodies") is { } encoded
            ? Encoding.UTF8.GetBytes(encoded)
            : throw new RecoveryWorkerException("verification_outbox_missing");
        if (bytes.Length > MaximumOutboxBytes || RecoveryEvidenceCodec.Hash(bytes) != String(item, "outboxSha256"))
            throw new RecoveryWorkerException("verification_outbox_digest_mismatch");
        List<string> messages;
        try
        {
            messages = JsonSerializer.Deserialize<List<string>>(bytes, OutboxJson)
                ?? throw new InvalidDataException("verification_outbox_empty");
        }
        catch (JsonException exception) { throw new RecoveryWorkerException("verification_outbox_invalid", exception); }
        if (messages.Count != RecoveryEvidenceCodec.ObservationClasses.Length + 1 ||
            ReadNumber(item, "messageCount") != messages.Count || messages.Any(body => Encoding.UTF8.GetByteCount(body) > MaximumMessageBytes))
            throw new RecoveryWorkerException("verification_outbox_count_invalid");
        var catalog = RecoveryCatalogCodec.Verify(RecoveryCatalogCodec.ParseEnvelope(Encoding.UTF8.GetBytes(messages[0])), trust).Payload;
        for (var index = 0; index < RecoveryEvidenceCodec.ObservationClasses.Length; index++)
        {
            var envelope = RecoveryEvidenceCodec.ParseObservationEnvelope(Encoding.UTF8.GetBytes(messages[index + 1]));
            var observation = RecoveryEvidenceCodec.VerifyObservation(envelope, trust).Payload;
            var coverage = catalog.Coverage[index];
            if (observation.RecoveryPointId != catalog.RecoveryPointId || observation.OwnerId != catalog.OwnerId ||
                observation.LibraryId != catalog.LibraryId || observation.DataClass != coverage.DataClass ||
                observation.Outcome != "healthy" || observation.ManifestSha256 != catalog.FullManifestSha256 ||
                observation.ObservedAtUtc != catalog.VerifiedAtUtc || observation.ExpectedItemCount != coverage.ExpectedItemCount ||
                observation.ObservedItemCount != coverage.ObservedItemCount || observation.ExpectedByteLength != coverage.ExpectedByteLength ||
                observation.ObservedByteLength != coverage.ObservedByteLength || observation.AlertDelivery is not null ||
                observation.ObservationId != ObservationId(catalog.OwnerId, catalog.LibraryId, catalog.RecoveryPointId,
                    coverage.DataClass, catalog.FullManifestSha256, catalog.VerifiedAtUtc))
                throw new RecoveryWorkerException("verification_outbox_observation_binding_invalid");
        }
        return (catalog,messages);
    }

    private static void ValidatePointMarker(IReadOnlyDictionary<string, AttributeValue> item,
        RecoveryWorkerConfiguration configuration, VerifiedCopiedPoint point, RecoveryAuditHeadV1 auditHead,
        RecoverySigningTrust trust)
    {
        if (String(item, "ownerId") != point.Pointer.OwnerId || String(item, "libraryId") != point.Pointer.LibraryId ||
            String(item, "pointId") != point.Pointer.PointId.ToString("D").ToLowerInvariant() ||
            String(item, "sourceManifestSha256") != point.Pointer.SourceManifestSha256 ||
            String(item, "fullManifestSha256") != point.Pointer.ManifestSha256 ||
            String(item, "checkpointSha256") != point.CheckpointPayloadSha256 ||
            ReadNumber(item, "auditHeadSequence") != auditHead.Sequence ||
            String(item, "auditHeadEventSha256") != auditHead.EventSha256 ||
            String(item, "workerBuildDigest") != configuration.WorkerBuildDigest)
            throw new RecoveryWorkerException("verification_point_marker_conflict");
        _ = ReadAndVerifyOutbox(item, trust, point);
    }

    private static Guid ObservationId(string ownerId, string libraryId, Guid pointId, string dataClass,
        string manifestSha256, DateTimeOffset observedAtUtc)
    {
        var name = Encoding.UTF8.GetBytes($"recovery-observation-v1\0{ownerId}\0{libraryId}\0{pointId:D}\0{dataClass}\0{manifestSha256}\0{observedAtUtc.ToUniversalTime():O}");
        var bytes = SHA256.HashData(name)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static bool IsLater(IReadOnlyDictionary<string, AttributeValue> marker, RecoveryPointManifestV1 manifest)
    {
        if (!DateTimeOffset.TryParse(String(marker, "snapshotCutoffUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var cutoff))
            throw new RecoveryWorkerException("verified_owner_marker_invalid");
        var sequence = ReadNumber(marker, "changeSequence");
        return cutoff.ToUniversalTime() > manifest.SnapshotCutoffUtc ||
               cutoff.ToUniversalTime() == manifest.SnapshotCutoffUtc && sequence > manifest.ChangeWatermark.LastSequence;
    }

    private static async Task MarkDeliveredAsync(IAmazonDynamoDB dynamo, string table, Guid pointId,
        string sourceManifestSha256, string fullManifestSha256, CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = PointKey(pointId),
            UpdateExpression = "SET #state=:delivered, deliveredAtUtc=:now",
            ConditionExpression = "#state=:pending AND sourceManifestSha256=:source AND fullManifestSha256=:full",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":delivered"] = S("delivered"), [":pending"] = S("pending"),
                [":source"] = S(sourceManifestSha256), [":full"] = S(fullManifestSha256),
                [":now"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        }, cancellationToken);
    }

    private static async Task MarkSupersededAsync(IAmazonDynamoDB dynamo, string table, Guid pointId,
        CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = PointKey(pointId),
            UpdateExpression = "SET #state=:superseded, supersededAtUtc=:now",
            ConditionExpression = "#state=:pending",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":superseded"] = S("superseded"), [":pending"] = S("pending"),
                [":now"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        }, cancellationToken);
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetAsync(IAmazonDynamoDB dynamo,
        string table, Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private static Dictionary<string, AttributeValue> PointKey(Guid pointId) => new()
    {
        ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()), ["sk"] = S("VERIFY")
    };

    private static Dictionary<string, AttributeValue> OwnerKey(string ownerId, string libraryId) => new()
    {
        ["pk"] = S("OWNER#" + RecoveryOwnerPointIndex.ScopeHash(ownerId, libraryId)), ["sk"] = S("VERIFIED")
    };

    private static string? String(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;
    private static string? Number(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.N : null;
    private static long ReadNumber(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        long.TryParse(Number(item, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record RecoveryClassTotal(long Count, long Bytes);
}
