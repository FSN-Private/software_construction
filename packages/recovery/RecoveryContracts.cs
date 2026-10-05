using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Recovery;

public enum RecoveryDataClass
{
    OriginalImage,
    XmpSidecar,
    VersionGraph,
    LibraryMetadata,
    AuditRecord,
    Configuration,
    ReleaseArtifact,
    AccessEvidence,
    RecoveryEvidence,
    DatabaseSnapshot,
    ObjectInventory,
    PreviewDerivative
}

public enum RecoveryPointState { Capturing, Copying, Verifying, Verified, Failed }
public enum RecoveryWriteState { Admitted, StoragePending, Unknown, Settled, Cleaned }
public enum RecoveryRestoreState { Requested, Restoring, Validating, Ready, Promoting, Promoted, Aborted, Failed, Expired }
public enum RecoveryObservationOutcome { Healthy, Missing, Incomplete, Corrupt, Inaccessible, PendingRetrieval, Failed }

public sealed record RecoveryLogicalReference(
    string OwnerId,
    string LibraryId,
    Guid? AssetId = null,
    Guid? EditId = null,
    string? CommitId = null)
{
    public void Validate()
    {
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (EditId is not null && AssetId is null) throw new InvalidDataException("edit_asset_scope_required");
        if (CommitId is not null && (CommitId.Length != 64 || !RecoveryValidation.IsSha256(CommitId))) throw new InvalidDataException("invalid_commit_id");
        if (AssetId == Guid.Empty || EditId == Guid.Empty) throw new InvalidDataException("invalid_logical_id");
    }
}

public sealed record RecoveryObjectReference(
    RecoveryDataClass DataClass,
    string OwnerId,
    string LibraryId,
    string SourceKey,
    string SourceVersionId,
    string Sha256,
    long ByteLength,
    IReadOnlyList<RecoveryLogicalReference> LogicalReferences)
{
    public RecoveryObjectReference Canonicalized() => this with
    {
        LogicalReferences = LogicalReferences.OrderBy(reference => reference.OwnerId, StringComparer.Ordinal)
            .ThenBy(reference => reference.LibraryId, StringComparer.Ordinal)
            .ThenBy(reference => reference.AssetId)
            .ThenBy(reference => reference.EditId)
            .ThenBy(reference => reference.CommitId, StringComparer.Ordinal)
            .ToArray()
    };

    public void Validate()
    {
        if (DataClass == RecoveryDataClass.PreviewDerivative) throw new InvalidDataException("disposable_class_not_protected");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(SourceKey, nameof(SourceKey), 1024);
        RecoveryValidation.Text(SourceVersionId, nameof(SourceVersionId), 1024);
        RecoveryValidation.Hash(Sha256, nameof(Sha256));
        if (ByteLength < 0) throw new InvalidDataException("invalid_byte_length");
        if (LogicalReferences is null) throw new InvalidDataException("logical_references_required");
        foreach (var reference in LogicalReferences) reference.Validate();
        if (LogicalReferences.Any(reference => reference.OwnerId != OwnerId || reference.LibraryId != LibraryId)) throw new InvalidDataException("logical_scope_mismatch");
        var duplicate = LogicalReferences.GroupBy(reference => (reference.AssetId, reference.EditId, reference.CommitId)).Any(group => group.Count() > 1);
        if (duplicate) throw new InvalidDataException("duplicate_logical_reference");
    }
}

public sealed record RecoveryComponentReference(
    string Name,
    RecoveryDataClass DataClass,
    string SourceKey,
    string SourceVersionId,
    string Sha256,
    long ByteLength)
{
    public void Validate()
    {
        RecoveryValidation.Text(Name, nameof(Name), 160);
        if (DataClass is not (RecoveryDataClass.DatabaseSnapshot or RecoveryDataClass.ObjectInventory or RecoveryDataClass.Configuration or RecoveryDataClass.ReleaseArtifact or RecoveryDataClass.AccessEvidence or RecoveryDataClass.RecoveryEvidence or RecoveryDataClass.AuditRecord))
            throw new InvalidDataException("invalid_recovery_component_class");
        RecoveryValidation.Text(SourceKey, nameof(SourceKey), 1024);
        RecoveryValidation.Text(SourceVersionId, nameof(SourceVersionId), 1024);
        RecoveryValidation.Hash(Sha256, nameof(Sha256));
        if (ByteLength < 0) throw new InvalidDataException("invalid_byte_length");
    }
}

public sealed record RecoveryClassCoverage(RecoveryDataClass DataClass, long ItemCount, long ByteLength)
{
    public void Validate()
    {
        if (DataClass == RecoveryDataClass.PreviewDerivative) throw new InvalidDataException("disposable_class_not_protected");
        if (ItemCount < 0 || ByteLength < 0) throw new InvalidDataException("invalid_coverage_count");
    }
}

public sealed record RecoveryChangeWatermark(long LastSequence, DateTimeOffset? LatestChangedAtUtc)
{
    public void Validate(DateTimeOffset snapshotCutoffUtc)
    {
        RecoveryValidation.Utc(snapshotCutoffUtc, nameof(snapshotCutoffUtc));
        if (LastSequence < 0) throw new InvalidDataException("invalid_change_sequence");
        if ((LastSequence == 0) != (LatestChangedAtUtc is null)) throw new InvalidDataException("ambiguous_change_watermark");
        if (LatestChangedAtUtc is { } changedAt)
        {
            RecoveryValidation.Utc(changedAt, nameof(LatestChangedAtUtc));
            if (changedAt > snapshotCutoffUtc) throw new InvalidDataException("change_after_snapshot");
        }
    }
}

public sealed record RecoveryChange(
    string OperationId,
    string EventType,
    string EntityType,
    string EntityId,
    IReadOnlyList<string> ContentSha256,
    DateTimeOffset ChangedAtUtc)
{
    public void Validate()
    {
        RecoveryValidation.Text(OperationId, nameof(OperationId), 200);
        RecoveryValidation.Text(EventType, nameof(EventType), 120);
        RecoveryValidation.Text(EntityType, nameof(EntityType), 120);
        RecoveryValidation.Text(EntityId, nameof(EntityId), 200);
        RecoveryValidation.Utc(ChangedAtUtc, nameof(ChangedAtUtc));
        if (ContentSha256 is null || ContentSha256.Any(hash => !RecoveryValidation.IsSha256(hash))) throw new InvalidDataException("invalid_change_content_reference");
        if (ContentSha256.Distinct(StringComparer.Ordinal).Count() != ContentSha256.Count) throw new InvalidDataException("duplicate_change_content_reference");
    }
}

public sealed record RecoveryPointManifestV1(
    int SchemaVersion,
    Guid RecoveryPointId,
    Guid CaptureId,
    string OwnerId,
    string LibraryId,
    string SourceRegion,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset SnapshotCutoffUtc,
    string SnapshotId,
    string SnapshotLsn,
    string MigrationDigest,
    RecoveryChangeWatermark ChangeWatermark,
    IReadOnlyList<RecoveryComponentReference> Components,
    IReadOnlyList<RecoveryObjectReference> Objects,
    IReadOnlyList<RecoveryClassCoverage> Coverage)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly RecoveryDataClass[] RequiredCoverage =
    [
        RecoveryDataClass.OriginalImage,
        RecoveryDataClass.XmpSidecar,
        RecoveryDataClass.VersionGraph,
        RecoveryDataClass.LibraryMetadata,
        RecoveryDataClass.AuditRecord,
        RecoveryDataClass.Configuration,
        RecoveryDataClass.ReleaseArtifact,
        RecoveryDataClass.AccessEvidence,
        RecoveryDataClass.RecoveryEvidence,
        RecoveryDataClass.DatabaseSnapshot,
        RecoveryDataClass.ObjectInventory
    ];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public RecoveryPointManifestV1 Canonicalized() => this with
    {
        Components = Components.OrderBy(component => component.DataClass).ThenBy(component => component.Name, StringComparer.Ordinal).ToArray(),
        Objects = Objects.Select(item => item.Canonicalized())
            .OrderBy(item => item.OwnerId, StringComparer.Ordinal)
            .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
            .ThenBy(item => item.DataClass)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.SourceVersionId, StringComparer.Ordinal)
            .ToArray(),
        Coverage = Coverage.OrderBy(item => item.DataClass).ToArray()
    };

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("unsupported_manifest_schema");
        if (RecoveryPointId == Guid.Empty || CaptureId == Guid.Empty) throw new InvalidDataException("invalid_recovery_point_id");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Region(SourceRegion);
        RecoveryValidation.Utc(CapturedAtUtc, nameof(CapturedAtUtc));
        RecoveryValidation.Utc(SnapshotCutoffUtc, nameof(SnapshotCutoffUtc));
        if (SnapshotCutoffUtc > CapturedAtUtc) throw new InvalidDataException("snapshot_after_capture");
        RecoveryValidation.Text(SnapshotId, nameof(SnapshotId), 128);
        if (!RecoveryValidation.IsLsn(SnapshotLsn)) throw new InvalidDataException("invalid_snapshot_lsn");
        RecoveryValidation.Hash(MigrationDigest, nameof(MigrationDigest));
        if (ChangeWatermark is null) throw new InvalidDataException("change_watermark_required");
        ChangeWatermark.Validate(SnapshotCutoffUtc);
        if (Components is null || Objects is null || Coverage is null) throw new InvalidDataException("manifest_collections_required");
        foreach (var component in Components) component.Validate();
        foreach (var item in Objects)
        {
            item.Validate();
            if (item.OwnerId != OwnerId || item.LibraryId != LibraryId) throw new InvalidDataException("manifest_object_scope_mismatch");
        }
        if (Components.Select(component => component.Name).Distinct(StringComparer.Ordinal).Count() != Components.Count) throw new InvalidDataException("duplicate_component_name");
        if (Objects.GroupBy(item => (item.OwnerId, item.LibraryId, item.SourceKey, item.SourceVersionId)).Any(group => group.Count() > 1)) throw new InvalidDataException("duplicate_object_version");
        foreach (var coverage in Coverage) coverage.Validate();
        if (Coverage.Select(item => item.DataClass).Distinct().Count() != Coverage.Count) throw new InvalidDataException("duplicate_coverage_class");
        if (RequiredCoverage.Except(Coverage.Select(item => item.DataClass)).Any()) throw new InvalidDataException("required_class_missing");
        if (Objects.Any(item => item.DataClass == RecoveryDataClass.PreviewDerivative)) throw new InvalidDataException("disposable_class_in_inventory");
        if (!Components.Any(component => component.DataClass == RecoveryDataClass.DatabaseSnapshot)) throw new InvalidDataException("database_snapshot_missing");
        if (!Components.Any(component => component.DataClass == RecoveryDataClass.ObjectInventory)) throw new InvalidDataException("object_inventory_missing");
    }

    public byte[] SerializeCanonical()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(Canonicalized(), JsonOptions);
    }

    public string Digest() => Convert.ToHexString(SHA256.HashData(SerializeCanonical())).ToLowerInvariant();

    public static RecoveryPointManifestV1 ParseCanonical(ReadOnlySpan<byte> json)
    {
        RecoveryPointManifestV1? manifest;
        try { manifest = JsonSerializer.Deserialize<RecoveryPointManifestV1>(json, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("manifest_invalid_json", exception); }
        if (manifest is null) throw new InvalidDataException("manifest_empty");
        manifest.Validate();
        if (!manifest.SerializeCanonical().AsSpan().SequenceEqual(json)) throw new InvalidDataException("manifest_noncanonical");
        return manifest;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

public sealed record RecoveryWriteIntent(
    Guid IntentId,
    Guid? RecoveryPointId,
    Guid? SessionId,
    string OwnerId,
    string LibraryId,
    long Generation,
    RecoveryObjectReference Source,
    string DestinationKey,
    string WriterId,
    RecoveryWriteState State,
    DateTimeOffset AdmittedAtUtc,
    DateTimeOffset? SettledAtUtc,
    string? DestinationVersionId,
    string? ProviderReceipt)
{
    public void Validate()
    {
        if (IntentId == Guid.Empty || RecoveryPointId == Guid.Empty || SessionId == Guid.Empty) throw new InvalidDataException("invalid_write_intent_id");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        if (Generation < 1) throw new InvalidDataException("invalid_lifecycle_generation");
        Source.Validate();
        if (Source.OwnerId != OwnerId || Source.LibraryId != LibraryId) throw new InvalidDataException("write_scope_mismatch");
        RecoveryValidation.Text(DestinationKey, nameof(DestinationKey), 1024);
        RecoveryValidation.Text(WriterId, nameof(WriterId), 200);
        RecoveryValidation.Utc(AdmittedAtUtc, nameof(AdmittedAtUtc));
        if (SettledAtUtc is { } settled) RecoveryValidation.Utc(settled, nameof(SettledAtUtc));
        if (State is RecoveryWriteState.Settled or RecoveryWriteState.Cleaned)
        {
            if (SettledAtUtc is null || string.IsNullOrWhiteSpace(DestinationVersionId)) throw new InvalidDataException("write_settlement_incomplete");
        }
        if (DestinationVersionId is not null) RecoveryValidation.Text(DestinationVersionId, nameof(DestinationVersionId), 1024);
        if (ProviderReceipt is { Length: > 4096 }) throw new InvalidDataException("provider_receipt_too_large");
    }

    public bool CanTransitionTo(RecoveryWriteState next) => (State, next) switch
    {
        (RecoveryWriteState.Admitted, RecoveryWriteState.StoragePending or RecoveryWriteState.Unknown) => true,
        (RecoveryWriteState.StoragePending, RecoveryWriteState.Settled or RecoveryWriteState.Unknown) => true,
        (RecoveryWriteState.Unknown, RecoveryWriteState.Settled or RecoveryWriteState.Cleaned) => true,
        (RecoveryWriteState.Settled, RecoveryWriteState.Cleaned) => true,
        _ => false
    };
}

public sealed record RecoveryCopyReceipt(
    RecoveryObjectReference Source,
    string DestinationBucket,
    string DestinationKey,
    string DestinationVersionId,
    string DestinationAccountAlias,
    string DestinationRegion,
    DateTimeOffset RetentionProtectedFromUtc,
    DateTimeOffset VerifiedAtUtc,
    string VerifierBuild,
    bool RetentionPolicyVerified,
    bool AccessPolicyVerified)
{
    public void Validate()
    {
        Source.Validate();
        RecoveryValidation.Text(DestinationBucket, nameof(DestinationBucket), 255);
        RecoveryValidation.Text(DestinationKey, nameof(DestinationKey), 1024);
        RecoveryValidation.Text(DestinationVersionId, nameof(DestinationVersionId), 1024);
        RecoveryValidation.Text(DestinationAccountAlias, nameof(DestinationAccountAlias), 120);
        RecoveryValidation.Region(DestinationRegion);
        RecoveryValidation.Utc(RetentionProtectedFromUtc, nameof(RetentionProtectedFromUtc));
        RecoveryValidation.Utc(VerifiedAtUtc, nameof(VerifiedAtUtc));
        if (RetentionProtectedFromUtc > VerifiedAtUtc) throw new InvalidDataException("retention_proof_after_verification");
        RecoveryValidation.Text(VerifierBuild, nameof(VerifierBuild), 200);
        if (!RetentionPolicyVerified || !AccessPolicyVerified) throw new InvalidDataException("copy_policy_unverified");
    }
}

public sealed record RecoveryPointDescriptor(
    Guid PointId,
    string OwnerId,
    string LibraryId,
    RecoveryPointState State,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string? ManifestSha256,
    IReadOnlyList<string> ExceptionCodes);

public sealed record RecoveryObservation(
    Guid ObservationId,
    Guid RecoveryPointId,
    RecoveryDataClass DataClass,
    RecoveryObservationOutcome Outcome,
    DateTimeOffset ObservedAtUtc,
    long ItemCount,
    long ByteLength,
    IReadOnlyList<string> SafeCodes);

public sealed record RecoveryRestoreSession(
    Guid SessionId,
    Guid RecoveryPointId,
    string OwnerId,
    string LibraryId,
    RecoveryRestoreState State,
    string IsolatedTargetId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? ValidationDigest,
    string? FailureCode);

public sealed record RecoveryValidationResult(
    Guid SessionId,
    string SchemaVersion,
    string TargetDigest,
    DateTimeOffset CompletedAtUtc,
    bool Healthy,
    IReadOnlyList<string> SafeCodes);

public sealed record RecoveryRetentionHold(
    Guid HoldId,
    Guid RecoveryPointId,
    string OwnerId,
    string LibraryId,
    string Scope,
    string ReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ReleasedAtUtc);

public sealed record RecoveryPurgeJournal(
    Guid OperationId,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    RecoveryDataClass DataClass,
    string Sha256,
    long Generation,
    string PlanDigest,
    DateTimeOffset AuthorizedAtUtc,
    IReadOnlyList<string> AffectedDestinationVersions,
    bool ExportAcknowledged,
    bool PrimaryAbsent,
    bool RecoveryAbsent,
    DateTimeOffset? CompletedAtUtc);

public sealed record RecoveryAuditEvent(
    Guid EventId,
    long Sequence,
    string OwnerId,
    string LibraryId,
    string OperationId,
    string EventType,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    string PreviousHash,
    string EventHash,
    string SignerKeyId,
    string Signature);

public sealed record RecoveryDrillResult(
    Guid RunId,
    Guid RecoveryPointId,
    DateTimeOffset DeclaredLossAtUtc,
    DateTimeOffset RestoreStartedAtUtc,
    DateTimeOffset RecoveryReadyAtUtc,
    long RpoSeconds,
    long RtoSeconds,
    bool PrimaryAccessDenied,
    bool OwnerAccessVerified,
    bool ForeignAccessDenied,
    string CorrectiveActions);

internal static class RecoveryValidation
{
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Hash(string? value, string name)
    {
        if (!IsSha256(value)) throw new InvalidDataException($"invalid_{name.ToLowerInvariant()}");
    }

    public static void Text(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl)) throw new InvalidDataException($"invalid_{name.ToLowerInvariant()}");
    }

    public static void Utc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero) throw new InvalidDataException($"ambiguous_{name.ToLowerInvariant()}");
    }

    public static void Region(string? value)
    {
        Text(value, nameof(Region), 40);
        if (value!.Split('-').Length < 3 || value.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new InvalidDataException("invalid_region");
    }

    public static bool IsLsn(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length is > 0 and <= 8 && part.All(Uri.IsHexDigit));
    }
}
