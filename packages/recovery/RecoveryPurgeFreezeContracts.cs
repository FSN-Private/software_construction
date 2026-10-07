using System.Text.Json;

namespace Platform.Recovery;

/// <summary>Primary API receipt for a reversible lifecycle freeze. Its exact bytes are included
/// in a signed post-freeze recovery snapshot and the final owner-approved purge plan.</summary>
public sealed record RecoveryPurgeFreezeAcknowledgementV1(
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
    string ProposalDigest,
    string AuthenticatedOwnerId,
    DateTimeOffset AuthTimeUtc,
    DateTimeOffset FrozenAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public void Validate(RecoveryPurgeProposalV1? proposal = null)
    {
        if (SchemaVersion != CurrentSchemaVersion || OperationId == Guid.Empty || EventId == Guid.Empty ||
            AssetId == Guid.Empty || EditId == Guid.Empty || ExpectedLifecycleVersion <= 0 || Generation <= 0)
            throw new InvalidDataException("purge_freeze_identity_invalid");
        RecoveryValidation.Text(OwnerId, nameof(OwnerId), 200);
        RecoveryValidation.Text(LibraryId, nameof(LibraryId), 200);
        RecoveryValidation.Text(AuthenticatedOwnerId, nameof(AuthenticatedOwnerId), 200);
        if (AuthenticatedOwnerId != OwnerId || Category is not ("original" or "xmp") ||
            Category == "original" && EditId is not null || Category == "xmp" && EditId is null)
            throw new InvalidDataException("purge_freeze_scope_invalid");
        RecoveryValidation.Hash(ContentSha256, nameof(ContentSha256));
        RecoveryValidation.Hash(ProposalDigest, nameof(ProposalDigest));
        RecoveryValidation.Utc(AuthTimeUtc, nameof(AuthTimeUtc));
        RecoveryValidation.Utc(FrozenAtUtc, nameof(FrozenAtUtc));
        if (AuthTimeUtc > FrozenAtUtc || FrozenAtUtc - AuthTimeUtc > TimeSpan.FromMinutes(5))
            throw new InvalidDataException("purge_freeze_authentication_stale");
        if (proposal is not null && (OperationId != proposal.OperationId || EventId != proposal.EventId ||
            OwnerId != proposal.OwnerId || LibraryId != proposal.LibraryId || AssetId != proposal.AssetId ||
            EditId != proposal.EditId || Category != proposal.Category || ContentSha256 != proposal.ContentSha256 ||
            ExpectedLifecycleVersion != proposal.ExpectedLifecycleVersion || Generation != proposal.Generation ||
            ProposalDigest != proposal.PlanDigest))
            throw new InvalidDataException("purge_freeze_proposal_mismatch");
    }

    public byte[] SerializeCanonical()
    {
        Validate();
        return RecoveryEvidenceCodec.SerializeCanonical(this);
    }

    public string Digest() => RecoveryEvidenceCodec.Hash(SerializeCanonical());

    public static RecoveryPurgeFreezeAcknowledgementV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        RecoveryPurgeFreezeAcknowledgementV1 acknowledgement;
        try
        {
            acknowledgement = JsonSerializer.Deserialize<RecoveryPurgeFreezeAcknowledgementV1>(bytes, JsonOptions)
                ?? throw new InvalidDataException("purge_freeze_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("purge_freeze_invalid_json", exception);
        }
        acknowledgement.Validate();
        if (!acknowledgement.SerializeCanonical().AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("purge_freeze_noncanonical");
        return acknowledgement;
    }
}
