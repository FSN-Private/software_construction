using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

/// <summary>Recovery-account-only purge planning and exact-version deletion. The owner/MFA
/// principal signs plans and release receipts; this process never signs or accesses the source account.</summary>
internal static class RecoveryPurgeRunner
{
    private const int MaximumIndexedPoints = 1_000_000;
    private const int MaximumIntentItems = 1_000_000;
    private const long MaximumDatabaseDumpBytes = 95L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count is < 3 or > 5)
            throw new RecoveryWorkerException("purge_usage_invalid");

        await VerifyExecutorIdentityAsync(configuration, cancellationToken);
        var proposalBytes = await ReadBoundedArtifactAsync(arguments[0], 64 * 1024, cancellationToken);
        var proposal = RecoveryPurgeProposalV1.ParseCanonical(proposalBytes);
        var region = configuration.RequiredRegion("DestinationRegion");
        using var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        using var dynamo = new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var recoveryTrust = ReadTrust(configuration, "RecoveryEvidenceTrust");
        var ownerTrust = ReadTrust(configuration, "OwnerApprovalTrust");

        if (arguments[1] == "plan")
        {
            if (arguments.Count != 3) throw new RecoveryWorkerException("purge_usage_invalid");
            var plan = await PreparePlanAsync(configuration, s3, dynamo, proposal, recoveryTrust,
                DateTimeOffset.UtcNow, cancellationToken);
            await WritePrivateArtifactAsync(arguments[2], plan.SerializeCanonical(), cancellationToken);
            Console.WriteLine($"recovery_purge_plan_prepared operation={plan.OperationId:D} points={plan.OwnerPointCount} targets={plan.TargetVersions.Count} digest={plan.Digest()}");
            return;
        }

        if (arguments[1] == "execute")
        {
            if (arguments.Count != 5) throw new RecoveryWorkerException("purge_usage_invalid");
            var plan = RecoveryPurgePlanV1.ParseCanonical(await ReadBoundedArtifactAsync(arguments[2], 64 * 1024 * 1024, cancellationToken));
            var approval = RecoveryPurgeApprovalCodec.ParseEnvelope(await ReadBoundedArtifactAsync(arguments[3], 64 * 1024, cancellationToken));
            var verifiedApproval = RecoveryPurgeApprovalCodec.Verify(approval, ownerTrust, plan, DateTimeOffset.UtcNow);
            EnsurePlanMatchesProposal(plan, proposal);
            var receipt = await ExecuteAsync(configuration, s3, dynamo, proposal, plan, verifiedApproval,
                recoveryTrust, DateTimeOffset.UtcNow, cancellationToken);
            await WritePrivateArtifactAsync(arguments[4], RecoveryEvidenceCodec.SerializeCanonical(receipt), cancellationToken);
            Console.WriteLine($"recovery_purge_recovery_side_complete operation={plan.OperationId:D} receipt={RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(receipt))} primary_cleanup=required owner_release=required");
            return;
        }

        throw new RecoveryWorkerException("purge_command_invalid");
    }

    internal static async Task<RecoveryPurgePlanV1> PreparePlanAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        IAmazonDynamoDB dynamo,
        RecoveryPurgeProposalV1 proposal,
        RecoverySigningTrust recoveryTrust,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(recoveryTrust);
        proposal.Validate(nowUtc);
        await RequireRecoveryAccountAsync(configuration, cancellationToken);

        var inventory = await ReadStrongPointInventoryAsync(dynamo, configuration.Required("ControlTableName"),
            proposal.OwnerId, proposal.LibraryId, cancellationToken);
        var verified = new List<VerifiedPurgeCopyPoint>(inventory.Pointers.Count);
        foreach (var pointer in inventory.Pointers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pointer.State != "copied") throw new RecoveryWorkerException("purge_point_not_fully_copied");
            var point = await RecoveryPurgePointVerifier.VerifyAsync(configuration, s3, pointer, recoveryTrust,
                null, cancellationToken);
            verified.Add(point);
        }
        if (verified.Count == 0) throw new RecoveryWorkerException("purge_recovery_point_missing");

        var latest = verified.OrderByDescending(item => item.SourceManifest.SnapshotCutoffUtc)
            .ThenByDescending(item => item.SourceManifest.ChangeWatermark.LastSequence)
            .ThenByDescending(item => item.Pointer.PointId)
            .First();
        if (latest.SourceManifest.SnapshotCutoffUtc < proposal.ProposedAtUtc)
            throw new RecoveryWorkerException("purge_post_proposal_snapshot_required");
        var retentionHistory = await BuildRetentionHistoryAsync(configuration, s3, verified, proposal,
            recoveryTrust, nowUtc, cancellationToken);
        var eligibleAt = Max(proposal.EligibleAtUtc, retentionHistory.EligibleAtUtc)
            ?? throw new RecoveryWorkerException("purge_retention_history_incomplete");
        if (nowUtc < eligibleAt) throw new RecoveryWorkerException("purge_retention_not_eligible");
        var freezeAcknowledgementSha256 = await VerifySnapshotProposalAndRetentionAsync(configuration, s3,
            latest, proposal, nowUtc, cancellationToken);

        var targetVersions = BuildTargetVersions(verified, latest, proposal)
            .Select(item => item with { EligibleAtUtc = eligibleAt }).ToArray();
        if (targetVersions.Length == 0) throw new RecoveryWorkerException("purge_target_copy_missing");
        var mediaScopeId = MediaScope(proposal);
        var snapshotScopeId = RecoveryFenceScope.SnapshotScopeId(proposal.OwnerId, proposal.LibraryId);
        var writerSet = await ReadAndRequireSettledWriterIntentsAsync(dynamo, configuration.Required("IntentTableName"),
            verified, proposal, mediaScopeId, snapshotScopeId, cancellationToken);
        var pointIds = inventory.Pointers.Select(item => item.PointId).Order().ToArray();
        var affectedPointIds = targetVersions.Where(item => item.Kind == "database_snapshot")
            .Select(item => item.PointId).Distinct().Order().ToArray();
        var preparedAt = nowUtc.ToUniversalTime();
        var expiresAt = preparedAt.AddHours(1) < proposal.ExpiresAtUtc ? preparedAt.AddHours(1) : proposal.ExpiresAtUtc;
        if (expiresAt <= preparedAt) throw new RecoveryWorkerException("purge_plan_expiry_invalid");
        var plan = new RecoveryPurgePlanV1(
            RecoveryPurgePlanV1.CurrentSchemaVersion,
            proposal.OperationId,
            proposal.EventId,
            proposal.PlanDigest,
            freezeAcknowledgementSha256,
            proposal.OwnerId,
            proposal.LibraryId,
            proposal.AssetId,
            proposal.EditId,
            proposal.Category,
            proposal.ContentSha256,
            proposal.Generation,
            proposal.Generation,
            pointIds.LongLength,
            RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(proposal.OwnerId, proposal.LibraryId, pointIds),
            ComputePointSetSha256(affectedPointIds),
            writerSet.Digest,
            preparedAt,
            expiresAt,
            retentionHistory,
            targetVersions);
        plan.Validate(preparedAt);
        return plan.Canonicalized();
    }

    internal static IReadOnlyList<RecoveryPurgeTargetVersionV1> BuildTargetVersions(
        IReadOnlyList<VerifiedPurgeCopyPoint> points,
        VerifiedPurgeCopyPoint latest,
        RecoveryPurgeProposalV1 proposal)
    {
        var targetReference = new RecoveryLogicalReference(proposal.OwnerId, proposal.LibraryId,
            proposal.AssetId, proposal.EditId);
        targetReference.Validate();
        var latestObjects = latest.SourceManifest.Objects;
        var liveShared = latestObjects
            .Where(item => item.Sha256 == proposal.ContentSha256)
            .SelectMany(item => item.LogicalReferences)
            .Where(reference => reference.AssetId is not null)
            .Any(reference => !SameIdentity(reference, targetReference));

        var targets = new Dictionary<(string Bucket, string Key, string Version), RecoveryPurgeTargetVersionV1>();
        foreach (var point in points)
        {
            var matchingObjects = point.SourceManifest.Objects.Where(item =>
                item.Sha256 == proposal.ContentSha256 &&
                item.LogicalReferences.Any(reference => SameIdentity(reference, targetReference))).ToArray();
            foreach (var objectReference in matchingObjects)
            {
                var dataClass = proposal.Category == "original" ? RecoveryDataClass.OriginalImage : RecoveryDataClass.XmpSidecar;
                if (objectReference.DataClass != dataClass) throw new RecoveryWorkerException("purge_media_class_mismatch");
                var receipt = point.Receipts.SingleOrDefault(item => item.SourceKey == objectReference.SourceKey &&
                    item.SourceVersionId == objectReference.SourceVersionId && item.Sha256 == objectReference.Sha256 &&
                    item.ByteLength == objectReference.ByteLength && item.DataClass == objectReference.DataClass.ToString());
                if (receipt is null || receipt.DestinationBucket.Length == 0 || receipt.DestinationVersionId == "null" ||
                    !receipt.DestinationKey.StartsWith("payload/sha256/", StringComparison.Ordinal))
                    throw new RecoveryWorkerException("purge_media_receipt_missing");
                var key = (receipt.DestinationBucket, receipt.DestinationKey, receipt.DestinationVersionId);
                targets[key] = new("media", point.Pointer.PointId, receipt.DestinationBucket,
                    receipt.DestinationKey, receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength,
                    proposal.EligibleAtUtc, liveShared);
            }

            if (matchingObjects.Length == 0) continue;
            var databaseComponents = point.SourceManifest.Components
                .Where(component => component.DataClass == RecoveryDataClass.DatabaseSnapshot).ToArray();
            if (databaseComponents.Length == 0) throw new RecoveryWorkerException("purge_snapshot_component_missing");
            foreach (var component in databaseComponents)
            {
                var receipt = point.Receipts.SingleOrDefault(item => item.SourceKey == ParseS3Uri(component.SourceKey).Key &&
                    item.SourceVersionId == component.SourceVersionId && item.Sha256 == component.Sha256 &&
                    item.ByteLength == component.ByteLength && item.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString());
                if (receipt is null || receipt.DestinationKey is null ||
                    !receipt.DestinationKey.StartsWith($"snapshots/{point.Pointer.PointId:D}/", StringComparison.Ordinal))
                    throw new RecoveryWorkerException("purge_snapshot_receipt_missing");
                if (receipt.ByteLength > MaximumDatabaseDumpBytes) throw new RecoveryWorkerException("purge_snapshot_size_limit");
                var key = (receipt.DestinationBucket, receipt.DestinationKey, receipt.DestinationVersionId);
                targets[key] = new("database_snapshot", point.Pointer.PointId, receipt.DestinationBucket,
                    receipt.DestinationKey, receipt.DestinationVersionId, receipt.Sha256, receipt.ByteLength,
                    proposal.EligibleAtUtc, false);
            }
        }
        if (!targets.Values.Any(item => item.Kind == "media")) throw new RecoveryWorkerException("purge_target_copy_missing");
        return targets.Values.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.PointId).ThenBy(item => item.Bucket, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.VersionId, StringComparer.Ordinal).ToArray();
    }

    internal static async Task<RecoveryPurgeExecutionReceiptV1> ExecuteAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        IAmazonDynamoDB dynamo,
        RecoveryPurgeProposalV1 proposal,
        RecoveryPurgePlanV1 plan,
        VerifiedRecoveryPurgeApproval approval,
        RecoverySigningTrust recoveryTrust,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        plan.Validate(nowUtc);
        RequireFreshApproval(approval, nowUtc);
        // The current contract has no independently verified sanitized replacement
        // snapshot. Deleting every affected dump could erase the final protected graph,
        // metadata and audit copy. Reject before installing irreversible fences or
        // making provider deletions until that obligation protocol is implemented.
        if (plan.TargetVersions.Any(target => target.Kind == "database_snapshot"))
            throw new RecoveryWorkerException("purge_snapshot_replacement_required");
        EnsurePlanMatchesProposal(plan, proposal);
        if (approval.Payload.PurgePlanDigest != plan.Digest() || approval.Payload.OperationId != plan.OperationId ||
            approval.Payload.OwnerId != plan.OwnerId || approval.Payload.Generation != plan.Generation)
            throw new RecoveryWorkerException("purge_approval_binding_invalid");
        await RequireRecoveryAccountAsync(configuration, cancellationToken);

        var mediaScopeId = MediaScope(proposal);
        var snapshotScopeId = RecoveryFenceScope.SnapshotScopeId(proposal.OwnerId, proposal.LibraryId);
        await InstallFencesAsync(dynamo, configuration.Required("FenceTableName"), proposal, plan,
            approval, mediaScopeId, snapshotScopeId, nowUtc, cancellationToken);

        // The immutable snapshot fence closes admission before the point-set equality check.
        var inventory = await ReadStrongPointInventoryAsync(dynamo, configuration.Required("ControlTableName"),
            proposal.OwnerId, proposal.LibraryId, cancellationToken);
        VerifyPlanPointSet(inventory, plan, proposal);
        var verified = new List<VerifiedPurgeCopyPoint>(inventory.Pointers.Count);
        foreach (var pointer in inventory.Pointers)
        {
            var point = await RecoveryPurgePointVerifier.VerifyAsync(configuration, s3, pointer, recoveryTrust,
                null, cancellationToken);
            verified.Add(point);
        }
        var latest = verified.OrderByDescending(item => item.SourceManifest.SnapshotCutoffUtc)
            .ThenByDescending(item => item.SourceManifest.ChangeWatermark.LastSequence)
            .ThenByDescending(item => item.Pointer.PointId).First();
        var retentionHistory = await BuildRetentionHistoryAsync(configuration, s3, verified, proposal,
            recoveryTrust, nowUtc, cancellationToken);
        var eligibleAt = Max(proposal.EligibleAtUtc, retentionHistory.EligibleAtUtc)
            ?? throw new RecoveryWorkerException("purge_retention_history_incomplete");
        if (nowUtc < eligibleAt) throw new RecoveryWorkerException("purge_retention_not_eligible");
        if (!RecoveryEvidenceCodec.SerializeCanonical(retentionHistory).AsSpan()
                .SequenceEqual(RecoveryEvidenceCodec.SerializeCanonical(plan.RetentionHistory)))
            throw new RecoveryWorkerException("purge_retention_history_changed");
        var freezeAcknowledgementSha256 = await VerifySnapshotProposalAndRetentionAsync(configuration, s3,
            latest, proposal, nowUtc, cancellationToken);
        if (freezeAcknowledgementSha256 != plan.FreezeAcknowledgementSha256)
            throw new RecoveryWorkerException("purge_freeze_acknowledgement_changed");
        var rebuiltTargets = BuildTargetVersions(verified, latest, proposal)
            .Select(item => item with { EligibleAtUtc = eligibleAt }).ToArray();
        if (!SameTargetPlan(rebuiltTargets, plan.TargetVersions))
            throw new RecoveryWorkerException("purge_inventory_changed");

        var writerSet = await ReadAndRequireSettledWriterIntentsAsync(dynamo, configuration.Required("IntentTableName"),
            verified, proposal, mediaScopeId, snapshotScopeId, cancellationToken);
        if (writerSet.Digest != plan.SettledWriterIntentSetSha256)
            throw new RecoveryWorkerException("purge_writer_intent_set_changed");
        foreach (var target in plan.TargetVersions)
        {
            if (target.Kind == "media" && target.RetainedForSharedIdentity) continue;
            if (target.Bucket != configuration.Required("DestinationBucket") ||
                target.Kind == "media" && !target.Key.StartsWith("payload/sha256/", StringComparison.Ordinal) ||
                target.Kind == "database_snapshot" && !target.Key.StartsWith("snapshots/", StringComparison.Ordinal))
                throw new RecoveryWorkerException("purge_target_outside_recovery_namespace");
            await S3ArtifactWriter.VerifyExactAsync(s3, target.Bucket, target.Key, target.VersionId,
                target.Sha256, target.ByteLength, cancellationToken);
        }

        var deletedCount = 0L;
        var deletedBytes = 0L;
        foreach (var target in plan.TargetVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Kind == "media" && target.RetainedForSharedIdentity) continue;
            // Verifying a large history can outlast the approval. Never begin another
            // provider deletion after the MFA window or immutable plan has expired.
            var deletionAt = DateTimeOffset.UtcNow;
            plan.Validate(deletionAt);
            RequireFreshApproval(approval, deletionAt);
            var existed = await ExactVersionExistsAsync(s3, target, cancellationToken);
            if (existed)
            {
                var response = await s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = target.Bucket,
                    Key = target.Key,
                    VersionId = target.VersionId
                }, cancellationToken);
                if (response.VersionId is not null && response.VersionId != target.VersionId)
                    throw new RecoveryWorkerException("purge_delete_version_mismatch");
            }
            if (await ExactVersionExistsAsync(s3, target, cancellationToken))
                throw new RecoveryWorkerException("purge_exact_version_still_present");
            deletedCount = checked(deletedCount + 1);
            deletedBytes = checked(deletedBytes + target.ByteLength);
        }

        // Fences remain active. A separate MFA release action follows primary/export cleanup.
        return new RecoveryPurgeExecutionReceiptV1(
            RecoveryPurgeExecutionReceiptV1.CurrentSchemaVersion,
            proposal.OperationId, proposal.EventId, proposal.OwnerId, proposal.LibraryId,
            proposal.AssetId, proposal.EditId, proposal.Category, proposal.ContentSha256,
            proposal.Generation, plan.SnapshotFenceGeneration, proposal.PlanDigest, plan.Digest(),
            approval.PayloadSha256, plan.AffectedPointSetSha256, deletedCount, deletedBytes,
            plan.TargetVersions.LongCount(item => item.Kind == "media" && item.RetainedForSharedIdentity),
            writerSet.Digest, nowUtc.ToUniversalTime(), configuration.WorkerBuildDigest);
    }

    internal static void RequireFreshApproval(VerifiedRecoveryPurgeApproval approval, DateTimeOffset nowUtc)
    {
        approval.Payload.Validate();
        if (approval.Payload.ApprovedAtUtc > nowUtc || approval.Payload.ExpiresAtUtc <= nowUtc)
            throw new RecoveryWorkerException("purge_approval_expired");
    }

    private static async Task InstallFencesAsync(
        IAmazonDynamoDB dynamo,
        string fenceTable,
        RecoveryPurgeProposalV1 proposal,
        RecoveryPurgePlanV1 plan,
        VerifiedRecoveryPurgeApproval approval,
        string mediaScope,
        string snapshotScope,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var mediaFence = BuildFence(mediaScope, proposal, plan, approval, nowUtc);
        var snapshotFence = BuildFence(snapshotScope, proposal, plan, approval, nowUtc);
        var mediaHistory = BuildFenceHistory(mediaScope, proposal, plan, approval, nowUtc);
        var snapshotHistory = BuildFenceHistory(snapshotScope, proposal, plan, approval, nowUtc);
        var keys = new[]
        {
            Key(mediaScope, "FENCE"), Key(snapshotScope, "FENCE"),
            Key(mediaScope, HistoryId(proposal.Generation)), Key(snapshotScope, HistoryId(proposal.Generation))
        };
        var items = new[] { mediaFence, snapshotFence, mediaHistory, snapshotHistory };
        var writes = items.Select(item => new TransactWriteItem
        {
            Put = new Put
            {
                TableName = fenceTable,
                Item = item,
                ConditionExpression = "attribute_not_exists(scopeId)"
            }
        }).ToArray();
        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes.ToList() }, cancellationToken);
        }
        catch (TransactionCanceledException)
        {
            // A retry is accepted only if all four exact immutable fence records already match.
            for (var i = 0; i < keys.Length; i++)
            {
                var existing = await GetItemAsync(dynamo, fenceTable, keys[i], cancellationToken);
                if (existing is null || !SameAttributes(existing, items[i]))
                    throw new RecoveryWorkerException("purge_fence_conflict");
            }
        }
    }

    private static Dictionary<string, AttributeValue> BuildFence(string scopeId,
        RecoveryPurgeProposalV1 proposal, RecoveryPurgePlanV1 plan,
        VerifiedRecoveryPurgeApproval approval, DateTimeOffset nowUtc) => new()
    {
        ["scopeId"] = S(scopeId), ["recordId"] = S("FENCE"), ["recordType"] = S("active_purge_fence"),
        ["generation"] = N(proposal.Generation), ["purgeJobId"] = S(proposal.OperationId.ToString("D")),
        ["eventId"] = S(proposal.EventId.ToString("D")), ["ownerId"] = S(proposal.OwnerId),
        ["libraryId"] = S(proposal.LibraryId), ["assetId"] = S(proposal.AssetId.ToString("D")),
        ["editId"] = S(proposal.EditId?.ToString("D") ?? ""), ["category"] = S(proposal.Category),
        ["contentSha256"] = S(proposal.ContentSha256), ["proposalDigest"] = S(proposal.PlanDigest),
        ["planDigest"] = S(plan.Digest()), ["approvalSha256"] = S(approval.PayloadSha256),
        ["installedAtUtc"] = S(nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
    };

    private static Dictionary<string, AttributeValue> BuildFenceHistory(string scopeId,
        RecoveryPurgeProposalV1 proposal, RecoveryPurgePlanV1 plan,
        VerifiedRecoveryPurgeApproval approval, DateTimeOffset nowUtc) => new()
    {
        ["scopeId"] = S(scopeId), ["recordId"] = S(HistoryId(proposal.Generation)),
        ["recordType"] = S("purge_fence_generation"), ["generation"] = N(proposal.Generation),
        ["purgeJobId"] = S(proposal.OperationId.ToString("D")), ["eventId"] = S(proposal.EventId.ToString("D")),
        ["ownerId"] = S(proposal.OwnerId), ["libraryId"] = S(proposal.LibraryId),
        ["planDigest"] = S(plan.Digest()), ["approvalSha256"] = S(approval.PayloadSha256),
        ["installedAtUtc"] = S(nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
    };

    private static async Task<PointInventory> ReadStrongPointInventoryAsync(
        IAmazonDynamoDB dynamo, string table, string ownerId, string libraryId,
        CancellationToken cancellationToken)
    {
        var ownerPartition = RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId);
        var marker = await GetItemAsync(dynamo, table, Key(ownerPartition, "INDEX"), cancellationToken)
            ?? throw new RecoveryWorkerException("purge_point_index_missing");
        if (String(marker, "status") != "complete" || Number(marker, "indexVersion") != 1)
            throw new RecoveryWorkerException("purge_point_index_incomplete");

        var rows = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var page = await dynamo.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                KeyConditionExpression = "pk=:owner",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":owner"] = S(ownerPartition) },
                ExclusiveStartKey = start,
                Limit = 1000
            }, cancellationToken);
            rows.AddRange(page.Items);
            if (rows.Count > MaximumIndexedPoints + 1) throw new RecoveryWorkerException("purge_point_index_limit");
            start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        } while (start is not null);

        var pointRows = rows.Where(item => String(item, "sk").StartsWith("POINT#", StringComparison.Ordinal)).ToArray();
        if (rows.Count != pointRows.Length + 1 || !rows.Any(item => String(item, "sk") == "INDEX"))
            throw new RecoveryWorkerException("purge_point_index_shape_invalid");
        var pointIds = new List<Guid>(pointRows.Length);
        var bootstrapIds = new List<Guid>();
        foreach (var item in pointRows)
        {
            var sortKey = String(item, "sk");
            if (!sortKey.StartsWith("POINT#", StringComparison.Ordinal) ||
                !Guid.TryParseExact(sortKey.AsSpan("POINT#".Length), "D", out var pointId) || pointId == Guid.Empty ||
                String(item, "pointId") != pointId.ToString("D").ToLowerInvariant() ||
                !IsHash(String(item, "sourceManifestSha256")))
                throw new RecoveryWorkerException("purge_point_index_row_invalid");
            pointIds.Add(pointId);
            if (Bool(item, "bootstrapMember")) bootstrapIds.Add(pointId);
        }
        var indexedCount = Number(marker, "indexedPointCount");
        var bootstrapCount = Number(marker, "bootstrapPointCount");
        var bootstrapDigest = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(ownerId, libraryId, bootstrapIds);
        if (indexedCount != pointRows.LongLength || bootstrapCount != bootstrapIds.Count ||
            String(marker, "bootstrapPointSetSha256") != bootstrapDigest ||
            pointIds.Distinct().Count() != pointIds.Count)
            throw new RecoveryWorkerException("purge_point_index_count_mismatch");

        var pointers = new List<CopyPointer>(pointRows.Length);
        foreach (var pointId in pointIds.Order())
        {
            var pointItem = await GetItemAsync(dynamo, table,
                Key("POINT#" + pointId.ToString("D").ToLowerInvariant(), "COPY"), cancellationToken)
                ?? throw new RecoveryWorkerException("purge_copy_pointer_missing");
            var pointer = CopyPointer.From(pointItem);
            if (pointer is null || pointer.PointId != pointId || pointer.OwnerId != ownerId || pointer.LibraryId != libraryId)
                throw new RecoveryWorkerException("purge_copy_pointer_scope_mismatch");
            pointers.Add(pointer);
        }
        return new(pointers, pointIds, bootstrapIds, bootstrapDigest, indexedCount);
    }

    private static async Task<WriterIntentSet> ReadAndRequireSettledWriterIntentsAsync(
        IAmazonDynamoDB dynamo, string table, IReadOnlyList<VerifiedPurgeCopyPoint> points,
        RecoveryPurgeProposalV1 proposal, string mediaScopeId, string snapshotScopeId,
        CancellationToken cancellationToken)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal) { mediaScopeId, snapshotScopeId };
        foreach (var point in points)
        {
            foreach (var reference in point.SourceManifest.Objects.Where(item => item.Sha256 == proposal.ContentSha256)
                         .SelectMany(item => item.LogicalReferences)
                         .Where(item => SameIdentity(item, new(proposal.OwnerId, proposal.LibraryId, proposal.AssetId, proposal.EditId))))
            {
                var category = proposal.Category;
                scopes.Add(RecoveryFenceScope.MediaScopeId(reference, category, proposal.ContentSha256));
            }
        }

        var intents = new List<string>();
        var readCount = 0;
        foreach (var scope in scopes.Order(StringComparer.Ordinal))
        {
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var page = await dynamo.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    ConsistentRead = true,
                    KeyConditionExpression = "scopeId=:scope",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":scope"] = S(scope) },
                    ExclusiveStartKey = start,
                    Limit = 1000
                }, cancellationToken);
                foreach (var item in page.Items)
                {
                    readCount++;
                    if (readCount > MaximumIntentItems) throw new RecoveryWorkerException("purge_intent_limit");
                    var state = String(item, "state");
                    if (state is not ("settled" or "cleaned"))
                        throw new RecoveryWorkerException("purge_writer_outcome_unresolved");
                    var destinationVersion = String(item, "destinationVersionId");
                    if (state == "settled" && (string.IsNullOrWhiteSpace(destinationVersion) || destinationVersion == "null"))
                        throw new RecoveryWorkerException("purge_writer_receipt_missing");
                    intents.Add(string.Join("\0", scope, String(item, "recordId"), state, destinationVersion,
                        String(item, "providerReceipt")));
                }
                start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            } while (start is not null);
        }
        intents.Sort(StringComparer.Ordinal);
        var digest = RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(string.Join("\n", intents)));
        return new(intents.Count, digest);
    }

    private static async Task<RecoveryRetentionHistoryProofV1> BuildRetentionHistoryAsync(
        RecoveryWorkerConfiguration configuration, IAmazonS3 s3,
        IReadOnlyList<VerifiedPurgeCopyPoint> points, RecoveryPurgeProposalV1 proposal,
        RecoverySigningTrust trust, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var observations = new List<RecoveryRetentionPointObservationV1>(points.Count);
        foreach (var point in points)
        {
            var dump = point.Receipts.SingleOrDefault(item => item.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString())
                ?? throw new RecoveryWorkerException("purge_snapshot_receipt_missing");
            if (dump.ByteLength > MaximumDatabaseDumpBytes ||
                dump.DestinationBucket != configuration.Required("DestinationBucket") ||
                !dump.DestinationKey.StartsWith("snapshots/", StringComparison.Ordinal))
                throw new RecoveryWorkerException("purge_snapshot_identity_invalid");
            var directory = Path.Combine(Path.GetTempPath(), "recovery-purge", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var dumpPath = Path.Combine(directory, "snapshot.dump");
                await RecoveryDrillRunner.DownloadExactFileAsync(s3, dump, dumpPath, cancellationToken);
                var rows = new Dictionary<string, IReadOnlyList<Dictionary<string, string?>>>(StringComparer.Ordinal);
                foreach (var table in new[] { "assets", "edit_identities", "lifecycle_holds", "recovery_holds" })
                {
                    var output = Path.Combine(directory, table + ".sql");
                    await RunDumpExtractAsync(configuration.Optional("PgRestorePath") ?? "pg_restore", table,
                        dumpPath, output, cancellationToken);
                    rows.Add(table, ReadCopyRows(output));
                }
                observations.Add(ReadRetentionObservation(rows, point, proposal, trust.KeyId));
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        return RetentionService.VerifyHistory(proposal.OwnerId, proposal.LibraryId, proposal.AssetId,
            proposal.EditId, proposal.Category, proposal.ContentSha256, trust.KeyId,
            points.Select(point => point.Pointer.PointId).ToArray(), observations, nowUtc);
    }

    internal static RecoveryRetentionPointObservationV1 ReadRetentionObservation(
        IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, string?>>> rows,
        VerifiedPurgeCopyPoint point, RecoveryPurgeProposalV1 proposal, string signingKeyId)
    {
        var asset = rows["assets"].SingleOrDefault(row => Value(row, "id") == proposal.AssetId.ToString("D"));
        if (asset is not null && Value(asset, "owner_id") != proposal.OwnerId)
            throw new RecoveryWorkerException("purge_retention_owner_mismatch");
        var target = proposal.EditId is null ? asset : rows["edit_identities"].SingleOrDefault(row =>
            Value(row, "id") == proposal.EditId.Value.ToString("D"));
        if (proposal.EditId is not null && target is not null &&
            (asset is null || Value(target, "asset_id") != proposal.AssetId.ToString("D")))
            throw new RecoveryWorkerException("purge_retention_identity_mismatch");
        var holds = new List<RecoveryRetentionHoldObservationV1>();
        foreach (var kind in new[] { "lifecycle", "recovery" })
        {
            foreach (var hold in rows[kind + "_holds"])
            {
                if (kind == "lifecycle" && Value(hold, "asset_id") != proposal.AssetId.ToString("D")) continue;
                if (kind == "recovery" && (Value(hold, "owner_id") != proposal.OwnerId ||
                    Value(hold, "library_id") != proposal.LibraryId ||
                    Value(hold, "asset_id") is { } heldAsset && heldAsset != proposal.AssetId.ToString("D") ||
                    Value(hold, "edit_id") is { } heldEdit && heldEdit != proposal.EditId?.ToString("D"))) continue;
                var expires = ParseTime(Value(hold, kind == "lifecycle" ? "expires_at" : "expires_at_utc"));
                var scope = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
                {
                    kind, assetId = Value(hold, "asset_id"), editId = Value(hold, "edit_id"),
                    ownerId = Value(hold, "owner_id"), libraryId = Value(hold, "library_id"),
                    sourceReference = Value(hold, "source_reference")
                }));
                holds.Add(new(Guid.Parse(Value(hold, "id") ?? throw new RecoveryWorkerException("purge_hold_id_missing")),
                    kind, scope, expires, expires is null || expires > point.SourceManifest.SnapshotCutoffUtc));
            }
        }
        return new(point.Pointer.PointId, point.SourceManifest.SnapshotCutoffUtc, point.Checkpoint.VerifiedAtUtc,
            point.CheckpointPayloadSha256, point.CheckpointEnvelopeSha256, signingKeyId,
            target is null ? null : Value(target, "state"), ReadVersion(target), ReadVersion(asset),
            target is null ? null : Value(target, proposal.EditId is null ? "original_sha256" : "xmp_sha256")?.Trim(),
            target is null ? null : ParseTime(Value(target, "deleted_until")), holds);
    }

    private static long ReadVersion(Dictionary<string, string?>? row) => row is null ? 0 :
        long.TryParse(Value(row, "lifecycle_version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version > 0 ? version : throw new RecoveryWorkerException("purge_retention_version_invalid");

    private static DateTimeOffset? ParseTime(string? value) => value is null ? null :
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time.ToUniversalTime() : throw new RecoveryWorkerException("purge_retention_time_invalid");

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left >= right ? left : right;

    private static async Task<string> VerifySnapshotProposalAndRetentionAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 s3,
        VerifiedPurgeCopyPoint point,
        RecoveryPurgeProposalV1 proposal,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var dump = point.Receipts.SingleOrDefault(item => item.DataClass == RecoveryDataClass.DatabaseSnapshot.ToString())
            ?? throw new RecoveryWorkerException("purge_snapshot_receipt_missing");
        if (dump.ByteLength > MaximumDatabaseDumpBytes ||
            !dump.DestinationBucket.Equals(configuration.Required("DestinationBucket"), StringComparison.Ordinal) ||
            !dump.DestinationKey.StartsWith("snapshots/", StringComparison.Ordinal))
            throw new RecoveryWorkerException("purge_snapshot_identity_invalid");

        var directory = Path.Combine(Path.GetTempPath(), "recovery-purge", proposal.OperationId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dumpPath = Path.Combine(directory, point.Pointer.PointId.ToString("N") + ".dump");
        var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await RecoveryDrillRunner.DownloadExactFileAsync(s3, dump, dumpPath, cancellationToken);
            var pgRestore = configuration.Optional("PgRestorePath") ?? "pg_restore";
            var tableNames = new[] { "assets", "edit_identities", "lifecycle_holds", "recovery_holds", "recovery_purge_proposals", "recovery_purge_freezes" };
            foreach (var table in tableNames)
            {
                var output = Path.Combine(directory, table + ".sql");
                await RunDumpExtractAsync(pgRestore, table, dumpPath, output, cancellationToken);
                extracted.Add(table, output);
            }
            return VerifyProposalAndTargetRows(extracted, proposal, nowUtc,
                point.SourceManifest.SnapshotCutoffUtc);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static async Task RunDumpExtractAsync(string pgRestore, string table, string dumpPath,
        string outputPath, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(pgRestore)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--data-only", "--table=public." + table, "--file", outputPath, dumpPath })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new RecoveryWorkerException("pg_restore_start_failed");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stderr;
        if (process.ExitCode != 0) throw new RecoveryWorkerException("purge_snapshot_extract_failed");
    }

    internal static string VerifyProposalAndTargetRows(
        IReadOnlyDictionary<string, string> files,
        RecoveryPurgeProposalV1 proposal,
        DateTimeOffset nowUtc,
        DateTimeOffset snapshotCutoffUtc)
    {
        var assets = ReadCopyRows(files["assets"]);
        var asset = assets.SingleOrDefault(row => Value(row, "id") == proposal.AssetId.ToString("D"))
            ?? throw new RecoveryWorkerException("purge_lifecycle_target_missing");
        if (Value(asset, "owner_id") != proposal.OwnerId ||
            proposal.Category == "xmp" && Value(asset, "state") is not ("active" or "restored") ||
            proposal.Category == "original" && (Value(asset, "state") != "recoverable-deleted" ||
            !long.TryParse(Value(asset, "lifecycle_version"), NumberStyles.None, CultureInfo.InvariantCulture, out var lifecycleVersion) ||
            lifecycleVersion != proposal.ExpectedLifecycleVersion ||
            !DateTimeOffset.TryParse(Value(asset, "deleted_until"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deletedUntil) ||
            deletedUntil.ToUniversalTime() > nowUtc ||
            Value(asset, "original_sha256")?.Trim() != proposal.ContentSha256))
            throw new RecoveryWorkerException("purge_lifecycle_target_changed");

        if (proposal.Category == "xmp")
        {
            var edits = ReadCopyRows(files["edit_identities"]);
            var edit = edits.SingleOrDefault(row => Value(row, "id") == proposal.EditId!.Value.ToString("D"))
                ?? throw new RecoveryWorkerException("purge_lifecycle_target_missing");
            if (Value(edit, "asset_id") != proposal.AssetId.ToString("D") ||
                Value(edit, "xmp_sha256")?.Trim() != proposal.ContentSha256 ||
                Value(edit, "state") != "recoverable-deleted" ||
                !long.TryParse(Value(edit, "lifecycle_version"), NumberStyles.None, CultureInfo.InvariantCulture, out lifecycleVersion) ||
                lifecycleVersion != proposal.ExpectedLifecycleVersion ||
                !DateTimeOffset.TryParse(Value(edit, "deleted_until"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deletedUntil) ||
                deletedUntil.ToUniversalTime() > nowUtc)
                throw new RecoveryWorkerException("purge_lifecycle_target_changed");
        }

        foreach (var row in ReadCopyRows(files["lifecycle_holds"]))
        {
            if (Value(row, "asset_id") != proposal.AssetId.ToString("D")) continue;
            var expiry = Value(row, "expires_at");
            if (expiry is null || !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt.ToUniversalTime() > nowUtc)
                throw new RecoveryWorkerException("purge_active_lifecycle_hold");
        }

        foreach (var row in ReadCopyRows(files["recovery_holds"]))
        {
            if (Value(row, "owner_id") != proposal.OwnerId || Value(row, "library_id") != proposal.LibraryId ||
                Value(row, "asset_id") is { } heldAsset && heldAsset != proposal.AssetId.ToString("D") ||
                Value(row, "edit_id") is { } heldEdit && heldEdit != proposal.EditId?.ToString("D")) continue;
            if (Value(row, "released_at_utc") is not null) continue;
            var expiry = Value(row, "expires_at_utc");
            if (expiry is null || !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt.ToUniversalTime() > nowUtc)
                throw new RecoveryWorkerException("purge_active_recovery_hold");
        }

        var proposalRows = ReadCopyRows(files["recovery_purge_proposals"]);
        var proposalRow = proposalRows.SingleOrDefault(row => Value(row, "operation_id") == proposal.OperationId.ToString("D"))
            ?? throw new RecoveryWorkerException("purge_snapshot_proposal_missing");
        var stored = Value(proposalRow, "canonical_proposal")
            ?? throw new RecoveryWorkerException("purge_snapshot_proposal_invalid");
        if (!string.Equals(stored, "\\x" + Convert.ToHexString(RecoveryEvidenceCodec.SerializeCanonical(proposal)).ToLowerInvariant(), StringComparison.Ordinal) &&
            !string.Equals(stored, Convert.ToHexString(RecoveryEvidenceCodec.SerializeCanonical(proposal)).ToLowerInvariant(), StringComparison.Ordinal))
            throw new RecoveryWorkerException("purge_snapshot_proposal_mismatch");
        if (Value(proposalRow, "proposal_digest")?.Trim() != proposal.PlanDigest ||
            Value(proposalRow, "phase") != "executing")
            throw new RecoveryWorkerException("purge_snapshot_proposal_mismatch");

        var freezeRows = ReadCopyRows(files["recovery_purge_freezes"]);
        var freezeRow = freezeRows.SingleOrDefault(row => Value(row, "operation_id") == proposal.OperationId.ToString("D"))
            ?? throw new RecoveryWorkerException("purge_snapshot_freeze_missing");
        var freezeBytes = ParseCopyBytea(Value(freezeRow, "canonical_freeze_ack"));
        var freezeAck = RecoveryPurgeFreezeAcknowledgementV1.ParseCanonical(freezeBytes);
        freezeAck.Validate(proposal);
        var freezeDigest = RecoveryEvidenceCodec.Hash(freezeBytes);
        if (freezeDigest != Value(freezeRow, "freeze_ack_sha256")?.Trim() ||
            Value(freezeRow, "proposal_digest")?.Trim() != proposal.PlanDigest ||
            Value(freezeRow, "owner_id") != proposal.OwnerId || Value(freezeRow, "library_id") != proposal.LibraryId ||
            Value(freezeRow, "asset_id") != proposal.AssetId.ToString("D") ||
            Value(freezeRow, "category") != proposal.Category ||
            Value(freezeRow, "content_sha256")?.Trim() != proposal.ContentSha256 ||
            !DateTimeOffset.TryParse(Value(freezeRow, "frozen_at_utc"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var frozenAt) || frozenAt.ToUniversalTime() != freezeAck.FrozenAtUtc ||
            freezeAck.FrozenAtUtc > nowUtc || freezeAck.FrozenAtUtc > snapshotCutoffUtc)
            throw new RecoveryWorkerException("purge_snapshot_freeze_mismatch");
        return freezeDigest;
    }

    private static byte[] ParseCopyBytea(string? value)
    {
        if (value is null || !value.StartsWith("\\x", StringComparison.Ordinal) || value.Length % 2 != 0)
            throw new RecoveryWorkerException("purge_snapshot_bytea_invalid");
        try { return Convert.FromHexString(value.AsSpan(2)); }
        catch (FormatException exception) { throw new RecoveryWorkerException("purge_snapshot_bytea_invalid", exception); }
    }

    private static IReadOnlyList<Dictionary<string, string?>> ReadCopyRows(string path)
    {
        var lines = File.ReadLines(path);
        string[]? columns = null;
        var rows = new List<Dictionary<string, string?>>();
        var inCopy = false;
        foreach (var line in lines)
        {
            if (!inCopy)
            {
                if (!line.StartsWith("COPY public.", StringComparison.Ordinal) ||
                    !line.Contains(" FROM stdin;", StringComparison.Ordinal)) continue;
                var open = line.IndexOf('(');
                var close = line.IndexOf(')', open + 1);
                if (open < 0 || close < 0) throw new RecoveryWorkerException("purge_snapshot_copy_header_invalid");
                columns = line[(open + 1)..close].Split(',').Select(item => item.Trim()).ToArray();
                inCopy = true;
                continue;
            }
            if (line == "\\.") { inCopy = false; columns = null; continue; }
            var values = line.Split('\t');
            if (columns is null || values.Length != columns.Length)
                throw new RecoveryWorkerException("purge_snapshot_copy_row_invalid");
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Length; i++) row.Add(columns[i], DecodeCopyField(values[i]));
            rows.Add(row);
        }
        if (inCopy) throw new RecoveryWorkerException("purge_snapshot_copy_terminator_missing");
        return rows;
    }

    private static string? DecodeCopyField(string value)
    {
        if (value == "\\N") return null;
        var output = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { output.Append(value[i]); continue; }
            var escaped = value[++i];
            output.Append(escaped switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', '\\' => '\\', _ => escaped });
        }
        return output.ToString();
    }

    private static async Task<bool> ExactVersionExistsAsync(IAmazonS3 s3,
        RecoveryPurgeTargetVersionV1 target, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = target.Bucket, Key = target.Key, VersionId = target.VersionId
            }, cancellationToken);
            if (metadata.VersionId != target.VersionId || metadata.ContentLength != target.ByteLength)
                throw new RecoveryWorkerException("purge_exact_version_identity_mismatch");
            return true;
        }
        catch (AmazonS3Exception exception) when (IsExactVersionAbsent(exception))
        {
            return false;
        }
    }

    private static bool IsExactVersionAbsent(AmazonS3Exception exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.NotFound &&
        exception.ErrorCode is "NoSuchVersion" or "NotFound";

    private static bool SameTargetPlan(IReadOnlyList<RecoveryPurgeTargetVersionV1> left,
        IReadOnlyList<RecoveryPurgeTargetVersionV1> right)
    {
        var leftBytes = RecoveryEvidenceCodec.SerializeCanonical(left.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.PointId).ThenBy(item => item.Bucket, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.VersionId, StringComparer.Ordinal).ToArray());
        var rightBytes = RecoveryEvidenceCodec.SerializeCanonical(right.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.PointId).ThenBy(item => item.Bucket, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.VersionId, StringComparer.Ordinal).ToArray());
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }

    private static void VerifyPlanPointSet(PointInventory inventory, RecoveryPurgePlanV1 plan,
        RecoveryPurgeProposalV1 proposal)
    {
        var actualDigest = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(proposal.OwnerId,
            proposal.LibraryId, inventory.PointIds);
        if (inventory.PointIds.Count != plan.OwnerPointCount || actualDigest != plan.OwnerPointSetSha256)
            throw new RecoveryWorkerException("purge_owner_point_set_changed");
    }

    private static async Task VerifyExecutorIdentityAsync(RecoveryWorkerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var region = configuration.RequiredRegion("DestinationRegion");
        var expectedAccount = configuration.RequiredAccountId("DestinationAccountId");
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
        if (identity.Account != expectedAccount || !MatchesAssumedRole(identity.Arn,
                configuration.Required("PurgeExecutorRoleArn")))
            throw new RecoveryWorkerException("purge_executor_identity_mismatch");
    }

    private static bool MatchesAssumedRole(string? callerArn, string roleArn)
    {
        if (callerArn is null || !roleArn.StartsWith("arn:", StringComparison.Ordinal)) return false;
        var roleParts = roleArn.Split(':', 6);
        var callerParts = callerArn.Split(':', 6);
        if (roleParts.Length != 6 || callerParts.Length != 6 || roleParts[0] != callerParts[0] ||
            roleParts[1] != callerParts[1] || roleParts[4] != callerParts[4] || !roleParts[5].StartsWith("role/", StringComparison.Ordinal) ||
            !callerParts[5].StartsWith("assumed-role/", StringComparison.Ordinal)) return false;
        var expectedPath = roleParts[5]["role/".Length..];
        var callerRolePath = callerParts[5]["assumed-role/".Length..];
        var finalSlash = callerRolePath.LastIndexOf('/');
        return finalSlash > 0 && callerRolePath[..finalSlash] == expectedPath;
    }

    private static async Task RequireRecoveryAccountAsync(RecoveryWorkerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(configuration.RequiredRegion("DestinationRegion")));
        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
        if (identity.Account != configuration.RequiredAccountId("DestinationAccountId"))
            throw new RecoveryWorkerException("recovery_task_account_mismatch");
    }

    private static RecoverySigningTrust ReadTrust(RecoveryWorkerConfiguration configuration, string prefix)
    {
        var trust = new RecoverySigningTrust(configuration.Required(prefix + "__KeyId"),
            configuration.Required(prefix + "__PublicKeySpkiBase64"),
            configuration.RequiredHash(prefix + "__PublicKeySha256"));
        try { _ = trust.ValidateAndGetPublicKey(); }
        catch (InvalidDataException exception) { throw new RecoveryWorkerException("purge_trust_invalid", exception); }
        return trust;
    }

    private static void EnsurePlanMatchesProposal(RecoveryPurgePlanV1 plan, RecoveryPurgeProposalV1 proposal)
    {
        if (plan.OperationId != proposal.OperationId || plan.EventId != proposal.EventId ||
            plan.ProposalDigest != proposal.PlanDigest || plan.OwnerId != proposal.OwnerId ||
            plan.LibraryId != proposal.LibraryId || plan.AssetId != proposal.AssetId ||
            plan.EditId != proposal.EditId || plan.Category != proposal.Category ||
            plan.ContentSha256 != proposal.ContentSha256 || plan.Generation != proposal.Generation)
            throw new RecoveryWorkerException("purge_plan_proposal_mismatch");
    }

    private static bool SameIdentity(RecoveryLogicalReference left, RecoveryLogicalReference right) =>
        left.OwnerId == right.OwnerId && left.LibraryId == right.LibraryId &&
        left.AssetId == right.AssetId && left.EditId == right.EditId;

    private static string MediaScope(RecoveryPurgeProposalV1 proposal) => RecoveryFenceScope.MediaScopeId(
        new RecoveryLogicalReference(proposal.OwnerId, proposal.LibraryId, proposal.AssetId, proposal.EditId),
        proposal.Category, proposal.ContentSha256);

    internal static string ComputePointSetSha256(IEnumerable<Guid> pointIds)
    {
        var ids = pointIds.Select(item => item.ToString("D").ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        if (ids.Any(item => item == Guid.Empty.ToString("D")) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("purge_point_set_invalid");
        return RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(ids));
    }

    private static (string Bucket, string Key) ParseS3Uri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "s3" || string.IsNullOrWhiteSpace(uri.Host))
            throw new RecoveryWorkerException("purge_source_identity_invalid");
        return (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }

    private static string? Value(IReadOnlyDictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    private static async Task<Dictionary<string, AttributeValue>?> GetItemAsync(IAmazonDynamoDB dynamo,
        string table, Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table, Key = key, ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    internal static async Task<byte[]> ReadBoundedArtifactAsync(string path, int maximumBytes, CancellationToken ct)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximumBytes) throw new RecoveryWorkerException("purge_artifact_size_invalid");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        if (input.ReadByte() != -1) throw new RecoveryWorkerException("purge_artifact_size_invalid");
        return bytes;
    }

    private static async Task WritePrivateArtifactAsync(string path, byte[] bytes,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (PlatformNotSupportedException) { throw new RecoveryWorkerException("purge_artifact_permissions_unavailable"); }
        }
    }

    private static string HistoryId(long generation) => "FENCE#" + generation.ToString("D20", CultureInfo.InvariantCulture);
    private static bool SameAttributes(Dictionary<string, AttributeValue> actual, Dictionary<string, AttributeValue> expected) =>
        expected.Where(pair => pair.Key != "installedAtUtc").All(pair =>
            actual.TryGetValue(pair.Key, out var value) && value.S == pair.Value.S && value.N == pair.Value.N);
    private static Dictionary<string, AttributeValue> Key(string scope, string record) => new() { ["scopeId"] = S(scope), ["recordId"] = S(record) };
    private static string String(Dictionary<string, AttributeValue> item, string name) => item.TryGetValue(name, out var value) ? value.S ?? "" : "";
    private static long Number(Dictionary<string, AttributeValue> item, string name) => item.TryGetValue(name, out var value) &&
        long.TryParse(value.N, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1;
    private static bool Bool(Dictionary<string, AttributeValue> item, string name) => item.TryGetValue(name, out var value) && value.BOOL == true;
    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private sealed record PointInventory(IReadOnlyList<CopyPointer> Pointers, IReadOnlyList<Guid> PointIds,
        IReadOnlyList<Guid> BootstrapPointIds, string BootstrapDigest, long IndexedPointCount);
    private sealed record WriterIntentSet(int Count, string Digest);

    private sealed record PurgeJobEvidenceV1(int SchemaVersion, Guid OperationId, Guid EventId,
        string OwnerId, string LibraryId, long Generation, long SnapshotFenceGeneration,
        string ProposalDigest, string PlanDigest, string ApprovalPayloadSha256,
        long OwnerPointCount, string OwnerPointSetSha256, string AffectedPointSetSha256,
        string TargetSetSha256, long TargetCount, Guid SnapshotPointId, string SnapshotManifestSha256,
        string SnapshotValidationSha256, DateTimeOffset InstalledAtUtc);

    private sealed record PurgeTargetAdmissionV1(int SchemaVersion, Guid OperationId, Guid EventId,
        string OwnerId, string LibraryId, long Generation, string ProposalDigest, string PlanDigest,
        string TargetDigest, string Kind, Guid PointId, string Bucket, string Key, string VersionId,
        string Sha256, long ByteLength, DateTimeOffset AdmittedAtUtc);

    private sealed record PurgeTargetResultV1(int SchemaVersion, Guid OperationId, Guid EventId,
        string OwnerId, string LibraryId, long Generation, string ProposalDigest, string PlanDigest,
        string TargetDigest, string AdmissionPayloadSha256, string Kind, Guid PointId, string Bucket,
        string Key, string VersionId, string Sha256, long ByteLength, string Outcome,
        string ProviderNotFoundCode, DateTimeOffset ConfirmedAbsentAtUtc, string WorkerBuildDigest);
}
