using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KeyManagementService;
using Amazon.S3;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryVerifierRunner
{
    private const int MaximumIndexedPoints = 1_000_000;
    private const int MaximumManifestBytes = 512 * 1024 * 1024;
    private const int MaximumReceiptChunkBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var region = configuration.RequiredRegion("DestinationRegion");
        var expectedAccount = configuration.RequiredAccountId("DestinationAccountId");
        using var sts = new Amazon.SecurityToken.AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var identity = await sts.GetCallerIdentityAsync(new Amazon.SecurityToken.Model.GetCallerIdentityRequest(), cancellationToken);
        if (identity.Account != expectedAccount) throw new RecoveryWorkerException("recovery_task_account_mismatch");

        using var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        using var dynamo = new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(region));
        using var kms = new AmazonKeyManagementServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var signer = await AwsKmsEvidenceSigner.CreateAsync(kms, configuration.Required("SigningKeyId"),
            configuration.RequiredHash("SigningPublicKeySha256"), cancellationToken);
        var copies = await ReadCopiedPointsAsync(dynamo, configuration.Required("ControlTableName"),
            configuration.Required("OwnerId"), configuration.Required("LibraryId"), cancellationToken);
        if (copies.Count == 0) throw new RecoveryWorkerException("recovery_copy_missing");

        // Copy completion can arrive out of order. Load the signed source manifests
        // before selecting a point so selection follows the database snapshot order,
        // not the wall clock at which S3 copying happened to finish.
        var candidates = new List<(CopyPointer Pointer, RecoveryPointManifestV1 Manifest)>();
        foreach (var copy in copies)
        {
            var source = await ReadSourceManifestAsync(s3, copy, signer.Trust, cancellationToken);
            candidates.Add((copy, source));
        }
        var latestByScope = candidates
            .Where(item => (configuration.Optional("OwnerId") is null || item.Pointer.OwnerId == configuration.Optional("OwnerId")) &&
                           (configuration.Optional("LibraryId") is null || item.Pointer.LibraryId == configuration.Optional("LibraryId")))
            .GroupBy(item => ScopeHash(item.Pointer.OwnerId, item.Pointer.LibraryId), StringComparer.Ordinal)
            .Select(group => SelectLatest(group))
            .ToArray();
        if (latestByScope.Length == 0) throw new RecoveryWorkerException("recovery_copy_scope_missing");

        long verifiedItems = 0;
        foreach (var point in latestByScope)
        {
            var verified = await VerifyPointAsync(configuration, s3, point.Pointer, signer.Trust, cancellationToken);
            verifiedItems = checked(verifiedItems + verified.Receipts.Count);
            Console.WriteLine($"recovery_copy_integrity_passed point={point.Pointer.PointId:D} owner={ScopeHash(point.Pointer.OwnerId, point.Pointer.LibraryId)} receipts={verified.Receipts.Count}");
        }

        // Object-level checks do not prove restored database consistency or owner access.
        // Only the isolated drill can publish a healthy point catalog.
        RecoveryWorkerMetrics.Emit("CopyIntegrityChecks", latestByScope.Length, configuration);
        RecoveryWorkerMetrics.Emit("CopyIntegrityObjects", verifiedItems, configuration);
    }

    internal static (CopyPointer Pointer, RecoveryPointManifestV1 Manifest) SelectLatest(
        IGrouping<string, (CopyPointer Pointer, RecoveryPointManifestV1 Manifest)> scope)
    {
        var ordered = scope.OrderByDescending(item => item.Manifest.SnapshotCutoffUtc)
            .ThenByDescending(item => item.Manifest.ChangeWatermark.LastSequence)
            .ThenByDescending(item => item.Manifest.SnapshotLsn, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length > 1 &&
            ordered[0].Manifest.SnapshotCutoffUtc == ordered[1].Manifest.SnapshotCutoffUtc &&
            ordered[0].Manifest.ChangeWatermark.LastSequence == ordered[1].Manifest.ChangeWatermark.LastSequence &&
            (ordered[0].Manifest.Digest() != ordered[1].Manifest.Digest()))
            throw new RecoveryWorkerException("recovery_snapshot_order_conflict");
        return ordered[0];
    }

    internal static async Task<RecoveryPointManifestV1> ReadSourceManifestAsync(
        IAmazonS3 s3,
        CopyPointer pointer,
        RecoverySigningTrust signerTrust,
        CancellationToken cancellationToken)
    {
        var checkpointBytes = await ReadExactBytesAsync(s3, pointer.EvidenceBucket, pointer.CheckpointKey,
            pointer.CheckpointVersionId, pointer.CheckpointSha256, pointer.CheckpointByteLength,
            1024 * 1024, cancellationToken);
        var checkpoint = WorkerArtifactCodec.VerifyCheckpoint(checkpointBytes, signerTrust);
        var receipt = checkpoint.Payload.SourceManifest;
        if (checkpoint.Payload.RecoveryPointId != pointer.PointId || checkpoint.Payload.CaptureId != pointer.CaptureId ||
            checkpoint.Payload.OwnerId != pointer.OwnerId || checkpoint.Payload.LibraryId != pointer.LibraryId ||
            receipt.SourceBucket != pointer.SourceCaptureBucket || receipt.SourceKey != pointer.SourceManifestKey ||
            receipt.SourceVersionId != pointer.SourceManifestVersionId || receipt.Sha256 != pointer.SourceManifestSha256 ||
            receipt.DestinationBucket != pointer.EvidenceBucket)
            throw new RecoveryWorkerException("source_manifest_receipt_mismatch");
        var bytes = await ReadExactBytesAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
            receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, MaximumManifestBytes, cancellationToken);
        var manifest = RecoveryPointManifestV1.ParseCanonical(bytes);
        if (manifest.RecoveryPointId != pointer.PointId || manifest.CaptureId != pointer.CaptureId ||
            manifest.OwnerId != pointer.OwnerId || manifest.LibraryId != pointer.LibraryId ||
            manifest.SourceRegion != pointer.SourceRegion)
            throw new RecoveryWorkerException("source_manifest_binding_invalid");
        return manifest;
    }

    internal static async Task<VerifiedRecoveryAuditGenesis> ReadAuditGenesisAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        RecoverySigningTrust trust,
        CancellationToken cancellationToken)
    {
        var bucket = configuration.Required("EvidenceBucket");
        var key = configuration.Required("AuditGenesisKey");
        if (!key.StartsWith("evidence/audit/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
            throw new RecoveryWorkerException("recovery_audit_genesis_location_invalid");
        if (!long.TryParse(configuration.Required("AuditGenesisByteLength"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var length) || length is <= 0 or > 64 * 1024)
            throw new RecoveryWorkerException("recovery_audit_genesis_size_invalid");
        var bytes = await ReadExactBytesAsync(s3, bucket, key,
            configuration.Required("AuditGenesisVersionId"), configuration.RequiredHash("AuditGenesisSha256"),
            length,
            64 * 1024, cancellationToken);
        try { return RecoveryAuditLedgerCodec.VerifyGenesisArtifact(bytes, trust, DateTimeOffset.UtcNow); }
        catch (InvalidDataException exception) { throw new RecoveryWorkerException("recovery_audit_genesis_untrusted", exception); }
    }

    internal static async Task<VerifiedCopiedPoint> VerifyPointAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        CopyPointer pointer,
        RecoverySigningTrust signerTrust,
        CancellationToken cancellationToken)
    {
        var sourceAccount = configuration.RequiredAccountId("SourceAccountId");
        var recoveryAccount = configuration.RequiredAccountId("DestinationAccountId");
        var sourceRegion = configuration.RequiredRegion("SourceRegion");
        var recoveryRegion = configuration.RequiredRegion("DestinationRegion");
        if (sourceAccount == recoveryAccount || sourceRegion == recoveryRegion ||
            pointer.SourceAccountId != sourceAccount || pointer.SourceRegion != sourceRegion ||
            pointer.RecoveryAccountId != recoveryAccount || pointer.RecoveryRegion != recoveryRegion)
            throw new RecoveryWorkerException("recovery_copy_binding_invalid");

        var checkpointBytes = await ReadExactBytesAsync(s3, pointer.EvidenceBucket, pointer.CheckpointKey,
            pointer.CheckpointVersionId, pointer.CheckpointSha256, pointer.CheckpointByteLength,
            1024 * 1024, cancellationToken);
        var checkpoint = WorkerArtifactCodec.VerifyCheckpoint(checkpointBytes, signerTrust);
        var payload = checkpoint.Payload;
        if (payload.RecoveryPointId != pointer.PointId || payload.CaptureId != pointer.CaptureId ||
            payload.OwnerId != pointer.OwnerId || payload.LibraryId != pointer.LibraryId ||
            payload.DestinationAccountId != recoveryAccount || payload.SourceRegion != sourceRegion ||
            payload.DestinationRegion != recoveryRegion || payload.RecoveryWorkerBuildDigest != configuration.WorkerBuildDigest ||
            (pointer.State == "copied" && (payload.FinalManifest is null || payload.OwnerAccessProof is null)) ||
            (pointer.State == "access_incomplete" && (payload.FinalManifest is not null || payload.OwnerAccessProof is not null)))
            throw new RecoveryWorkerException("recovery_copy_checkpoint_binding_invalid");

        var sourceManifestReceipt = payload.SourceManifest;
        if (sourceManifestReceipt.SourceBucket != pointer.SourceCaptureBucket ||
            sourceManifestReceipt.SourceKey != pointer.SourceManifestKey ||
            sourceManifestReceipt.SourceVersionId != pointer.SourceManifestVersionId ||
            sourceManifestReceipt.DestinationBucket != pointer.EvidenceBucket ||
            sourceManifestReceipt.Sha256 != payload.SourceManifestSha256 ||
            sourceManifestReceipt.Sha256 != pointer.SourceManifestSha256)
            throw new RecoveryWorkerException("source_manifest_receipt_mismatch");
        var sourceManifestBytes = await ReadExactBytesAsync(s3, sourceManifestReceipt.DestinationBucket,
            sourceManifestReceipt.DestinationKey, sourceManifestReceipt.DestinationVersionId,
            sourceManifestReceipt.Sha256, sourceManifestReceipt.ByteLength, MaximumManifestBytes, cancellationToken);
        var sourceManifest = RecoveryPointManifestV1.ParseCanonical(sourceManifestBytes);
        if (sourceManifest.RecoveryPointId != pointer.PointId || sourceManifest.CaptureId != pointer.CaptureId ||
            sourceManifest.OwnerId != pointer.OwnerId || sourceManifest.LibraryId != pointer.LibraryId ||
            sourceManifest.SourceRegion != sourceRegion)
            throw new RecoveryWorkerException("source_manifest_binding_invalid");

        RecoveryPointManifestV1? finalManifest = null;
        if (payload.FinalManifest is { } finalReceipt)
        {
            if (finalReceipt.DestinationBucket != pointer.EvidenceBucket ||
                finalReceipt.DestinationKey != pointer.ManifestKey ||
                finalReceipt.DestinationVersionId != pointer.ManifestVersionId ||
                finalReceipt.Sha256 != pointer.ManifestSha256 ||
                payload.FinalManifestSha256 != pointer.ManifestSha256)
                throw new RecoveryWorkerException("final_manifest_receipt_mismatch");
            var finalManifestBytes = await ReadExactBytesAsync(s3, pointer.EvidenceBucket, pointer.ManifestKey,
                pointer.ManifestVersionId, pointer.ManifestSha256, finalReceipt.ByteLength,
                MaximumManifestBytes, cancellationToken);
            finalManifest = RecoveryPointManifestV1.ParseCanonical(finalManifestBytes);
            if (finalManifest.RecoveryPointId != sourceManifest.RecoveryPointId || finalManifest.CaptureId != sourceManifest.CaptureId ||
                finalManifest.OwnerId != sourceManifest.OwnerId || finalManifest.LibraryId != sourceManifest.LibraryId ||
                finalManifest.SnapshotId != sourceManifest.SnapshotId || finalManifest.SnapshotLsn != sourceManifest.SnapshotLsn ||
                finalManifest.SnapshotCutoffUtc != sourceManifest.SnapshotCutoffUtc ||
                finalManifest.ChangeWatermark != sourceManifest.ChangeWatermark ||
                finalManifest.MigrationDigest != sourceManifest.MigrationDigest ||
                finalManifest.Components.Count != sourceManifest.Components.Count + 1 ||
                !finalManifest.Components.Any(component => component.Name == "owner-access-proof" && component.DataClass == RecoveryDataClass.AccessEvidence))
                throw new RecoveryWorkerException("final_manifest_binding_invalid");
        }

        var receiptRows = await ReadReceiptChunksAsync(s3, pointer.EvidenceBucket, payload, cancellationToken);
        var expected = BuildExpectedReceipts(sourceManifest, finalManifest, pointer, configuration);
        ValidateReceiptSet(receiptRows, expected);
        foreach (var receipt in receiptRows)
            await S3ArtifactWriter.VerifyExactAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
                receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, cancellationToken);

        var ownerAccess = payload.OwnerAccessProof is null ? null : await VerifyOwnerAccessProofAsync(configuration, s3,
            payload.OwnerAccessProof, pointer.OwnerId, pointer.LibraryId, cancellationToken);
        VerifyClassCoverage(finalManifest ?? sourceManifest, receiptRows);
        return new(pointer, sourceManifest, finalManifest, payload, receiptRows, checkpoint.PayloadSha256,
            checkpoint.EnvelopeSha256, ownerAccess);
    }

    private static async Task<VerifiedRecoveryOwnerAccessEvidence> VerifyOwnerAccessProofAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        PrivateArtifactReceiptV1 receipt,
        string ownerId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        if (receipt.SourceBucket != receipt.DestinationBucket || receipt.SourceKey != receipt.DestinationKey ||
            receipt.SourceVersionId != receipt.DestinationVersionId)
            throw new RecoveryWorkerException("owner_access_receipt_not_immutable");
        var bytes = await ReadExactBytesAsync(s3, receipt.DestinationBucket, receipt.DestinationKey,
            receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength, 64 * 1024, cancellationToken);
        var envelope = RecoveryOwnerAccessCodec.ParseEnvelope(bytes);
        var trust = new RecoverySigningTrust(configuration.Required("OwnerApprovalTrust__KeyId"),
            configuration.Required("OwnerApprovalTrust__PublicKeySpkiBase64"),
            configuration.RequiredHash("OwnerApprovalTrust__PublicKeySha256"));
        var expected = new RecoveryOwnerAccessExpectationV1(ownerId, libraryId,
            configuration.RequiredHash("ConfigurationDigest"), configuration.RequiredHash("ApiBuildDigest"),
            configuration.Required("SourceRevision"), configuration.RequiredHash("MigrationDigest"),
            configuration.WorkerBuildDigest, configuration.RequiredHash("RecoveryTemplateDigest"),
            configuration.Required("OwnerUserPoolId"), configuration.Required("OwnerUserPoolClientId"),
            configuration.Required("OwnerUserPoolIssuer"));
        return RecoveryOwnerAccessCodec.Verify(envelope, trust, expected, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<PrivateCopyReceiptV1> BuildExpectedReceipts(
        RecoveryPointManifestV1 source,
        RecoveryPointManifestV1? final,
        CopyPointer pointer,
        RecoveryWorkerConfiguration configuration)
    {
        var expected = new List<PrivateCopyReceiptV1>(source.Objects.Count + source.Components.Count + 1);
        var payloadBucket = configuration.Required("DestinationBucket");
        foreach (var item in source.Objects)
        {
            var destinationKey = "payload/sha256/" + item.Sha256;
            expected.Add(new(configuration.Required("SourceBucket"), item.SourceKey, item.SourceVersionId, item.Sha256,
                item.ByteLength, payloadBucket, destinationKey, "", item.DataClass.ToString(), default));
        }
        foreach (var component in source.Components)
        {
            var (sourceBucket, sourceKey) = ParseS3Uri(component.SourceKey);
            var toPayload = component.DataClass == RecoveryDataClass.DatabaseSnapshot;
            expected.Add(new(sourceBucket, sourceKey, component.SourceVersionId, component.Sha256,
                component.ByteLength, toPayload ? payloadBucket : pointer.EvidenceBucket,
                toPayload ? $"snapshots/{source.RecoveryPointId:D}/{component.Name}" :
                    $"points/{source.RecoveryPointId:D}/components/{component.Name}",
                "", component.DataClass.ToString(), default));
        }
        var ownerProof = final?.Components.SingleOrDefault(component => component.Name == "owner-access-proof");
        if (ownerProof is not null)
        {
            var (ownerBucket, ownerKey) = ParseS3Uri(ownerProof.SourceKey);
            expected.Add(new(ownerBucket, ownerKey, ownerProof.SourceVersionId, ownerProof.Sha256,
                ownerProof.ByteLength, ownerBucket, ownerKey, ownerProof.SourceVersionId,
                RecoveryDataClass.AccessEvidence.ToString(), default));
        }
        return expected;
    }

    internal static void ValidateReceiptSet(
        IReadOnlyList<PrivateCopyReceiptV1> receipts,
        IReadOnlyList<PrivateCopyReceiptV1> expected)
    {
        if (receipts.Count != expected.Count) throw new RecoveryWorkerException("copy_receipt_count_mismatch");
        var unmatched = new Dictionary<ReceiptIdentity, int>();
        foreach (var item in expected)
        {
            var key = ReceiptIdentity.From(item);
            unmatched.TryGetValue(key, out var count);
            unmatched[key] = checked(count + 1);
        }
        foreach (var receipt in receipts)
        {
            var key = ReceiptIdentity.From(receipt);
            if (!unmatched.TryGetValue(key, out var count) || count <= 0 || string.IsNullOrWhiteSpace(receipt.DestinationVersionId) ||
                receipt.DestinationVersionId == "null" || receipt.VerifiedAtUtc.Offset != TimeSpan.Zero)
                throw new RecoveryWorkerException("copy_receipt_binding_invalid");
            if (count == 1) unmatched.Remove(key);
            else unmatched[key] = count - 1;
        }
        if (unmatched.Count != 0) throw new RecoveryWorkerException("copy_receipt_missing");
    }

    private static void VerifyClassCoverage(
        RecoveryPointManifestV1 manifest,
        IReadOnlyList<PrivateCopyReceiptV1> receipts)
    {
        var s3Objects = manifest.Objects.Select(item => (item.DataClass, item.ByteLength))
            .Concat(manifest.Components.Select(item => (item.DataClass, item.ByteLength))).ToArray();
        foreach (var declared in manifest.Coverage)
        {
            if (declared.DataClass is RecoveryDataClass.OriginalImage or RecoveryDataClass.XmpSidecar or
                RecoveryDataClass.Configuration or RecoveryDataClass.ReleaseArtifact or RecoveryDataClass.AccessEvidence or
                RecoveryDataClass.DatabaseSnapshot or RecoveryDataClass.ObjectInventory)
            {
                var items = s3Objects.Where(item => item.DataClass == declared.DataClass).ToArray();
                if (items.LongLength != declared.ItemCount || items.Sum(item => item.ByteLength) != declared.ByteLength)
                    throw new RecoveryWorkerException("manifest_class_coverage_mismatch");
            }
            else if (declared.DataClass is RecoveryDataClass.VersionGraph or RecoveryDataClass.LibraryMetadata or
                     RecoveryDataClass.AuditRecord or RecoveryDataClass.RecoveryEvidence)
            {
                // These classes are authoritative database rows in the point-in-time dump.
                // Their actual row/byte counts are checked after pg_restore in drill mode.
                continue;
            }
            else
                throw new RecoveryWorkerException("manifest_class_coverage_mismatch");
        }
    }

    private static async Task<IReadOnlyList<PrivateCopyReceiptV1>> ReadReceiptChunksAsync(
        IAmazonS3 s3,
        string evidenceBucket,
        PrivateCopyCheckpointPayloadV1 payload,
        CancellationToken cancellationToken)
    {
        var receipts = new List<PrivateCopyReceiptV1>(checked((int)Math.Min(payload.CopyCount, int.MaxValue)));
        foreach (var chunk in payload.ReceiptChunks)
        {
            var bytes = await ReadExactBytesAsync(s3, evidenceBucket, chunk.Key, chunk.VersionId,
                chunk.Sha256, chunk.ByteLength, MaximumReceiptChunkBytes, cancellationToken);
            var parsed = JsonSerializer.Deserialize<PrivateCopyReceiptV1[]>(bytes, ArtifactJson)
                ?? throw new RecoveryWorkerException("copy_receipt_chunk_invalid");
            if (parsed.Length != chunk.ReceiptCount || parsed.Any(item => item is null))
                throw new RecoveryWorkerException("copy_receipt_chunk_count_mismatch");
            receipts.AddRange(parsed);
        }
        if (receipts.Count != payload.CopyCount) throw new RecoveryWorkerException("copy_receipt_count_mismatch");
        return receipts;
    }

    internal static async Task<IReadOnlyList<CopyPointer>> ReadCopiedPointsAsync(
        IAmazonDynamoDB dynamo,
        string table,
        string ownerId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var results = new List<CopyPointer>();
        var indexKey = RecoveryPointIndexBootstrapRunner.IndexKey(ownerId, libraryId);
        var indexResponse = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = indexKey,
            ConsistentRead = true
        }, timeout.Token);
        if (indexResponse.Item is not { Count: > 0 } marker)
            throw new RecoveryWorkerException("scope_index_bootstrap_required");
        RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);

        var indexed = new List<RecoveryOwnerPointMembership>();
        var discovered = new Dictionary<Guid, string>();
        Dictionary<string, AttributeValue>? startKey = null;
        var pointCountBefore = ReadNumber(marker, "indexedPointCount");
        do
        {
            var page = await dynamo.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                Limit = 1000,
                ExclusiveStartKey = startKey,
                KeyConditionExpression = "pk=:pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = S(RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId))
                }
            }, timeout.Token);
            foreach (var item in page.Items)
            {
                var sortKey = GetString(item, "sk");
                if (sortKey == "INDEX") continue;
                if (sortKey is null || !sortKey.StartsWith("POINT#", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(sortKey.AsSpan("POINT#".Length), "D", out var pointId) || pointId == Guid.Empty ||
                    GetString(item, "pointId") != pointId.ToString("D").ToLowerInvariant() ||
                    GetString(item, "sourceManifestSha256") is not { } manifestSha || !IsHash(manifestSha) ||
                    GetBool(item, "bootstrapMember") is not { } bootstrapMember || !discovered.TryAdd(pointId, manifestSha))
                    throw new RecoveryWorkerException("scope_index_member_invalid");
                indexed.Add(new(pointId, manifestSha, bootstrapMember));

                var copyKey = RecoveryPointIndexBootstrapRunner.OwnerPointKey(ownerId, libraryId, pointId);
                var copyResponse = await dynamo.GetItemAsync(new GetItemRequest
                {
                    TableName = table,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = S("POINT#" + pointId.ToString("D").ToLowerInvariant()),
                        ["sk"] = S("COPY")
                    },
                    ConsistentRead = true
                }, timeout.Token);
                if (copyResponse.Item is not { Count: > 0 } copyItem)
                    throw new RecoveryWorkerException("scope_index_point_missing");
                if (GetString(copyItem, "ownerId") != ownerId || GetString(copyItem, "libraryId") != libraryId ||
                    GetString(copyItem, "sourceManifestSha256") != manifestSha)
                    throw new RecoveryWorkerException("scope_index_point_binding_mismatch");
                var state = GetString(copyItem, "state");
                if (state == "copying") continue;
                if (state is not ("copied" or "access_incomplete"))
                    throw new RecoveryWorkerException("recovery_copy_state_invalid");
                var pointer = CopyPointer.From(copyItem);
                if (pointer is null || pointer.PointId != pointId || pointer.OwnerId != ownerId || pointer.LibraryId != libraryId)
                    throw new RecoveryWorkerException("recovery_copy_control_record_invalid");
                results.Add(pointer);
            }
            startKey = page.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        } while (startKey is not null);

        var markerAfter = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = indexKey,
            ConsistentRead = true
        }, timeout.Token);
        if (markerAfter.Item is not { Count: > 0 } finalMarker ||
            ReadNumber(finalMarker, "indexedPointCount") != pointCountBefore)
            throw new RecoveryWorkerException("scope_index_changed_during_read");
        RecoveryPointIndexBootstrapRunner.ValidateComplete(finalMarker);
        if (ReadNumber(finalMarker, "bootstrapPointCount") != ReadNumber(marker, "bootstrapPointCount") ||
            GetString(finalMarker, "bootstrapPointSetSha256") != GetString(marker, "bootstrapPointSetSha256"))
            throw new RecoveryWorkerException("scope_index_bootstrap_binding_changed");
        if (discovered.Count > MaximumIndexedPoints)
            throw new RecoveryWorkerException("recovery_control_discovery_limit");
        try
        {
            RecoveryOwnerPointIndex.ValidateStrongIndex(ownerId, libraryId,
                checked((int)ReadNumber(finalMarker, "bootstrapPointCount")),
                GetString(finalMarker, "bootstrapPointSetSha256") ?? string.Empty,
                ReadNumber(finalMarker, "indexedPointCount"), indexed, discovered);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("scope_index_inventory_mismatch", exception);
        }
        return results;
    }

    internal static async Task<byte[]> ReadExactBytesAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        string versionId,
        string sha256,
        long byteLength,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (byteLength < 0 || byteLength > maxBytes) throw new RecoveryWorkerException("recovery_artifact_size_limit");
        var response = await s3.GetObjectAsync(new Amazon.S3.Model.GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, cancellationToken);
        using (response)
        {
            if (response.VersionId != versionId || response.ContentLength != byteLength)
                throw new RecoveryWorkerException("recovery_artifact_identity_mismatch");
            using var output = new MemoryStream(checked((int)byteLength));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > byteLength) throw new RecoveryWorkerException("recovery_artifact_length_mismatch");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (output.Length != byteLength || actualSha256 != sha256)
                throw new RecoveryWorkerException("recovery_artifact_checksum_mismatch");
            return output.ToArray();
        }
    }

    private static (string Bucket, string Key) ParseS3Uri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "s3" ||
            string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.AbsolutePath))
            throw new RecoveryWorkerException("recovery_component_uri_invalid");
        return (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }

    internal static string ScopeHash(string ownerId, string libraryId) =>
        RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(ownerId + "\0" + libraryId));
    private static AttributeValue S(string value) => new() { S = value };
    private static string? GetString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;
    private static long ReadNumber(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && long.TryParse(value.N, NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) ? number : -1;
    private static bool? GetBool(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.BOOL : null;
    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private readonly record struct ReceiptIdentity(string SourceBucket, string SourceKey, string SourceVersionId,
        string Sha256, long ByteLength, string DestinationBucket, string DestinationKey, string DataClass)
    {
        public static ReceiptIdentity From(PrivateCopyReceiptV1 receipt) => new(receipt.SourceBucket, receipt.SourceKey,
            receipt.SourceVersionId, receipt.Sha256, receipt.ByteLength, receipt.DestinationBucket,
            receipt.DestinationKey, receipt.DataClass);
    }
}

internal sealed record VerifiedCopiedPoint(
    CopyPointer Pointer,
    RecoveryPointManifestV1 SourceManifest,
    RecoveryPointManifestV1? FinalManifest,
    PrivateCopyCheckpointPayloadV1 Checkpoint,
    IReadOnlyList<PrivateCopyReceiptV1> Receipts,
    string CheckpointPayloadSha256,
    string CheckpointEnvelopeSha256,
    VerifiedRecoveryOwnerAccessEvidence? OwnerAccess);

internal sealed record CopyPointer(
    Guid PointId,
    Guid CaptureId,
    string OwnerId,
    string LibraryId,
    string SourceAccountId,
    string SourceRegion,
    string RecoveryAccountId,
    string RecoveryRegion,
    string SourceCaptureBucket,
    string SourceManifestKey,
    string SourceManifestVersionId,
    string SourceManifestSha256,
    string EvidenceBucket,
    string ManifestKey,
    string ManifestVersionId,
    string ManifestSha256,
    string CheckpointKey,
    string CheckpointVersionId,
    string CheckpointSha256,
    long CheckpointByteLength,
    DateTimeOffset CopiedAtUtc,
    string State)
{
    public static CopyPointer? From(Dictionary<string, AttributeValue> item)
    {
        string Get(string name) => item.TryGetValue(name, out var value) ? value.S ?? "" : "";
        string GetNumber(string name) => item.TryGetValue(name, out var value) ? value.N ?? "" : "";
        if (!Guid.TryParse(Get("pk").StartsWith("POINT#", StringComparison.Ordinal) ? Get("pk")[6..] : "", out var pointId) ||
            !Guid.TryParse(Get("captureId"), out var captureId))
            throw new RecoveryWorkerException("recovery_copy_control_record_invalid");
        _ = DateTimeOffset.TryParse(Get("copiedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var copiedAt);
        var evidenceBucket = Get("evidenceBucket");
        if (string.IsNullOrWhiteSpace(evidenceBucket)) evidenceBucket = Get("manifestBucket");
        var pointer = new CopyPointer(pointId, captureId, Get("ownerId"), Get("libraryId"), Get("sourceAccountId"),
            Get("sourceRegion"), Get("recoveryAccountId"), Get("recoveryRegion"), Get("sourceManifestBucket"), Get("sourceManifestKey"),
            Get("sourceManifestVersionId"), Get("sourceManifestSha256"), evidenceBucket, Get("manifestKey"), Get("manifestVersionId"),
            Get("manifestSha256"), Get("checkpointKey"), Get("checkpointVersionId"), Get("checkpointSha256"),
            long.TryParse(GetNumber("checkpointByteLength"), NumberStyles.None, CultureInfo.InvariantCulture, out var checkpointBytes) ? checkpointBytes : 0,
            copiedAt == default ? DateTimeOffset.MinValue : copiedAt.ToUniversalTime(), Get("state"));
        if (string.IsNullOrWhiteSpace(pointer.OwnerId) || string.IsNullOrWhiteSpace(pointer.LibraryId) ||
            string.IsNullOrWhiteSpace(pointer.SourceCaptureBucket) || string.IsNullOrWhiteSpace(pointer.SourceManifestKey) ||
            string.IsNullOrWhiteSpace(pointer.SourceManifestVersionId) || string.IsNullOrWhiteSpace(pointer.SourceManifestSha256) ||
            string.IsNullOrWhiteSpace(pointer.EvidenceBucket) || string.IsNullOrWhiteSpace(pointer.CheckpointKey) ||
            string.IsNullOrWhiteSpace(pointer.CheckpointVersionId) || pointer.CheckpointByteLength <= 0)
            throw new RecoveryWorkerException("recovery_copy_control_record_incomplete");
        if (pointer.State == "copied" && (string.IsNullOrWhiteSpace(pointer.ManifestKey) ||
            string.IsNullOrWhiteSpace(pointer.ManifestVersionId) || string.IsNullOrWhiteSpace(pointer.ManifestSha256)))
            throw new RecoveryWorkerException("recovery_copy_control_record_incomplete");
        if (pointer.State is not ("copied" or "access_incomplete"))
            throw new RecoveryWorkerException("recovery_copy_state_invalid");
        return pointer;
    }
}
