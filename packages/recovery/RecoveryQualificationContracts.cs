using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

public sealed record RecoveryQualificationV1(
    int SchemaVersion,
    Guid QualificationId,
    Guid RunId,
    DateTimeOffset QualifiedAtUtc,
    string SourceRevision,
    string RecoveryTemplateDigest,
    string ApiBuildDigest,
    string RecoveryWorkerBuildDigest,
    string IngestWorkerBuildDigest,
    string RendererBuildDigest,
    string MigrationDigest,
    RecoveryQualificationAccountV1 Primary,
    RecoveryQualificationAccountV1 Recovery,
    RecoveryQualifiedPointV1 Point,
    RecoveryQualifiedWorkloadV1 Workload,
    RecoveryPrimaryAccessEvidenceV1 PrimaryAccessDenied,
    RecoveryQualificationRestoreV1 Restore,
    RecoveryQualificationChecksV1 Checks,
    IReadOnlyList<RecoveryQualifiedAlertV1> Alerts,
    RecoveryQualificationRetentionV1 Retention,
    RecoveryQualificationPurgeV1 Purge,
    IReadOnlyList<string> CorrectiveActions,
    IReadOnlyList<string> UnresolvedBlockingCorrections)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || QualificationId == Guid.Empty || RunId == Guid.Empty)
            throw new InvalidDataException("invalid_qualification_identity");
        RecoveryValidation.Utc(QualifiedAtUtc, nameof(QualifiedAtUtc));
        RecoveryValidation.Text(SourceRevision, nameof(SourceRevision), 200);
        RecoveryValidation.Hash(RecoveryTemplateDigest, nameof(RecoveryTemplateDigest));
        RecoveryValidation.Hash(ApiBuildDigest, nameof(ApiBuildDigest));
        RecoveryValidation.Hash(RecoveryWorkerBuildDigest, nameof(RecoveryWorkerBuildDigest));
        RecoveryValidation.Hash(IngestWorkerBuildDigest, nameof(IngestWorkerBuildDigest));
        RecoveryValidation.Hash(RendererBuildDigest, nameof(RendererBuildDigest));
        RecoveryValidation.Hash(MigrationDigest, nameof(MigrationDigest));
        Primary.Validate();
        Recovery.Validate();
        if (Primary.AccountId == Recovery.AccountId || Primary.Region == Recovery.Region)
            throw new InvalidDataException("recovery_environment_not_independent");
        Point.Validate();
        Workload.Validate();
        PrimaryAccessDenied.Validate();
        Restore.Validate();
        if (Checks is null || Purge is null) throw new InvalidDataException("qualification_results_required");
        if (Alerts is null) throw new InvalidDataException("qualification_alerts_required");
        foreach (var alert in Alerts) alert.Validate();
        Retention.Validate();
        RecoveryEvidenceCodec.ValidateSafeCodes(CorrectiveActions);
        RecoveryEvidenceCodec.ValidateSafeCodes(UnresolvedBlockingCorrections);
    }

    public byte[] SerializeCanonical() => RecoveryEvidenceCodec.SerializeCanonical(this);
}

public sealed record RecoveryQualificationEnvelopeV1(
    [property: JsonPropertyName("schemaVersion")] int EnvelopeSchemaVersion,
    RecoveryQualificationV1 Payload,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    string CanonicalPayloadBase64,
    string SignatureBase64)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (EnvelopeSchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("unsupported_qualification_envelope");
        Payload.Validate();
        RecoveryValidation.Hash(PayloadSha256, nameof(PayloadSha256));
        RecoveryValidation.Text(SigningKeyId, nameof(SigningKeyId), 512);
        RecoveryValidation.Hash(SigningPublicKeySha256, nameof(SigningPublicKeySha256));
        try { _ = Convert.FromBase64String(CanonicalPayloadBase64); _ = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_qualification_signature_encoding", exception); }
        if (string.IsNullOrWhiteSpace(SignatureBase64)) throw new InvalidDataException("qualification_signature_required");
    }
}

public sealed record RecoveryQualificationAccountV1(string AccountId, string AccountAlias, string Region)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId) || AccountId.Length != 12 || AccountId.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("invalid_qualification_account_id");
        RecoveryValidation.Text(AccountAlias, nameof(AccountAlias), 120);
        RecoveryValidation.Region(Region);
    }
}

public sealed record RecoveryQualifiedClassCoverageV1(
    string DataClass,
    long ExpectedItemCount,
    long ObservedItemCount,
    long ExpectedByteLength,
    long ObservedByteLength,
    string Outcome)
{
    public void Validate()
    {
        if (!RecoveryEvidenceCodec.ObservationClasses.Contains(DataClass, StringComparer.Ordinal)) throw new InvalidDataException("invalid_qualification_class");
        if (!RecoveryEvidenceCodec.ObservationOutcomes.Contains(Outcome, StringComparer.Ordinal)) throw new InvalidDataException("invalid_qualification_outcome");
        if (ExpectedItemCount < 0 || ObservedItemCount < 0 || ExpectedByteLength < 0 || ObservedByteLength < 0)
            throw new InvalidDataException("invalid_qualification_coverage");
    }
}

public sealed record RecoveryQualifiedPointV1(
    Guid PointId,
    Guid CaptureId,
    string ManifestSha256,
    string CheckpointSha256,
    string CheckpointKeyId,
    string CheckpointSignatureBase64,
    DateTimeOffset SnapshotCutoffUtc,
    string SnapshotLsn,
    long ChangeSequence,
    DateTimeOffset? LatestChangedAtUtc,
    IReadOnlyList<RecoveryQualifiedClassCoverageV1> Coverage)
{
    public void Validate()
    {
        if (PointId == Guid.Empty || CaptureId == Guid.Empty) throw new InvalidDataException("invalid_qualification_point");
        RecoveryValidation.Hash(ManifestSha256, nameof(ManifestSha256));
        RecoveryValidation.Hash(CheckpointSha256, nameof(CheckpointSha256));
        RecoveryValidation.Text(CheckpointKeyId, nameof(CheckpointKeyId), 512);
        RecoveryValidation.Text(CheckpointSignatureBase64, nameof(CheckpointSignatureBase64), 4096);
        try { _ = Convert.FromBase64String(CheckpointSignatureBase64); }
        catch (FormatException exception) { throw new InvalidDataException("invalid_checkpoint_signature", exception); }
        RecoveryValidation.Utc(SnapshotCutoffUtc, nameof(SnapshotCutoffUtc));
        if (!RecoveryValidation.IsLsn(SnapshotLsn) || ChangeSequence < 0) throw new InvalidDataException("invalid_qualification_watermark");
        if (LatestChangedAtUtc is { } latest)
        {
            RecoveryValidation.Utc(latest, nameof(LatestChangedAtUtc));
            if (latest > SnapshotCutoffUtc) throw new InvalidDataException("qualification_change_after_cutoff");
        }
        if ((ChangeSequence == 0) != (LatestChangedAtUtc is null)) throw new InvalidDataException("ambiguous_qualification_watermark");
        if (Coverage is null || Coverage.Count != RecoveryEvidenceCodec.ObservationClasses.Length) throw new InvalidDataException("qualification_coverage_incomplete");
        foreach (var item in Coverage) item.Validate();
        if (Coverage.Select(item => item.DataClass).Distinct(StringComparer.Ordinal).Count() != Coverage.Count ||
            RecoveryEvidenceCodec.ObservationClasses.Except(Coverage.Select(item => item.DataClass), StringComparer.Ordinal).Any())
            throw new InvalidDataException("qualification_coverage_incomplete");
    }
}

public sealed record RecoveryQualifiedWorkloadV1(
    long? ImageCount,
    long? ContentRecordCount,
    long? ProtectedBytes,
    long? ApprovedMaximumProtectedBytes,
    string? MonthlyBudget,
    string? ApprovalId)
{
    public void Validate()
    {
        if (ImageCount < 0 || ContentRecordCount < 0 || ProtectedBytes < 0 || ApprovedMaximumProtectedBytes < 0)
            throw new InvalidDataException("invalid_qualification_workload");
        if (MonthlyBudget is not null) RecoveryValidation.Text(MonthlyBudget, nameof(MonthlyBudget), 80);
        if (ApprovalId is not null) RecoveryValidation.Text(ApprovalId, nameof(ApprovalId), 200);
    }
}

public sealed record RecoveryPrimaryAccessEvidenceV1(bool Verified, DateTimeOffset? ObservedAtUtc, string? EvidenceDigest)
{
    public void Validate()
    {
        if (ObservedAtUtc is { } observed) RecoveryValidation.Utc(observed, nameof(ObservedAtUtc));
        if (Verified)
        {
            if (ObservedAtUtc is null || EvidenceDigest is null) throw new InvalidDataException("primary_denial_evidence_required");
            RecoveryValidation.Hash(EvidenceDigest, nameof(EvidenceDigest));
        }
        else if (EvidenceDigest is not null) RecoveryValidation.Hash(EvidenceDigest, nameof(EvidenceDigest));
    }
}

public sealed record RecoveryQualificationRestoreV1(
    DateTimeOffset? DeclaredLossAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ReadyAtUtc,
    long? RpoSeconds,
    long? RtoSeconds,
    string? IsolatedTargetDigest)
{
    public void Validate()
    {
        if (DeclaredLossAtUtc is { } loss) RecoveryValidation.Utc(loss, nameof(DeclaredLossAtUtc));
        if (StartedAtUtc is { } start) RecoveryValidation.Utc(start, nameof(StartedAtUtc));
        if (ReadyAtUtc is { } ready) RecoveryValidation.Utc(ready, nameof(ReadyAtUtc));
        if (RpoSeconds < 0 || RtoSeconds < 0) throw new InvalidDataException("invalid_qualification_duration");
        if (DeclaredLossAtUtc is { } declared && StartedAtUtc is { } started && started < declared) throw new InvalidDataException("restore_started_before_loss");
        if (StartedAtUtc is { } began && ReadyAtUtc is { } complete && complete < began) throw new InvalidDataException("restore_completed_before_start");
        if (IsolatedTargetDigest is not null) RecoveryValidation.Hash(IsolatedTargetDigest, nameof(IsolatedTargetDigest));
    }
}

public sealed record RecoveryQualificationChecksV1(
    bool DatabaseRestore,
    bool ObjectHashes,
    bool VersionGraph,
    bool Xmp,
    bool Metadata,
    bool Audit,
    bool OwnerLogin,
    bool ForeignAccessDenied,
    bool Configuration,
    bool ReleaseArtifacts,
    bool AccessPolicy);

public sealed record RecoveryQualifiedAlertV1(
    Guid AlertId,
    DateTimeOffset ThresholdAtUtc,
    DateTimeOffset? ProviderAcceptedAtUtc,
    DateTimeOffset? DeliveryAtUtc,
    string DeliveryStatus,
    string? DeliveryEvidenceSha256)
{
    public void Validate() => new RecoveryAlertDeliveryV1(AlertId, ThresholdAtUtc, ProviderAcceptedAtUtc, DeliveryAtUtc, DeliveryStatus, DeliveryEvidenceSha256).Validate();
}

public sealed record RecoveryQualificationRetentionV1(
    int MinimumDays,
    string ColdStorageClass,
    DateTimeOffset? ColdRetrievalVerifiedAtUtc,
    bool FinalCopyProtected)
{
    public void Validate()
    {
        if (MinimumDays < 90) throw new InvalidDataException("qualification_retention_too_short");
        RecoveryValidation.Text(ColdStorageClass, nameof(ColdStorageClass), 80);
        if (ColdRetrievalVerifiedAtUtc is { } verified) RecoveryValidation.Utc(verified, nameof(ColdRetrievalVerifiedAtUtc));
    }
}

public sealed record RecoveryQualificationPurgeV1(bool Tested, bool FinalCopyProtected, bool ExportAcknowledged);

public sealed record VerifiedRecoveryQualification(
    RecoveryQualificationV1 Payload,
    byte[] CanonicalPayloadBytes,
    string PayloadSha256,
    string SigningKeyId,
    string SigningPublicKeySha256,
    byte[] Signature);

public static class RecoveryQualificationCodec
{
    public const string FileName = "recovery-qualification.v1.json";

    public static byte[] SerializePayload(RecoveryQualificationV1 payload)
    {
        payload.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(payload);
    }

    public static RecoveryQualificationV1 ParsePayload(ReadOnlySpan<byte> bytes)
    {
        RecoveryQualificationV1 payload;
        try { payload = JsonSerializer.Deserialize<RecoveryQualificationV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("qualification_payload_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("qualification_payload_invalid_json", exception); }
        payload.Validate();
        if (!payload.SerializeCanonical().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("qualification_payload_noncanonical");
        return payload;
    }

    public static RecoveryQualificationEnvelopeV1 CreateEnvelope(RecoveryQualificationV1 payload, RecoveryEvidenceSignature signature)
    {
        var bytes = SerializePayload(payload);
        var envelope = new RecoveryQualificationEnvelopeV1(1, payload, RecoveryEvidenceCodec.Hash(bytes), signature.KeyId,
            signature.PublicKeySha256, Convert.ToBase64String(bytes), Convert.ToBase64String(signature.Signature));
        envelope.Validate();
        return envelope;
    }

    public static byte[] SerializeEnvelope(RecoveryQualificationEnvelopeV1 envelope)
    {
        envelope.Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(envelope);
    }

    public static RecoveryQualificationEnvelopeV1 ParseEnvelope(ReadOnlySpan<byte> bytes)
    {
        RecoveryQualificationEnvelopeV1 envelope;
        try { envelope = JsonSerializer.Deserialize<RecoveryQualificationEnvelopeV1>(bytes, RecoveryEvidenceCodec.JsonOptions) ?? throw new InvalidDataException("qualification_envelope_empty"); }
        catch (JsonException exception) { throw new InvalidDataException("qualification_envelope_invalid_json", exception); }
        envelope.Validate();
        if (!SerializeEnvelope(envelope).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("qualification_envelope_noncanonical");
        return envelope;
    }

    public static VerifiedRecoveryQualification Verify(RecoveryQualificationEnvelopeV1 envelope, RecoverySigningTrust trust)
    {
        envelope.Validate();
        var bytes = Convert.FromBase64String(envelope.CanonicalPayloadBase64);
        var parsed = ParsePayload(bytes);
        if (!parsed.SerializeCanonical().AsSpan().SequenceEqual(envelope.Payload.SerializeCanonical()))
            throw new InvalidDataException("qualification_envelope_payload_mismatch");
        var signature = RecoveryEvidenceCodec.VerifyDetached(bytes, envelope.PayloadSha256, envelope.SignatureBase64,
            envelope.SigningKeyId, envelope.SigningPublicKeySha256, trust);
        return new(parsed, bytes, RecoveryEvidenceCodec.Hash(bytes), envelope.SigningKeyId, envelope.SigningPublicKeySha256, signature);
    }

}

public sealed record RecoveryQualificationBindingV1(
    string SourceRevision,
    string RecoveryTemplateDigest,
    string ApiBuildDigest,
    string RecoveryWorkerBuildDigest,
    string IngestWorkerBuildDigest,
    string RendererBuildDigest,
    string MigrationDigest);

/// <summary>
/// Applies the release criteria after an envelope has been parsed. Structural parsing
/// intentionally accepts failed drill evidence so failures can still be recorded.
/// </summary>
public static class RecoveryQualificationReleaseGate
{
    public static void RequireReady(
        VerifiedRecoveryQualification qualification,
        RecoverySigningTrust trust,
        RecoveryQualificationBindingV1 release,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(release);
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        var payload = qualification.Payload;
        var envelope = RecoveryQualificationCodec.CreateEnvelope(payload,
            new RecoveryEvidenceSignature(qualification.SigningKeyId, qualification.SigningPublicKeySha256, qualification.Signature));
        var reverified = RecoveryQualificationCodec.Verify(envelope, trust);
        if (reverified.PayloadSha256 != qualification.PayloadSha256 ||
            !reverified.CanonicalPayloadBytes.AsSpan().SequenceEqual(qualification.CanonicalPayloadBytes))
            throw new InvalidDataException("qualification_verified_evidence_mismatch");

        if (payload.QualifiedAtUtc > nowUtc.AddMinutes(5) || payload.QualifiedAtUtc < nowUtc.AddMonths(-3))
            throw new InvalidDataException("qualification_expired_or_future");
        if (payload.SourceRevision != release.SourceRevision ||
            payload.RecoveryTemplateDigest != release.RecoveryTemplateDigest ||
            payload.ApiBuildDigest != release.ApiBuildDigest ||
            payload.RecoveryWorkerBuildDigest != release.RecoveryWorkerBuildDigest ||
            payload.IngestWorkerBuildDigest != release.IngestWorkerBuildDigest ||
            payload.RendererBuildDigest != release.RendererBuildDigest ||
            payload.MigrationDigest != release.MigrationDigest)
            throw new InvalidDataException("qualification_release_binding_mismatch");
        if (payload.Primary.AccountId == payload.Recovery.AccountId || payload.Primary.Region == payload.Recovery.Region)
            throw new InvalidDataException("recovery_environment_not_independent");

        if (!payload.PrimaryAccessDenied.Verified || payload.Restore.RpoSeconds is null or < 0 or > 3600 ||
            payload.Restore.RtoSeconds is null or < 0 or > 86400 || payload.Restore.IsolatedTargetDigest is null)
            throw new InvalidDataException("qualification_recovery_objective_failed");
        var restore = payload.Restore;
        if (restore.DeclaredLossAtUtc is not { } loss || restore.StartedAtUtc is not { } started ||
            restore.ReadyAtUtc is not { } ready || payload.Point.SnapshotCutoffUtc > loss ||
            payload.PrimaryAccessDenied.ObservedAtUtc is not { } deniedAt || deniedAt > started || deniedAt > loss)
            throw new InvalidDataException("qualification_timeline_incomplete");
        var measuredRpo = ElapsedWholeSeconds(payload.Point.SnapshotCutoffUtc, loss);
        var measuredRto = ElapsedWholeSeconds(loss, ready);
        if (restore.RpoSeconds != measuredRpo || restore.RtoSeconds != measuredRto)
            throw new InvalidDataException("qualification_duration_mismatch");
        var lastEvidenceAt = new[]
            {
                ready,
                payload.PrimaryAccessDenied.ObservedAtUtc.Value,
                payload.Retention.ColdRetrievalVerifiedAtUtc ?? DateTimeOffset.MinValue
            }
            .Concat(payload.Alerts.Select(alert => alert.DeliveryAtUtc ?? DateTimeOffset.MinValue))
            .Max();
        if (payload.QualifiedAtUtc < lastEvidenceAt || lastEvidenceAt == DateTimeOffset.MinValue)
            throw new InvalidDataException("qualification_predates_evidence");
        var checks = payload.Checks;
        if (!checks.DatabaseRestore || !checks.ObjectHashes || !checks.VersionGraph || !checks.Xmp ||
            !checks.Metadata || !checks.Audit || !checks.OwnerLogin || !checks.ForeignAccessDenied ||
            !checks.Configuration || !checks.ReleaseArtifacts || !checks.AccessPolicy)
            throw new InvalidDataException("qualification_validation_failed");
        if (payload.Point.Coverage.Any(item => item.Outcome != "healthy" ||
            item.ExpectedItemCount != item.ObservedItemCount || item.ExpectedByteLength != item.ObservedByteLength))
            throw new InvalidDataException("qualification_coverage_failed");

        var workload = payload.Workload;
        if (workload.ImageCount is null or < 100_000 || workload.ContentRecordCount is null or < 1_000_000 || workload.ProtectedBytes is null ||
            workload.ApprovedMaximumProtectedBytes is null || workload.ProtectedBytes > workload.ApprovedMaximumProtectedBytes ||
            string.IsNullOrWhiteSpace(workload.MonthlyBudget) || string.IsNullOrWhiteSpace(workload.ApprovalId))
            throw new InvalidDataException("qualification_workload_unapproved");
        if (payload.Alerts.Count == 0 || payload.Alerts.Any(alert => alert.DeliveryStatus != "delivered" ||
            alert.DeliveryAtUtc is null || alert.DeliveryAtUtc > alert.ThresholdAtUtc.AddMinutes(15) ||
            alert.DeliveryEvidenceSha256 is null))
            throw new InvalidDataException("qualification_alert_delivery_unproven");
        if (payload.Retention.MinimumDays < 90 || payload.Retention.ColdStorageClass != "GLACIER_IR" ||
            payload.Retention.ColdRetrievalVerifiedAtUtc is null || !payload.Retention.FinalCopyProtected ||
            !payload.Purge.Tested || !payload.Purge.FinalCopyProtected || !payload.Purge.ExportAcknowledged ||
            payload.UnresolvedBlockingCorrections.Count != 0)
            throw new InvalidDataException("qualification_retention_or_purge_failed");
    }

    private static long ElapsedWholeSeconds(DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = (to - from).TotalSeconds;
        if (seconds < 0) throw new InvalidDataException("qualification_negative_duration");
        return checked((long)Math.Ceiling(seconds));
    }
}
