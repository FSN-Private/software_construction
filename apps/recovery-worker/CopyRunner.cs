using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KeyManagementService;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class CopyRunner
{
    private const string LeaseDuration = "04:00:00";
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var sourceRegion = configuration.RequiredRegion("SourceRegion");
        var destinationRegion = configuration.RequiredRegion("DestinationRegion");
        var sourceAccountId = configuration.RequiredAccountId("SourceAccountId");
        var destinationAccountId = configuration.RequiredAccountId("DestinationAccountId");
        if (sourceAccountId == destinationAccountId || sourceRegion == destinationRegion)
            throw new RecoveryWorkerException("recovery_environment_not_independent");

        var primaryReadRoleArn = configuration.Required("PrimaryReadRoleArn");
        using var sts = new AmazonSecurityTokenServiceClient(RegionEndpoint.GetBySystemName(destinationRegion));
        var assumed = await sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = primaryReadRoleArn,
            RoleSessionName = "recovery-copy-" + Guid.NewGuid().ToString("N"),
            DurationSeconds = 3600
        }, cancellationToken);
        var assumedCredentials = assumed.Credentials ?? throw new RecoveryWorkerException("primary_read_assume_failed");
        var sourceCredentials = new SessionAWSCredentials(assumedCredentials.AccessKeyId,
            assumedCredentials.SecretAccessKey, assumedCredentials.SessionToken);
        using var sourceS3 = new AmazonS3Client(sourceCredentials, RegionEndpoint.GetBySystemName(sourceRegion));
        using var destinationS3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(destinationRegion));
        using var dynamo = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(destinationRegion));
        using var kms = new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(destinationRegion));
        using var sqs = new AmazonSQSClient(RegionEndpoint.GetBySystemName(sourceRegion));
        var destinationActual = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
        if (destinationActual.Account != destinationAccountId)
            throw new RecoveryWorkerException("recovery_task_account_mismatch");
        var sourceSts = new AmazonSecurityTokenServiceClient(sourceCredentials, RegionEndpoint.GetBySystemName(sourceRegion));
        try
        {
            var sourceActual = await sourceSts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            if (sourceActual.Account != sourceAccountId) throw new RecoveryWorkerException("primary_read_account_mismatch");
        }
        finally { sourceSts.Dispose(); }

        var signer = await AwsKmsEvidenceSigner.CreateAsync(kms,
            configuration.Required("SigningKeyId"), configuration.RequiredHash("SigningPublicKeySha256"), cancellationToken);
        var intentStore = new DynamoFenceIntentStore(dynamo,
            configuration.Required("FenceTableName"), configuration.Required("IntentTableName"));
        var payloadBucket = configuration.Required("DestinationBucket");
        var evidenceBucket = configuration.Required("EvidenceBucket");
        var sourceContentBucket = configuration.Required("SourceBucket");
        var sourceCaptureBucket = configuration.Required("SourceCaptureBucket");
        var payloadKmsKey = configuration.Required("DestinationKmsKeyId");
        var evidenceKmsKey = configuration.Required("EvidenceKmsKeyId");
        await CheckDestinationBucketAsync(destinationS3, payloadBucket, payloadKmsKey, destinationRegion, cancellationToken);
        await CheckDestinationBucketAsync(destinationS3, evidenceBucket, evidenceKmsKey, destinationRegion, cancellationToken);

        var payloadStore = CreateStore(sourceS3, destinationS3, sourceContentBucket, payloadBucket, payloadKmsKey, configuration);
        var capturePayloadStore = CreateStore(sourceS3, destinationS3, sourceCaptureBucket, payloadBucket, payloadKmsKey, configuration);
        var evidenceStore = CreateStore(sourceS3, destinationS3, sourceCaptureBucket, evidenceBucket, evidenceKmsKey, configuration);
        await payloadStore.CheckReadyAsync(cancellationToken);
        await capturePayloadStore.CheckReadyAsync(cancellationToken);
        await evidenceStore.CheckReadyAsync(cancellationToken);

        var completeMarkers = await ListCompletedCaptureMarkersAsync(sourceS3, sourceCaptureBucket, cancellationToken);
        var scopeHashes = new HashSet<string>(StringComparer.Ordinal);
        if (configuration.Optional("OwnerId") is { Length: > 0 } configuredOwner &&
            configuration.Optional("LibraryId") is { Length: > 0 } configuredLibrary)
            scopeHashes.Add(ScopeHash(configuredOwner, configuredLibrary));
        foreach (var markerKey in completeMarkers)
        {
            var marker = await ReadJsonAsync<CaptureCompleteV1>(sourceS3, sourceCaptureBucket, markerKey, cancellationToken);
            if (marker.SchemaVersion != 1 || marker.CaptureId == Guid.Empty || marker.ManifestObjects is null || marker.ManifestObjects.Count == 0 ||
                marker.ScopeCount != marker.ManifestObjects.Count)
                throw new RecoveryWorkerException("capture_complete_marker_invalid");
            var capturePrefix = $"captures/{marker.CaptureId:D}/";
            if (!markerKey.StartsWith(capturePrefix, StringComparison.Ordinal) ||
                marker.ManifestObjects.Any(item => item is null || !item.Key.StartsWith(capturePrefix + "scopes/", StringComparison.Ordinal) ||
                    !item.Key.EndsWith("/manifest.json", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.VersionId) ||
                    !IsHash(item.Sha256) || item.ByteLength <= 0))
                throw new RecoveryWorkerException("capture_marker_scope_invalid");

            foreach (var manifestPointer in marker.ManifestObjects.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var manifestKey = manifestPointer.Key;
                var manifestObject = await ReadVersionedBytesAsync(sourceS3, sourceCaptureBucket, manifestKey,
                    manifestPointer.VersionId, manifestPointer.Sha256, null, cancellationToken);
                if (manifestObject.Bytes.LongLength != manifestPointer.ByteLength)
                    throw new RecoveryWorkerException("capture_manifest_length_mismatch");
                var manifestBytes = manifestObject.Bytes;
                var manifest = RecoveryPointManifestV1.ParseCanonical(manifestBytes);
                if (manifest.CaptureId != marker.CaptureId || manifest.SourceRegion != sourceRegion ||
                    manifest.OwnerId.Length == 0 || manifest.LibraryId.Length == 0 ||
                    !manifestKey.Contains(ScopeHash(manifest.OwnerId, manifest.LibraryId), StringComparison.Ordinal))
                    throw new RecoveryWorkerException("capture_manifest_binding_invalid");
                scopeHashes.Add(ScopeHash(manifest.OwnerId, manifest.LibraryId));

                var leaseToken = await TryAcquirePointAsync(dynamo, configuration.Required("ControlTableName"),
                    marker.CaptureId, manifest, RecoveryEvidenceCodec.Hash(manifestBytes), configuration.WorkerId, cancellationToken);
                if (leaseToken is null) continue;

                try
                {
                    var finished = await CopyPointAsync(configuration, sourceS3, destinationS3, dynamo, sqs,
                        intentStore, payloadStore, capturePayloadStore, evidenceStore, signer, sourceCaptureBucket, sourceContentBucket,
                        evidenceBucket, payloadBucket, payloadKmsKey, evidenceKmsKey, sourceAccountId, destinationAccountId, sourceRegion,
                        destinationRegion, manifestKey, manifestObject.VersionId, manifestBytes, manifest, leaseToken, cancellationToken);
                    if (finished)
                        await CompletePointAsync(dynamo, configuration.Required("ControlTableName"), manifest,
                            leaseToken, cancellationToken);
                    else
                        await DeferPointAsync(dynamo, configuration.Required("ControlTableName"), manifest,
                            leaseToken, cancellationToken);
                }
                catch
                {
                    try
                    {
                        await PublishCycleAsync(sqs, configuration, signer, manifest, "copy_failed", cancellationToken);
                    }
                    catch { }
                    await DeferPointAsync(dynamo, configuration.Required("ControlTableName"), manifest,
                        leaseToken, cancellationToken);
                    throw;
                }
            }
        }

        await EmitVerifiedAgeAsync(dynamo, configuration, scopeHashes, cancellationToken);
    }

    private static async Task<bool> CopyPointAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonS3 sourceS3,
        IAmazonS3 destinationS3,
        IAmazonDynamoDB dynamo,
        IAmazonSQS sqs,
        DynamoFenceIntentStore intentStore,
        Platform.Recovery.RecoveryObjectStore payloadStore,
        Platform.Recovery.RecoveryObjectStore capturePayloadStore,
        Platform.Recovery.RecoveryObjectStore evidenceStore,
        AwsKmsEvidenceSigner signer,
        string sourceCaptureBucket,
        string sourceContentBucket,
        string evidenceBucket,
        string payloadBucket,
        string payloadKmsKey,
        string evidenceKmsKey,
        string sourceAccountId,
        string destinationAccountId,
        string sourceRegion,
        string destinationRegion,
        string manifestKey,
        string manifestVersionId,
        byte[] sourceManifestBytes,
        RecoveryPointManifestV1 sourceManifest,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var pointId = sourceManifest.RecoveryPointId;
        var ownerId = sourceManifest.OwnerId;
        var libraryId = sourceManifest.LibraryId;
        var release = await ReadComponentAsync<CaptureReleaseBindingV1>(sourceS3, sourceCaptureBucket,
            sourceManifest, "release-bindings.json", cancellationToken);
        var captureConfiguration = await ReadComponentAsync<CaptureConfigurationV1>(sourceS3, sourceCaptureBucket,
            sourceManifest, "configuration.json", cancellationToken);
        ValidateBuildBinding(configuration, sourceManifest, release, captureConfiguration);

        var plans = new List<PlannedCopy>();
        foreach (var objectRef in sourceManifest.Objects)
        {
            objectRef.Validate();
            if (objectRef.LogicalReferences.Count == 0) throw new RecoveryWorkerException("media_logical_reference_missing");
            plans.Add(new(sourceContentBucket, objectRef, payloadBucket,
                "payload/sha256/" + objectRef.Sha256, IsMedia: true, Admissions: []));
        }
        foreach (var component in sourceManifest.Components)
        {
            var (bucket, key) = ParseS3Uri(component.SourceKey);
            if (bucket != sourceCaptureBucket) throw new RecoveryWorkerException("capture_component_bucket_mismatch");
            if (component.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new RecoveryWorkerException("capture_component_name_invalid");
            var componentRef = new RecoveryObjectReference(component.DataClass, ownerId, libraryId, key,
                component.SourceVersionId, component.Sha256, component.ByteLength, []);
            componentRef.Validate();
            var databaseSnapshot = component.DataClass == RecoveryDataClass.DatabaseSnapshot;
            var destinationBucket = databaseSnapshot ? payloadBucket : evidenceBucket;
            var destinationKey = databaseSnapshot
                ? $"snapshots/{pointId:D}/{component.Name}"
                : $"points/{pointId:D}/components/{component.Name}";
            plans.Add(new(sourceCaptureBucket, componentRef, destinationBucket,
                destinationKey, IsMedia: false, Admissions: []));
        }

        var allAdmissions = new List<LogicalWriteAdmission>();
        var admittedPlans = new List<AdmittedPlan>(plans.Count);
        var admittedAtUtc = DateTimeOffset.UtcNow;
        foreach (var plan in plans)
        {
            var admissions = new List<LogicalWriteAdmission>();
            if (plan.IsMedia)
            {
                var physicalWriteId = StableGuid(pointId.ToString("D") + "\0" + plan.Source.SourceKey + "\0" + plan.Source.SourceVersionId);
                foreach (var logical in plan.Source.LogicalReferences)
                {
                    var admission = await intentStore.AdmitMediaAsync(pointId, plan.Source, plan.SourceBucket,
                        plan.DestinationBucket, plan.DestinationKey, logical, physicalWriteId, configuration.WorkerId,
                        admittedAtUtc, cancellationToken);
                    admissions.Add(admission);
                    allAdmissions.Add(admission);
                }
            }
            else
            {
                var admission = await intentStore.AdmitComponentAsync(pointId, plan.Source, plan.SourceBucket,
                    plan.DestinationBucket, plan.DestinationKey, configuration.WorkerId, admittedAtUtc, cancellationToken);
                admissions.Add(admission);
                allAdmissions.Add(admission);
            }
            admittedPlans.Add(new(plan, admissions));
        }

        // Every logical reference is durably admitted before the first external object write.
        foreach (var admission in allAdmissions.Where(item => item.State != "settled"))
            await intentStore.SetStoragePendingAsync(admission, cancellationToken);

        var receiptJournal = new ReceiptJournalWriter(destinationS3, evidenceBucket, evidenceKmsKey, pointId);
        foreach (var admitted in admittedPlans)
        {
            try
            {
                var representative = admitted.Admissions[0];
                var writeIntent = new RecoveryWriteIntent(representative.IntentId, pointId, null,
                    ownerId, libraryId, 1, admitted.Plan.Source, admitted.Plan.DestinationKey,
                    configuration.WorkerId, RecoveryWriteState.StoragePending, representative.AdmittedAtUtc,
                    null, null, null);
                var store = admitted.Plan.DestinationBucket == payloadBucket
                    ? admitted.Plan.SourceBucket == sourceCaptureBucket ? capturePayloadStore : payloadStore
                    : evidenceStore;
                var settledVersions = admitted.Admissions.Where(item => item.State == "settled")
                    .Select(item => item.DestinationVersionId).Distinct(StringComparer.Ordinal).ToArray();
                if (settledVersions.Length > 1 || settledVersions.Length == 1 && string.IsNullOrWhiteSpace(settledVersions[0]))
                    throw new RecoveryWorkerException("settled_intent_version_mismatch");
                var receipt = settledVersions.Length == 1
                    ? await store.VerifyExistingAsync(admitted.Plan.Source, admitted.Plan.DestinationKey,
                        settledVersions[0]!, representative.AdmittedAtUtc, cancellationToken)
                    : await store.CopyVerifiedAsync(writeIntent, cancellationToken);
                var providerReceipt = JsonSerializer.Serialize(new
                {
                    receipt.Source.Sha256,
                    receipt.Source.ByteLength,
                    receipt.DestinationBucket,
                    receipt.DestinationKey,
                    receipt.DestinationVersionId,
                    receipt.VerifiedAtUtc
                }, ArtifactJson);
                foreach (var admission in admitted.Admissions)
                {
                    if (admission.State == "settled")
                    {
                        if (admission.DestinationVersionId != receipt.DestinationVersionId)
                            throw new RecoveryWorkerException("settled_intent_version_mismatch");
                    }
                    else
                    {
                        await intentStore.SetSettledAsync(admission, receipt.DestinationVersionId,
                            providerReceipt, cancellationToken);
                    }
                }
                await receiptJournal.AppendAsync(new(admitted.Plan.SourceBucket, admitted.Plan.Source.SourceKey,
                    admitted.Plan.Source.SourceVersionId, admitted.Plan.Source.Sha256,
                    admitted.Plan.Source.ByteLength, admitted.Plan.DestinationBucket,
                    admitted.Plan.DestinationKey, receipt.DestinationVersionId,
                    admitted.Plan.Source.DataClass.ToString(), receipt.VerifiedAtUtc), cancellationToken);
            }
            catch
            {
                foreach (var admission in admitted.Admissions.Where(item => item.State != "settled"))
                    await intentStore.SetUnknownAsync(admission, cancellationToken);
                throw;
            }
        }

        var sourceManifestKey = $"points/{pointId:D}/source-manifests/{RecoveryEvidenceCodec.Hash(sourceManifestBytes)}.json";
        var sourceManifestObject = await S3ArtifactWriter.PutBytesAsync(destinationS3, evidenceBucket,
            sourceManifestKey, sourceManifestBytes, evidenceKmsKey, cancellationToken);
        var sourceManifestReceipt = new PrivateArtifactReceiptV1(sourceCaptureBucket, manifestKey, manifestVersionId,
            evidenceBucket, sourceManifestObject.Key, sourceManifestObject.VersionId, sourceManifestObject.Sha256,
            sourceManifestObject.ByteLength);

        var ownerAccess = await ReadOwnerAccessProofAsync(configuration, dynamo, destinationS3,
            evidenceBucket, ownerId, libraryId, cancellationToken);
        if (ownerAccess is null)
        {
            await PublishCycleAsync(sqs, configuration, signer, sourceManifest,
                "access_policy_failed", cancellationToken);
            var receiptState = await receiptJournal.CompleteAsync(cancellationToken);
            var deferred = new PrivateCopyCheckpointPayloadV1(1, sourceManifest.CaptureId, pointId,
                ownerId, libraryId, RecoveryEvidenceCodec.Hash(sourceManifestBytes), null, sourceRegion,
                destinationRegion, destinationAccountId, DateTimeOffset.UtcNow, configuration.WorkerBuildDigest,
                receiptState.Count, receiptState.Chunks, sourceManifestReceipt, null, null);
            var deferredCheckpoint = await WriteSignedCheckpointAsync(destinationS3, evidenceBucket, evidenceKmsKey, signer,
                configuration.WorkerId, pointId, deferred, cancellationToken);
            await WriteDeferredCheckpointToControlAsync(dynamo, configuration.Required("ControlTableName"),
                sourceManifest, sourceCaptureBucket, RecoveryEvidenceCodec.Hash(sourceManifestBytes), manifestKey,
                manifestVersionId, evidenceBucket, deferredCheckpoint.Key, deferredCheckpoint.VersionId,
                deferredCheckpoint.Sha256, deferredCheckpoint.ByteLength, sourceAccountId, sourceRegion,
                destinationAccountId, destinationRegion, leaseToken, cancellationToken);
            return false;
        }

        var accessArtifactComponent = new RecoveryComponentReference("owner-access-proof", RecoveryDataClass.AccessEvidence,
            $"s3://{evidenceBucket}/{ownerAccess.ArtifactKey}", ownerAccess.VersionId,
            ownerAccess.ArtifactSha256, ownerAccess.ByteLength);
        var finalCoverage = sourceManifest.Coverage.Select(item => item.DataClass == RecoveryDataClass.AccessEvidence
            ? new RecoveryClassCoverage(item.DataClass, 1, ownerAccess.ByteLength)
            : item).ToArray();
        var finalManifest = sourceManifest with
        {
            Components = sourceManifest.Components.Concat([accessArtifactComponent]).ToArray(),
            Coverage = finalCoverage
        };
        var finalManifestBytes = finalManifest.SerializeCanonical();
        var finalManifestSha256 = RecoveryEvidenceCodec.Hash(finalManifestBytes);
        var finalManifestObject = await S3ArtifactWriter.PutBytesAsync(destinationS3, evidenceBucket,
            $"points/{pointId:D}/manifests/{finalManifestSha256}.json", finalManifestBytes, evidenceKmsKey, cancellationToken);
        await receiptJournal.AppendAsync(new(evidenceBucket, ownerAccess.ArtifactKey, ownerAccess.VersionId,
            ownerAccess.ArtifactSha256, ownerAccess.ByteLength, evidenceBucket, ownerAccess.ArtifactKey,
            ownerAccess.VersionId, RecoveryDataClass.AccessEvidence.ToString(), ownerAccess.VerifiedAtUtc), cancellationToken);
        var receiptSummary = await receiptJournal.CompleteAsync(cancellationToken);

        var checkpointPayload = new PrivateCopyCheckpointPayloadV1(1, sourceManifest.CaptureId, pointId,
            ownerId, libraryId, RecoveryEvidenceCodec.Hash(sourceManifestBytes), finalManifestObject.Sha256,
            sourceRegion, destinationRegion, destinationAccountId, DateTimeOffset.UtcNow,
            configuration.WorkerBuildDigest, receiptSummary.Count, receiptSummary.Chunks, sourceManifestReceipt,
            new PrivateArtifactReceiptV1(evidenceBucket, ownerAccess.ArtifactKey, ownerAccess.VersionId,
                evidenceBucket, ownerAccess.ArtifactKey, ownerAccess.VersionId, ownerAccess.ArtifactSha256, ownerAccess.ByteLength),
            new PrivateArtifactReceiptV1(evidenceBucket, finalManifestObject.Key, finalManifestObject.VersionId,
                evidenceBucket, finalManifestObject.Key, finalManifestObject.VersionId,
                finalManifestObject.Sha256, finalManifestObject.ByteLength));
        var checkpoint = await WriteSignedCheckpointAsync(destinationS3, evidenceBucket, evidenceKmsKey, signer,
            configuration.WorkerId, pointId, checkpointPayload, cancellationToken);

        await WriteCopyArtifactsToControlAsync(dynamo, configuration.Required("ControlTableName"),
            sourceManifest, sourceCaptureBucket, RecoveryEvidenceCodec.Hash(sourceManifestBytes), manifestKey,
            manifestVersionId, evidenceBucket, finalManifestObject.Key, finalManifestObject.VersionId,
            finalManifestObject.Sha256, checkpoint.Key, checkpoint.VersionId, checkpoint.Sha256, checkpoint.ByteLength,
            sourceAccountId, sourceRegion, destinationAccountId, destinationRegion, leaseToken, cancellationToken);
        Console.WriteLine($"recovery_copy_complete point={pointId:D} owner={ScopeHash(ownerId, libraryId)} objects={sourceManifest.Objects.Count}");
        return true;
    }

    private static async Task<OwnerAccessProof?> ReadOwnerAccessProofAsync(
        RecoveryWorkerConfiguration configuration,
        IAmazonDynamoDB dynamo,
        IAmazonS3 destinationS3,
        string expectedEvidenceBucket,
        string ownerId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = configuration.Required("ControlTableName"),
            Key = ControlKey("OWNERACCESS#" + ScopeHash(ownerId, libraryId), "PROOF"),
            ConsistentRead = true
        }, cancellationToken);

        string bucket;
        string objectKey;
        string versionId;
        string sha256;
        if (response.Item is { Count: > 0 })
        {
            bucket = GetString(response.Item, "bucket");
            objectKey = GetString(response.Item, "key");
            versionId = GetString(response.Item, "versionId");
            sha256 = GetString(response.Item, "sha256");
        }
        else if (configuration.Optional("OwnerAccessProof__Bucket") is { } bootstrapBucket &&
                 configuration.Optional("OwnerAccessProof__Key") is { } bootstrapKey &&
                 configuration.Optional("OwnerAccessProof__VersionId") is { } bootstrapVersion &&
                 configuration.Optional("OwnerAccessProof__Sha256") is { } bootstrapSha &&
                 configuration.Optional("OwnerId") == ownerId && configuration.Optional("LibraryId") == libraryId)
        {
            bucket = bootstrapBucket;
            objectKey = bootstrapKey;
            versionId = bootstrapVersion;
            sha256 = bootstrapSha;
        }
        else return null;

        if (bucket != expectedEvidenceBucket || string.IsNullOrWhiteSpace(objectKey) ||
            string.IsNullOrWhiteSpace(versionId) || !IsHash(sha256))
            throw new RecoveryWorkerException("owner_access_proof_pointer_invalid");
        var bytes = await ReadBytesAsync(destinationS3, bucket, objectKey, versionId, sha256, 64 * 1024, cancellationToken);
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
        var verified = RecoveryOwnerAccessCodec.Verify(envelope, trust, expected, DateTimeOffset.UtcNow);
        return new(verified.Payload.VerifiedAtUtc, objectKey, versionId, sha256, bytes.LongLength,
            verified.Payload.TranscriptSha256);
    }

    internal static void ValidateBuildBinding(RecoveryWorkerConfiguration configuration,
        RecoveryPointManifestV1 manifest, CaptureReleaseBindingV1 release, CaptureConfigurationV1 sourceConfiguration)
    {
        var expectedRevision = configuration.Required("SourceRevision");
        var migrationDigest = configuration.RequiredHash("MigrationDigest");
        if (sourceConfiguration.SourceRevision != expectedRevision || sourceConfiguration.MigrationDigest != migrationDigest ||
            sourceConfiguration.ConfigurationDigest != configuration.RequiredHash("ConfigurationDigest") ||
            sourceConfiguration.DatabaseRootCertificateSha256 != configuration.RequiredHash("DatabaseRootCertificateSha256") ||
            manifest.MigrationDigest != migrationDigest || release.SourceRevision != expectedRevision ||
            release.ApiBuildDigest != configuration.RequiredHash("ApiBuildDigest") ||
            release.IngestWorkerBuildDigest != configuration.RequiredHash("IngestWorkerBuildDigest") ||
            release.RecoveryWorkerBuildDigest != configuration.WorkerBuildDigest ||
            release.RendererBuildDigest != configuration.RequiredHash("RendererBuildDigest"))
            throw new RecoveryWorkerException("release_binding_mismatch");
    }

    private static async Task<T> ReadComponentAsync<T>(IAmazonS3 sourceS3, string captureBucket,
        RecoveryPointManifestV1 manifest, string name, CancellationToken cancellationToken)
    {
        var component = manifest.Components.SingleOrDefault(item => item.Name == name)
            ?? throw new RecoveryWorkerException("capture_component_missing");
        var (bucket, key) = ParseS3Uri(component.SourceKey);
        if (bucket != captureBucket) throw new RecoveryWorkerException("capture_component_bucket_mismatch");
        var bytes = await ReadBytesAsync(sourceS3, bucket, key, component.SourceVersionId,
            component.Sha256, checked((int)Math.Min(component.ByteLength, 4 * 1024 * 1024)), cancellationToken);
        if (bytes.LongLength != component.ByteLength) throw new RecoveryWorkerException("capture_component_length_mismatch");
        return JsonSerializer.Deserialize<T>(bytes, ArtifactJson) ?? throw new RecoveryWorkerException("capture_component_invalid");
    }

    private static async Task<PrivateCheckpointReceiptV1> WriteSignedCheckpointAsync(
        IAmazonS3 destinationS3,
        string evidenceBucket,
        string kmsKey,
        AwsKmsEvidenceSigner signer,
        string workerId,
        Guid pointId,
        PrivateCopyCheckpointPayloadV1 payload,
        CancellationToken cancellationToken)
    {
        var payloadBytes = RecoveryEvidenceCodec.SerializeCanonical(payload);
        var digest = RecoveryEvidenceCodec.Hash(payloadBytes);
        var signature = await signer.SignDigestAsync(SHA256.HashData(payloadBytes), cancellationToken);
        var envelope = new PrivateSignedCheckpointV1(1, "recovery_copy_checkpoint", workerId,
            signer.KeyId, signer.PublicKeySha256, digest, Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signature));
        var bytes = RecoveryEvidenceCodec.SerializeCanonical(envelope);
        var result = await S3ArtifactWriter.PutBytesAsync(destinationS3, evidenceBucket,
            $"points/{pointId:D}/checkpoints/{RecoveryEvidenceCodec.Hash(payloadBytes)}.json", bytes, kmsKey, cancellationToken);
        return new(result.Key, result.VersionId, result.Sha256, result.ByteLength, digest);
    }

    private static async Task PublishCycleAsync(IAmazonSQS sqs, RecoveryWorkerConfiguration configuration,
        AwsKmsEvidenceSigner signer, RecoveryPointManifestV1 manifest, string safeCode,
        CancellationToken cancellationToken)
    {
        var payload = new RecoveryCycleEventV1(1, Guid.NewGuid(), manifest.CaptureId,
            manifest.RecoveryPointId, manifest.OwnerId, manifest.LibraryId, DateTimeOffset.UtcNow,
            "publication", "recovery_records", "incomplete", safeCode, configuration.WorkerBuildDigest);
        var canonical = RecoveryCycleCodec.SerializePayload(payload);
        var signature = new RecoveryEvidenceSignature(signer.KeyId, signer.PublicKeySha256,
            await signer.SignDigestAsync(SHA256.HashData(canonical), cancellationToken));
        var envelope = RecoveryCycleCodec.CreateEnvelope(payload, configuration.WorkerId, signature);
        var bytes = RecoveryCycleCodec.SerializeEnvelope(envelope);
        if (bytes.Length > 64 * 1024) throw new RecoveryWorkerException("cycle_message_size_limit");
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = configuration.Required("ObservationQueueUrl"),
            MessageBody = Encoding.UTF8.GetString(bytes)
        }, cancellationToken);
    }

    private static async Task WriteCopyArtifactsToControlAsync(IAmazonDynamoDB dynamo, string table,
        RecoveryPointManifestV1 manifest, string sourceCaptureBucket, string sourceManifestSha256,
        string sourceManifestKey, string sourceManifestVersion, string manifestBucket, string manifestKey,
        string manifestVersion, string manifestSha256, string checkpointKey, string checkpointVersion,
        string checkpointSha256, long checkpointByteLength, string sourceAccountId, string sourceRegion, string recoveryAccountId,
        string recoveryRegion, string leaseToken, CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = ControlKey("POINT#" + manifest.RecoveryPointId.ToString("D"), "COPY"),
            UpdateExpression = "SET ownerId=:owner, libraryId=:library, captureId=:capture, sourceAccountId=:sourceAccount, sourceRegion=:sourceRegion, recoveryAccountId=:recoveryAccount, recoveryRegion=:recoveryRegion, sourceManifestSha256=:sourceSha, sourceManifestBucket=:sourceBucket, sourceManifestKey=:sourceKey, sourceManifestVersionId=:sourceVersion, evidenceBucket=:evidenceBucket, manifestBucket=:manifestBucket, manifestKey=:manifestKey, manifestVersionId=:manifestVersion, manifestSha256=:manifestSha, checkpointKey=:checkpointKey, checkpointVersionId=:checkpointVersion, checkpointSha256=:checkpointSha, checkpointByteLength=:checkpointLength, copiedAtUtc=:copied",
            ConditionExpression = "#state=:copying AND leaseToken=:lease",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":copying"] = S("copying"),
                [":lease"] = S(leaseToken),
                [":owner"] = S(manifest.OwnerId),
                [":library"] = S(manifest.LibraryId),
                [":capture"] = S(manifest.CaptureId.ToString("D")),
                [":sourceAccount"] = S(sourceAccountId),
                [":sourceRegion"] = S(sourceRegion),
                [":recoveryAccount"] = S(recoveryAccountId),
                [":recoveryRegion"] = S(recoveryRegion),
                [":sourceSha"] = S(sourceManifestSha256),
                [":sourceBucket"] = S(sourceCaptureBucket),
                [":sourceKey"] = S(sourceManifestKey),
                [":sourceVersion"] = S(sourceManifestVersion),
                [":evidenceBucket"] = S(manifestBucket),
                [":manifestBucket"] = S(manifestBucket),
                [":manifestKey"] = S(manifestKey),
                [":manifestVersion"] = S(manifestVersion),
                [":manifestSha"] = S(manifestSha256),
                [":checkpointKey"] = S(checkpointKey),
                [":checkpointVersion"] = S(checkpointVersion),
                [":checkpointSha"] = S(checkpointSha256),
                [":checkpointLength"] = N(checkpointByteLength),
                [":copied"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        }, cancellationToken);
    }

    private static async Task WriteDeferredCheckpointToControlAsync(IAmazonDynamoDB dynamo, string table,
        RecoveryPointManifestV1 manifest, string sourceCaptureBucket, string sourceManifestSha256,
        string sourceManifestKey, string sourceManifestVersion, string evidenceBucket,
        string checkpointKey, string checkpointVersion, string checkpointSha256, long checkpointByteLength,
        string sourceAccountId, string sourceRegion, string recoveryAccountId, string recoveryRegion,
        string leaseToken, CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = ControlKey("POINT#" + manifest.RecoveryPointId.ToString("D"), "COPY"),
            UpdateExpression = "SET ownerId=:owner, libraryId=:library, captureId=:capture, sourceAccountId=:sourceAccount, sourceRegion=:sourceRegion, recoveryAccountId=:recoveryAccount, recoveryRegion=:recoveryRegion, sourceManifestSha256=:sourceSha, sourceManifestBucket=:sourceBucket, sourceManifestKey=:sourceKey, sourceManifestVersionId=:sourceVersion, evidenceBucket=:evidenceBucket, checkpointKey=:checkpointKey, checkpointVersionId=:checkpointVersion, checkpointSha256=:checkpointSha, checkpointByteLength=:checkpointLength, updatedAtUtc=:updated",
            ConditionExpression = "#state=:copying AND leaseToken=:lease",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":copying"] = S("copying"),
                [":lease"] = S(leaseToken),
                [":owner"] = S(manifest.OwnerId),
                [":library"] = S(manifest.LibraryId),
                [":capture"] = S(manifest.CaptureId.ToString("D")),
                [":sourceAccount"] = S(sourceAccountId),
                [":sourceRegion"] = S(sourceRegion),
                [":recoveryAccount"] = S(recoveryAccountId),
                [":recoveryRegion"] = S(recoveryRegion),
                [":sourceSha"] = S(sourceManifestSha256),
                [":sourceBucket"] = S(sourceCaptureBucket),
                [":sourceKey"] = S(sourceManifestKey),
                [":sourceVersion"] = S(sourceManifestVersion),
                [":evidenceBucket"] = S(evidenceBucket),
                [":checkpointKey"] = S(checkpointKey),
                [":checkpointVersion"] = S(checkpointVersion),
                [":checkpointSha"] = S(checkpointSha256),
                [":checkpointLength"] = N(checkpointByteLength),
                [":updated"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        }, cancellationToken);
    }

    private static async Task<string?> TryAcquirePointAsync(IAmazonDynamoDB dynamo, string table,
        Guid captureId, RecoveryPointManifestV1 manifest, string sourceManifestSha256, string workerId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseToken = Guid.NewGuid().ToString("N");
        var itemKey = ControlKey("POINT#" + manifest.RecoveryPointId.ToString("D"), "COPY");
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = itemKey,
                UpdateExpression = "SET #state=:copying, captureId=:capture, ownerId=:owner, libraryId=:library, sourceManifestSha256=:manifest, workerId=:worker, leaseToken=:token, leaseUntilUtc=:lease",
                ConditionExpression = "attribute_not_exists(pk) OR ((attribute_not_exists(#state) OR (#state <> :copied AND (#state <> :copying OR attribute_not_exists(leaseUntilUtc) OR leaseUntilUtc < :now))) AND (attribute_not_exists(sourceManifestSha256) OR sourceManifestSha256=:manifest))",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":copying"] = S("copying"),
                    [":copied"] = S("copied"),
                    [":capture"] = S(captureId.ToString("D")),
                    [":owner"] = S(manifest.OwnerId),
                    [":library"] = S(manifest.LibraryId),
                    [":worker"] = S(workerId),
                    [":manifest"] = S(sourceManifestSha256),
                    [":token"] = S(leaseToken),
                    [":lease"] = S(now.Add(TimeSpan.Parse(LeaseDuration, CultureInfo.InvariantCulture)).ToString("O", CultureInfo.InvariantCulture)),
                    [":now"] = S(now.ToString("O", CultureInfo.InvariantCulture))
                }
            }, cancellationToken);
            return leaseToken;
        }
        catch (ConditionalCheckFailedException)
        {
            var existing = await dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = itemKey,
                ConsistentRead = true
            }, cancellationToken);
            if (existing.Item is { Count: > 0 } item)
            {
                if (GetString(item, "sourceManifestSha256") != sourceManifestSha256)
                    throw new RecoveryWorkerException("recovery_point_id_conflict");
                var state = GetString(item, "state");
                if (state is "copied" or "copying") return null;
            }
            return null;
        }
    }

    private static async Task CompletePointAsync(IAmazonDynamoDB dynamo, string table,
        RecoveryPointManifestV1 manifest, string leaseToken, CancellationToken cancellationToken)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = ControlKey("POINT#" + manifest.RecoveryPointId.ToString("D"), "COPY"),
            UpdateExpression = "SET #state=:copied, completedAtUtc=:now REMOVE leaseUntilUtc",
            ConditionExpression = "#state=:copying AND leaseToken=:token",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":copied"] = S("copied"),
                [":copying"] = S("copying"),
                [":token"] = S(leaseToken),
                [":now"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            }
        }, cancellationToken);
    }

    private static async Task DeferPointAsync(IAmazonDynamoDB dynamo, string table,
        RecoveryPointManifestV1 manifest, string leaseToken, CancellationToken cancellationToken)
    {
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = ControlKey("POINT#" + manifest.RecoveryPointId.ToString("D"), "COPY"),
                UpdateExpression = "SET #state=:deferred, updatedAtUtc=:now REMOVE leaseUntilUtc",
                ConditionExpression = "#state=:copying AND leaseToken=:token",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":deferred"] = S("access_incomplete"),
                    [":copying"] = S("copying"),
                    [":token"] = S(leaseToken),
                    [":now"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException) { }
    }

    private static async Task EmitVerifiedAgeAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration, IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var age = scopes.Count == 0 ? 86_400d * 365 : 0d;
        foreach (var scope in scopes)
        {
            var latest = await dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = configuration.Required("ControlTableName"),
                Key = ControlKey("OWNER#" + scope, "VERIFIED"),
                ConsistentRead = true
            }, cancellationToken);
            if (latest.Item is null || !DateTimeOffset.TryParse(GetString(latest.Item, "verifiedAtUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var verified))
            {
                age = Math.Max(age, 86_400d * 365);
                continue;
            }
            age = Math.Max(age, (now - verified.ToUniversalTime()).TotalSeconds);
        }
        RecoveryWorkerMetrics.Emit("VerifiedPointAgeSeconds", Math.Max(0, age), configuration);
    }

    private static async Task CheckDestinationBucketAsync(IAmazonS3 s3, string bucket, string kmsKey,
        string expectedRegion, CancellationToken cancellationToken)
    {
        var location = await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }, cancellationToken);
        var actualRegion = string.IsNullOrWhiteSpace(location.Location.Value) ? "us-east-1" : location.Location.Value;
        if (actualRegion != expectedRegion) throw new RecoveryWorkerException("destination_bucket_region_mismatch");
        var encryption = await s3.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket }, cancellationToken);
        if (encryption.ServerSideEncryptionConfiguration?.ServerSideEncryptionRules is not { Count: > 0 } rules ||
            !rules.Any(rule => rule.ServerSideEncryptionByDefault?.ServerSideEncryptionAlgorithm == ServerSideEncryptionMethod.AWSKMS &&
                (rule.ServerSideEncryptionByDefault.ServerSideEncryptionKeyManagementServiceKeyId == kmsKey ||
                 rule.ServerSideEncryptionByDefault.ServerSideEncryptionKeyManagementServiceKeyId?.EndsWith(kmsKey[(kmsKey.LastIndexOf('/') + 1)..], StringComparison.Ordinal) == true)))
            throw new RecoveryWorkerException("destination_bucket_encryption_mismatch");
        var policyStatus = await s3.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = bucket }, cancellationToken);
        if (policyStatus.PolicyStatus?.IsPublic == true) throw new RecoveryWorkerException("destination_bucket_public_policy");
        var ownership = await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket }, cancellationToken);
        if (ownership.OwnershipControls?.Rules is not { Count: > 0 } ownershipRules ||
            ownershipRules.All(rule => rule.ObjectOwnership != ObjectOwnership.BucketOwnerEnforced))
            throw new RecoveryWorkerException("destination_bucket_ownership_unverified");
    }

    private static Platform.Recovery.RecoveryObjectStore CreateStore(IAmazonS3 source, IAmazonS3 destination,
        string sourceBucket, string destinationBucket, string kmsKey, RecoveryWorkerConfiguration configuration) => new(
        source, destination, sourceBucket, destinationBucket, configuration.Required("DestinationAccountAlias"),
        configuration.RequiredRegion("DestinationRegion"), kmsKey,
        configuration.WorkerBuildDigest);

    private static async Task<IReadOnlyList<string>> ListCompletedCaptureMarkersAsync(IAmazonS3 s3,
        string bucket, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "captures/",
                ContinuationToken = token
            }, cancellationToken);
            keys.AddRange(page.S3Objects.Where(item => item.Key.EndsWith("/capture-complete.json", StringComparison.Ordinal))
                .Select(item => item.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<T> ReadJsonAsync<T>(IAmazonS3 s3, string bucket, string key,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(s3, bucket, key, null, null, 1024 * 1024, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, ArtifactJson) ?? throw new RecoveryWorkerException("capture_artifact_invalid");
    }

    private static async Task<byte[]> ReadBytesAsync(IAmazonS3 s3, string bucket, string key,
        string? versionId, string? expectedSha256, int? maxBytes, CancellationToken cancellationToken)
    {
        var read = await ReadVersionedBytesAsync(s3, bucket, key, versionId, expectedSha256, maxBytes, cancellationToken);
        return read.Bytes;
    }

    private static async Task<VersionedBytes> ReadVersionedBytesAsync(IAmazonS3 s3, string bucket, string key,
        string? versionId, string? expectedSha256, int? maxBytes, CancellationToken cancellationToken)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, cancellationToken);
        using (response)
        {
            if (versionId is not null && response.VersionId != versionId)
                throw new RecoveryWorkerException("capture_artifact_version_mismatch");
            if (string.IsNullOrWhiteSpace(response.VersionId) || response.VersionId == "null")
                throw new RecoveryWorkerException("capture_artifact_version_missing");
            if (maxBytes is { } limit && response.ContentLength > limit)
                throw new RecoveryWorkerException("capture_artifact_size_limit");
            using var output = response.ContentLength is >= 0 and <= int.MaxValue
                ? new MemoryStream((int)response.ContentLength)
                : new MemoryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (maxBytes is { } max && output.Length + read > max)
                    throw new RecoveryWorkerException("capture_artifact_size_limit");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && actual != expectedSha256)
                throw new RecoveryWorkerException("capture_artifact_checksum_mismatch");
            if (response.ContentLength != output.Length)
                throw new RecoveryWorkerException("capture_artifact_length_mismatch");
            return new VersionedBytes(output.ToArray(), response.VersionId);
        }
    }

    private static (string Bucket, string Key) ParseS3Uri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "s3" ||
            string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.AbsolutePath))
            throw new RecoveryWorkerException("capture_component_uri_invalid");
        return (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }

    private static Dictionary<string, AttributeValue> ControlKey(string pk, string sk) => new()
    {
        ["pk"] = S(pk),
        ["sk"] = S(sk)
    };

    private static string GetString(Dictionary<string, AttributeValue> item, string key) =>
        item.TryGetValue(key, out var value) ? value.S ?? "" : "";
    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static string ScopeHash(string ownerId, string libraryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId + "\0" + libraryId))).ToLowerInvariant();
    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private sealed record CaptureManifestPointerV1(string Key, string VersionId, string Sha256, long ByteLength);
    private sealed record CaptureCompleteV1(int SchemaVersion, Guid CaptureId, DateTimeOffset CompletedAtUtc,
        int ScopeCount, IReadOnlyList<CaptureManifestPointerV1> ManifestObjects);
    private sealed record PlannedCopy(string SourceBucket, RecoveryObjectReference Source,
        string DestinationBucket, string DestinationKey, bool IsMedia, IReadOnlyList<LogicalWriteAdmission> Admissions);
    private sealed record AdmittedPlan(PlannedCopy Plan, IReadOnlyList<LogicalWriteAdmission> Admissions);
    private sealed record OwnerAccessProof(DateTimeOffset VerifiedAtUtc, string ArtifactKey,
        string VersionId, string ArtifactSha256, long ByteLength, string TranscriptSha256);
    private sealed record VersionedBytes(byte[] Bytes, string VersionId);
}
