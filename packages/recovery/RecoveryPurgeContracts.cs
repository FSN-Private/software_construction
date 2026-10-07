using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

public sealed record RecoveryPurgeProposalV1(
    int SchemaVersion,
    Guid OperationId,
    Guid EventId,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long ExpectedLifecycleVersion,
    long Generation,
    DateTimeOffset ProposedAtUtc,
    DateTimeOffset EligibleAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PlanDigest)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static RecoveryPurgeProposalV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgeProposalV1 proposal;
        try
        {
            proposal = JsonSerializer.Deserialize<RecoveryPurgeProposalV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_proposal_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_proposal_invalid_json", exception);
        }
        proposal.Validate(proposal.ProposedAtUtc);
        if (!RecoveryEvidenceCodec.SerializeCanonical(proposal).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_proposal_noncanonical");
        return proposal;
    }

    public void Validate(DateTimeOffset? nowUtc = null)
    {
        if (SchemaVersion != CurrentSchemaVersion || OperationId == Guid.Empty || EventId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty)
            throw new InvalidDataException("purge_proposal_identity_invalid");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (Category is not ("original" or "xmp") || Category == "xmp" && EditId is null ||
            Category == "original" && EditId is not null)
            throw new InvalidDataException("purge_proposal_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        if (ExpectedLifecycleVersion <= 0 || Generation <= 0)
            throw new InvalidDataException("purge_proposal_generation_invalid");
        RecoveryValidation.Utc(ProposedAtUtc, nameof(ProposedAtUtc));
        RecoveryValidation.Utc(EligibleAtUtc, nameof(EligibleAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (EligibleAtUtc < ProposedAtUtc || ExpiresAtUtc <= ProposedAtUtc ||
            ExpiresAtUtc > ProposedAtUtc.AddDays(7))
            throw new InvalidDataException("purge_proposal_timeline_invalid");
        RecoveryValidation.Hash(PlanDigest, nameof(PlanDigest));
        if (!string.Equals(PlanDigest, ComputePlanDigest(this), StringComparison.Ordinal))
            throw new InvalidDataException("purge_proposal_digest_mismatch");
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        RecoveryValidation.Utc(now, nameof(nowUtc));
        if (now < ProposedAtUtc || now >= ExpiresAtUtc)
            throw new InvalidDataException("purge_proposal_expired");
    }

    public static RecoveryPurgeProposalV1 Create(
        Guid operationId,
        Guid eventId,
        string ownerId,
        string libraryId,
        Guid assetId,
        Guid? editId,
        string category,
        string contentSha256,
        long expectedLifecycleVersion,
        long generation,
        DateTimeOffset proposedAtUtc,
        DateTimeOffset eligibleAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var proposal = new RecoveryPurgeProposalV1(CurrentSchemaVersion, operationId, eventId, ownerId, libraryId,
            assetId, editId, category, contentSha256, expectedLifecycleVersion, generation, proposedAtUtc,
            eligibleAtUtc, expiresAtUtc, new string('0', 64));
        return proposal with { PlanDigest = ComputePlanDigest(proposal) };
    }

    public static string ComputePlanDigest(RecoveryPurgeProposalV1 proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var payload = new PurgeProposalDigestV1(proposal.SchemaVersion, proposal.OperationId, proposal.EventId,
            proposal.OwnerId, proposal.LibraryId, proposal.AssetId, proposal.EditId, proposal.Category,
            proposal.ContentSha256, proposal.ExpectedLifecycleVersion, proposal.Generation,
            proposal.ProposedAtUtc, proposal.EligibleAtUtc, proposal.ExpiresAtUtc);
        return RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(payload));
    }

    private sealed record PurgeProposalDigestV1(
        int SchemaVersion,
        Guid OperationId,
        Guid EventId,
        string OwnerId,
        string LibraryId,
        Guid AssetId,
        Guid? EditId,
        string Category,
        string ContentSha256,
        long ExpectedLifecycleVersion,
        long Generation,
        DateTimeOffset ProposedAtUtc,
        DateTimeOffset EligibleAtUtc,
        DateTimeOffset ExpiresAtUtc);
}

/// <summary>Private operator artifact: the digest covers all exact copied versions, including affected snapshots.</summary>
public sealed record RecoveryPurgePlanV1(
    int SchemaVersion,
    Guid OperationId,
    Guid EventId,
    string ProposalDigest,
    string FreezeAcknowledgementSha256,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long Generation,
    long SnapshotFenceGeneration,
    long OwnerPointCount,
    string OwnerPointSetSha256,
    string AffectedPointSetSha256,
    string SettledWriterIntentSetSha256,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    RecoveryRetentionHistoryProofV1 RetentionHistory,
    IReadOnlyList<RecoveryPurgeTargetVersionV1> TargetVersions)
{
    public const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public RecoveryPurgePlanV1 Canonicalized() => this with
    {
        TargetVersions = TargetVersions
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.PointId)
            .ThenBy(item => item.Bucket, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.VersionId, StringComparer.Ordinal)
            .ToArray()
    };

    public void Validate(DateTimeOffset? nowUtc = null)
    {
        if (SchemaVersion != CurrentSchemaVersion || OperationId == Guid.Empty || EventId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty)
            throw new InvalidDataException("purge_plan_identity_invalid");
        RecoveryValidation.Hash(ProposalDigest, nameof(ProposalDigest));
        RecoveryValidation.Hash(FreezeAcknowledgementSha256, nameof(FreezeAcknowledgementSha256));
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (Category is not ("original" or "xmp") || Category == "xmp" && EditId is null ||
            Category == "original" && EditId is not null)
            throw new InvalidDataException("purge_plan_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        if (Generation <= 0 || SnapshotFenceGeneration <= 0 || OwnerPointCount <= 0 ||
            TargetVersions is null || TargetVersions.Count == 0)
            throw new InvalidDataException("purge_plan_targets_required");
        RecoveryValidation.Hash(OwnerPointSetSha256, nameof(OwnerPointSetSha256));
        RecoveryValidation.Hash(AffectedPointSetSha256, nameof(AffectedPointSetSha256));
        RecoveryValidation.Hash(SettledWriterIntentSetSha256, nameof(SettledWriterIntentSetSha256));
        RecoveryValidation.Utc(PreparedAtUtc, nameof(PreparedAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (RetentionHistory is null) throw new InvalidDataException("purge_retention_history_required");
        RetentionService.ValidateHistoryProof(RetentionHistory, OwnerId, LibraryId, AssetId, EditId,
            Category, ContentSha256, OwnerPointCount, OwnerPointSetSha256);
        if (ExpiresAtUtc <= PreparedAtUtc || ExpiresAtUtc > PreparedAtUtc.AddHours(1))
            throw new InvalidDataException("purge_plan_expiry_invalid");
        foreach (var target in TargetVersions)
        {
            if (target is null) throw new InvalidDataException("purge_plan_target_invalid");
            target.Validate();
        }
        if (TargetVersions.GroupBy(item => (item.PointId, item.Bucket, item.Key, item.VersionId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("purge_plan_duplicate_target");
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        RecoveryValidation.Utc(now, nameof(nowUtc));
        if (now < PreparedAtUtc || now >= ExpiresAtUtc)
            throw new InvalidDataException("purge_plan_expired");
    }

    public byte[] SerializeCanonical()
    {
        Validate(PreparedAtUtc);
        return RecoveryEvidenceCodec.SerializeCanonical(Canonicalized());
    }

    public string Digest() => RecoveryEvidenceCodec.Hash(SerializeCanonical());

    public static RecoveryPurgePlanV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgePlanV1 plan;
        try
        {
            plan = JsonSerializer.Deserialize<RecoveryPurgePlanV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_plan_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_plan_invalid_json", exception);
        }
        plan.Validate(plan.PreparedAtUtc);
        var canonical = RecoveryEvidenceCodec.SerializeCanonical(plan.Canonicalized());
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_plan_noncanonical");
        return plan.Canonicalized();
    }
}

public sealed record RecoveryPurgeTargetVersionV1(
    string Kind,
    Guid PointId,
    string Bucket,
    string Key,
    string VersionId,
    string Sha256,
    long ByteLength,
    DateTimeOffset EligibleAtUtc,
    bool RetainedForSharedIdentity)
{
    public void Validate()
    {
        if (Kind is not ("media" or "database_snapshot") || PointId == Guid.Empty)
            throw new InvalidDataException("purge_target_kind_invalid");
        RecoveryValidation.Text(Bucket, nameof(Bucket), 255);
        RecoveryValidation.Text(Key, nameof(Key), 1024);
        RecoveryValidation.Text(VersionId, nameof(VersionId), 1024);
        if (VersionId == "null") throw new InvalidDataException("purge_target_version_invalid");
        RecoveryValidation.Hash(Sha256, nameof(Sha256));
        if (ByteLength < 0) throw new InvalidDataException("purge_target_length_invalid");
        RecoveryValidation.Utc(EligibleAtUtc, nameof(EligibleAtUtc));
    }
}

public sealed record RecoveryPurgeApprovalV1(
    int SchemaVersion,
    Guid ApprovalId,
    Guid OperationId,
    string ProposalDigest,
    string PurgePlanDigest,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long Generation,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || ApprovalId == Guid.Empty || OperationId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty || Generation <= 0)
            throw new InvalidDataException("purge_approval_identity_invalid");
        RecoveryValidation.Hash(ProposalDigest, nameof(ProposalDigest));
        RecoveryValidation.Hash(PurgePlanDigest, nameof(PurgePlanDigest));
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(ApprovedBy, nameof(ApprovedBy), 200);
        if (Category is not ("original" or "xmp") || Category == "xmp" && EditId is null ||
            Category == "original" && EditId is not null)
            throw new InvalidDataException("purge_approval_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        RecoveryValidation.Utc(ApprovedAtUtc, nameof(ApprovedAtUtc));
        RecoveryValidation.Utc(ExpiresAtUtc, nameof(ExpiresAtUtc));
        if (ExpiresAtUtc <= ApprovedAtUtc || ExpiresAtUtc > ApprovedAtUtc.AddMinutes(15))
            throw new InvalidDataException("purge_approval_expiry_invalid");
    }
}

public sealed record RecoveryPurgeApprovalEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    RecoveryPurgeApprovalV1 Payload,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_purge_approval" || Payload is null)
            throw new InvalidDataException("purge_approval_envelope_invalid");
        Payload.Validate();
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("purge_approval_signature_encoding_invalid", exception);
        }
    }
}

public sealed record RecoveryPurgeExecutionReceiptV1(
    int SchemaVersion,
    Guid OperationId,
    Guid EventId,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long Generation,
    long SnapshotFenceGeneration,
    string ProposalDigest,
    string PurgePlanDigest,
    string OwnerApprovalPayloadSha256,
    string AffectedPointSetSha256,
    long DeletedVersionCount,
    long DeletedByteLength,
    long RetainedSharedVersionCount,
    string SettledWriterIntentSetSha256,
    DateTimeOffset ExecutedAtUtc,
    string RecoveryWorkerBuildDigest)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || OperationId == Guid.Empty || EventId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty || Generation <= 0 || SnapshotFenceGeneration <= 0 ||
            DeletedVersionCount < 0 || DeletedByteLength < 0 || RetainedSharedVersionCount < 0)
            throw new InvalidDataException("purge_execution_receipt_invalid");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (Category is not ("original" or "xmp") || Category == "xmp" && EditId is null ||
            Category == "original" && EditId is not null)
            throw new InvalidDataException("purge_execution_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        RecoveryValidation.Hash(ProposalDigest, nameof(ProposalDigest));
        RecoveryValidation.Hash(PurgePlanDigest, nameof(PurgePlanDigest));
        RecoveryValidation.Hash(OwnerApprovalPayloadSha256, nameof(OwnerApprovalPayloadSha256));
        RecoveryValidation.Hash(AffectedPointSetSha256, nameof(AffectedPointSetSha256));
        RecoveryValidation.Hash(SettledWriterIntentSetSha256, nameof(SettledWriterIntentSetSha256));
        RecoveryValidation.Utc(ExecutedAtUtc, nameof(ExecutedAtUtc));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
    }
}

public sealed record RecoveryPurgeExecutionEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_purge_execution" ||
            string.IsNullOrWhiteSpace(WorkerId))
            throw new InvalidDataException("purge_execution_envelope_invalid");
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("purge_execution_signature_encoding_invalid", exception);
        }
    }
}

public sealed record VerifiedRecoveryPurgeExecution(
    RecoveryPurgeExecutionReceiptV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string WorkerId,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryPurgeExecutionCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static byte[] SerializeEnvelope(RecoveryPurgeExecutionEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryPurgeExecutionEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgeExecutionEnvelopeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RecoveryPurgeExecutionEnvelopeV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_execution_envelope_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_execution_envelope_invalid_json", exception);
        }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_execution_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryPurgeExecution Verify(
        RecoveryPurgeExecutionEnvelopeV1 envelope,
        RecoverySigningTrust recoveryEvidenceTrust)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recoveryEvidenceTrust);
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        RecoveryPurgeExecutionReceiptV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryPurgeExecutionReceiptV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_execution_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_execution_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(payload).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_execution_payload_noncanonical");
        var digest = RecoveryEvidenceCodec.Hash(bytes);
        if (digest != envelope.PayloadSha256)
            throw new InvalidDataException("purge_execution_payload_digest_mismatch");
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, digest, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, recoveryEvidenceTrust);
        return new(payload, bytes, digest, envelope.WorkerId, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}

public sealed record RecoveryPurgeCompletionV1(
    int SchemaVersion,
    Guid OperationId,
    Guid EventId,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long LifecycleGeneration,
    long SnapshotFenceGeneration,
    string ProposalDigest,
    string PurgePlanDigest,
    string OwnerApprovalPayloadSha256,
    string RecoveryExecutionReceiptSha256,
    string PrimaryCleanupReceiptSha256,
    string AffectedPointSetSha256,
    long DeletedVersionCount,
    long DeletedByteLength,
    long RetainedSharedVersionCount,
    string SettledWriterIntentSetSha256,
    string ExportAcknowledgementSha256,
    string MediaFenceScopeId,
    string SnapshotFenceScopeId,
    DateTimeOffset SnapshotFenceReleasedAtUtc,
    string ReleasedBy,
    DateTimeOffset CompletedAtUtc,
    string RecoveryWorkerBuildDigest)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || OperationId == Guid.Empty || EventId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty || LifecycleGeneration <= 0 || SnapshotFenceGeneration <= 0 ||
            DeletedVersionCount < 0 || DeletedByteLength < 0 || RetainedSharedVersionCount < 0)
            throw new InvalidDataException("purge_completion_invalid");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(ReleasedBy, nameof(ReleasedBy), 200);
        if (Category is not ("original" or "xmp") || Category == "xmp" && EditId is null ||
            Category == "original" && EditId is not null)
            throw new InvalidDataException("purge_completion_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        RecoveryValidation.Hash(ProposalDigest, nameof(ProposalDigest));
        RecoveryValidation.Hash(PurgePlanDigest, nameof(PurgePlanDigest));
        RecoveryValidation.Hash(OwnerApprovalPayloadSha256, nameof(OwnerApprovalPayloadSha256));
        RecoveryValidation.Hash(RecoveryExecutionReceiptSha256, nameof(RecoveryExecutionReceiptSha256));
        RecoveryValidation.Hash(PrimaryCleanupReceiptSha256, nameof(PrimaryCleanupReceiptSha256));
        RecoveryValidation.Hash(AffectedPointSetSha256, nameof(AffectedPointSetSha256));
        RecoveryValidation.Hash(SettledWriterIntentSetSha256, nameof(SettledWriterIntentSetSha256));
        RecoveryValidation.Hash(ExportAcknowledgementSha256, nameof(ExportAcknowledgementSha256));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Text(MediaFenceScopeId, nameof(MediaFenceScopeId), 80);
        RecoveryValidation.Text(SnapshotFenceScopeId, nameof(SnapshotFenceScopeId), 80);
        if (!MediaFenceScopeId.StartsWith("MEDIA#", StringComparison.Ordinal) ||
            !SnapshotFenceScopeId.StartsWith("SNAPSHOT#", StringComparison.Ordinal))
            throw new InvalidDataException("purge_completion_fence_scope_invalid");
        RecoveryValidation.Utc(SnapshotFenceReleasedAtUtc, nameof(SnapshotFenceReleasedAtUtc));
        RecoveryValidation.Utc(CompletedAtUtc, nameof(CompletedAtUtc));
        if (SnapshotFenceReleasedAtUtc > CompletedAtUtc)
            throw new InvalidDataException("purge_completion_timeline_invalid");
    }
}

public sealed record RecoveryPurgeCompletionEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    string MessageType,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string PayloadSha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion || MessageType != "recovery_purge_completion")
            throw new InvalidDataException("purge_completion_envelope_invalid");
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("purge_completion_signature_encoding_invalid", exception);
        }
    }
}

public sealed record VerifiedRecoveryPurgeCompletion(
    RecoveryPurgeCompletionV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryPurgeCompletionCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static byte[] SerializeEnvelope(RecoveryPurgeCompletionEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryPurgeCompletionEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgeCompletionEnvelopeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RecoveryPurgeCompletionEnvelopeV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_completion_envelope_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_completion_envelope_invalid_json", exception);
        }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_completion_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryPurgeCompletion Verify(
        RecoveryPurgeCompletionEnvelopeV1 envelope,
        RecoverySigningTrust ownerApprovalTrust,
        RecoveryPurgeProposalV1 proposal,
        VerifiedRecoveryPurgeApproval approval,
        DateTimeOffset nowUtc)
    {
        envelope.Validate();
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(approval);
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        RecoveryPurgeCompletionV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryPurgeCompletionV1>(payloadBytes, JsonOptions)
                ?? throw new InvalidDataException("purge_completion_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_completion_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(payload).AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("purge_completion_payload_noncanonical");
        var digest = RecoveryEvidenceCodec.Hash(payloadBytes);
        if (digest != envelope.PayloadSha256)
            throw new InvalidDataException("purge_completion_payload_digest_mismatch");
        var signature = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, digest, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, ownerApprovalTrust);
        var approvalPayload = approval.Payload;
        var approvalDigest = approval.PayloadSha256;
        var mediaScope = RecoveryFenceScope.MediaScopeId(
            new RecoveryLogicalReference(proposal.OwnerId, proposal.LibraryId, proposal.AssetId, proposal.EditId),
            proposal.Category, proposal.ContentSha256);
        var snapshotScope = RecoveryFenceScope.SnapshotScopeId(proposal.OwnerId, proposal.LibraryId);
        if (payload.OperationId != proposal.OperationId || payload.EventId != proposal.EventId ||
            payload.OwnerId != proposal.OwnerId || payload.LibraryId != proposal.LibraryId ||
            payload.AssetId != proposal.AssetId || payload.EditId != proposal.EditId ||
            payload.Category != proposal.Category || payload.ContentSha256 != proposal.ContentSha256 ||
            payload.LifecycleGeneration != proposal.Generation || payload.ProposalDigest != proposal.PlanDigest ||
            payload.PurgePlanDigest != approvalPayload.PurgePlanDigest || payload.OwnerApprovalPayloadSha256 != approvalDigest ||
            payload.MediaFenceScopeId != mediaScope || payload.SnapshotFenceScopeId != snapshotScope ||
            payload.CompletedAtUtc > nowUtc || payload.SnapshotFenceReleasedAtUtc > nowUtc)
            throw new InvalidDataException("purge_completion_binding_mismatch");
        return new(payload, payloadBytes, digest, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}

public sealed record VerifiedRecoveryPurgeApproval(
    RecoveryPurgeApprovalV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryPurgeApprovalCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static byte[] SerializeEnvelope(RecoveryPurgeApprovalEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryPurgeApprovalEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgeApprovalEnvelopeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RecoveryPurgeApprovalEnvelopeV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_approval_envelope_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_approval_envelope_invalid_json", exception);
        }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_approval_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryPurgeApproval Verify(
        RecoveryPurgeApprovalEnvelopeV1 envelope,
        RecoverySigningTrust ownerApprovalTrust,
        RecoveryPurgePlanV1 plan,
        DateTimeOffset nowUtc)
    {
        envelope.Validate();
        plan.Validate(nowUtc);
        var payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        RecoveryPurgeApprovalV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryPurgeApprovalV1>(payloadBytes, JsonOptions)
                ?? throw new InvalidDataException("purge_approval_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_approval_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(payload).AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("purge_approval_payload_noncanonical");
        var digest = RecoveryEvidenceCodec.Hash(payloadBytes);
        if (digest != envelope.PayloadSha256)
            throw new InvalidDataException("purge_approval_payload_digest_mismatch");
        if (!RecoveryEvidenceCodec.SerializeCanonical(envelope.Payload).AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("purge_approval_envelope_payload_mismatch");
        var signature = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, digest, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, ownerApprovalTrust);
        if (payload.OperationId != plan.OperationId || payload.ProposalDigest != plan.ProposalDigest ||
            payload.PurgePlanDigest != plan.Digest() || payload.OwnerId != plan.OwnerId ||
            payload.LibraryId != plan.LibraryId || payload.AssetId != plan.AssetId || payload.EditId != plan.EditId ||
            payload.Category != plan.Category || payload.ContentSha256 != plan.ContentSha256 ||
            payload.Generation != plan.Generation || payload.ApprovedAtUtc > nowUtc || payload.ExpiresAtUtc <= nowUtc ||
            payload.ApprovedAtUtc < plan.PreparedAtUtc || payload.ExpiresAtUtc > plan.ExpiresAtUtc)
            throw new InvalidDataException("purge_approval_binding_mismatch");
        return new(payload, payloadBytes, digest, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }

    /// <summary>Verifies the owner-signed purge plan approval without receiving its private exact-version plan.</summary>
    public static VerifiedRecoveryPurgeApproval VerifyForCapture(
        RecoveryPurgeApprovalEnvelopeV1 envelope,
        RecoverySigningTrust ownerApprovalTrust,
        RecoveryPurgeProposalV1 proposal,
        string expectedPurgePlanDigest,
        DateTimeOffset nowUtc,
        bool allowExpiredForCompletedPurge = false)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(ownerApprovalTrust);
        ArgumentNullException.ThrowIfNull(proposal);
        envelope.Validate();
        proposal.Validate(proposal.ProposedAtUtc);
        RecoveryValidation.Hash(expectedPurgePlanDigest, nameof(expectedPurgePlanDigest));
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var payloadBytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        RecoveryPurgeApprovalV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryPurgeApprovalV1>(payloadBytes, JsonOptions)
                ?? throw new InvalidDataException("purge_approval_payload_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_approval_payload_invalid_json", exception);
        }
        payload.Validate();
        if (!RecoveryEvidenceCodec.SerializeCanonical(payload).AsSpan().SequenceEqual(payloadBytes) ||
            !RecoveryEvidenceCodec.SerializeCanonical(envelope.Payload).AsSpan().SequenceEqual(payloadBytes))
            throw new InvalidDataException("purge_approval_payload_noncanonical");
        var digest = RecoveryEvidenceCodec.Hash(payloadBytes);
        if (digest != envelope.PayloadSha256)
            throw new InvalidDataException("purge_approval_payload_digest_mismatch");
        var signature = RecoveryEvidenceCodec.VerifyDetached(payloadBytes, digest, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, ownerApprovalTrust);
        if (payload.OperationId != proposal.OperationId || payload.ProposalDigest != proposal.PlanDigest ||
            payload.PurgePlanDigest != expectedPurgePlanDigest || payload.OwnerId != proposal.OwnerId ||
            payload.LibraryId != proposal.LibraryId || payload.AssetId != proposal.AssetId ||
            payload.EditId != proposal.EditId || payload.Category != proposal.Category ||
            payload.ContentSha256 != proposal.ContentSha256 || payload.Generation != proposal.Generation ||
            payload.ApprovedAtUtc < proposal.ProposedAtUtc || payload.ApprovedAtUtc > proposal.ExpiresAtUtc ||
            payload.ApprovedAtUtc > nowUtc || (!allowExpiredForCompletedPurge && payload.ExpiresAtUtc <= nowUtc))
            throw new InvalidDataException("purge_approval_binding_mismatch");
        return new(payload, payloadBytes, digest, envelope.SigningKeyId,
            envelope.SigningPublicKeySha256, signature);
    }
}
