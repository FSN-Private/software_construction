namespace Platform.Recovery;

public enum RecoveryRetirementPurpose
{
    Routine,
    AuthorizedScopedPurge
}

public sealed record RecoveryRetentionHoldV1(
    string ScopeId,
    string ReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ReleasedAtUtc)
{
    public void Validate()
    {
        RecoveryValidation.Text(ScopeId, nameof(ScopeId), 512);
        RecoveryValidation.Text(ReasonCode, nameof(ReasonCode), 120);
        RecoveryValidation.Utc(CreatedAtUtc, nameof(CreatedAtUtc));
        if (ExpiresAtUtc is { } expires) RecoveryValidation.Utc(expires, nameof(ExpiresAtUtc));
        if (ReleasedAtUtc is { } released) RecoveryValidation.Utc(released, nameof(ReleasedAtUtc));
        if (ExpiresAtUtc is { } expiry && expiry < CreatedAtUtc || ReleasedAtUtc is { } release && release < CreatedAtUtc)
            throw new InvalidDataException("retention_hold_timeline_invalid");
    }

    public bool IsActiveAt(DateTimeOffset nowUtc) =>
        ExpiresAtUtc is null || ExpiresAtUtc > nowUtc;
}

public sealed record RecoveryRetentionCandidateV1(
    string ScopeId,
    string ObjectIdentity,
    DateTimeOffset EligibleAtUtc,
    bool ExactVersionVerified,
    bool RetainedForSharedIdentity,
    long IndependentlyVerifiedReplacementCount,
    IReadOnlyList<RecoveryRetentionHoldV1> Holds,
    bool HasUnsettledWriter,
    bool HasExactPurgeAuthorization,
    DateTimeOffset? PurgeApprovalExpiresAtUtc);

public sealed record RecoveryRetentionDecisionV1(bool MayRetire, string SafeCode, DateTimeOffset? EligibleAtUtc);

public sealed record RecoveryRetentionHoldObservationV1(
    Guid HoldId,
    string Kind,
    string ScopeSha256,
    DateTimeOffset? ExpiresAtUtc,
    bool ActiveAtSnapshot);

/// <summary>
/// One logical lifecycle row sampled from an exact database snapshot whose copy checkpoint was
/// independently signature-verified. A missing lifecycle row is represented with null state and
/// version zero; it is accepted only before the target identity was first observed.
/// </summary>
public sealed record RecoveryRetentionPointObservationV1(
    Guid RecoveryPointId,
    DateTimeOffset SnapshotCutoffUtc,
    DateTimeOffset VerifiedAtUtc,
    string CheckpointPayloadSha256,
    string CheckpointEnvelopeSha256,
    string SigningKeyId,
    string? LifecycleState,
    long LifecycleVersion,
    long ParentLifecycleVersion,
    string? CurrentContentSha256,
    DateTimeOffset? SourceDeletedUntilUtc,
    IReadOnlyList<RecoveryRetentionHoldObservationV1> Holds);

/// <summary>
/// Canonical proof summary for an exact owner/library/logical identity/content digest. HistorySha256
/// commits the ordered point checkpoints, observed lifecycle state, and every in-scope hold row.
/// </summary>
public sealed record RecoveryRetentionHistoryProofV1(
    int SchemaVersion,
    string OwnerId,
    string LibraryId,
    Guid AssetId,
    Guid? EditId,
    string Category,
    string ContentSha256,
    long CompletePointCount,
    string CompletePointSetSha256,
    string HistorySha256,
    string HoldSetSha256,
    string SigningKeyId,
    Guid FirstNonCurrentPointId,
    string FirstCheckpointPayloadSha256,
    string FirstCheckpointEnvelopeSha256,
    DateTimeOffset FirstObservedAtUtc,
    Guid LatestPointId,
    string LatestCheckpointPayloadSha256,
    string LatestCheckpointEnvelopeSha256,
    DateTimeOffset LatestVerifiedAtUtc,
    string LatestState,
    long LatestLifecycleVersion,
    long LatestParentLifecycleVersion,
    bool HasActiveHold,
    DateTimeOffset? EligibleAtUtc);

/// <summary>Fail-closed retention decisions shared by the operator purge executor and tests.</summary>
public static class RetentionService
{
    public const int MinimumRecoveryRetentionDays = 90;

    /// <summary>
    /// Evaluates the first recovery-owned observation of continuous deletion/displacement. Inputs
    /// must come from every point in the strong owner index after verifying its checkpoint, exact
    /// dump version, manifest and copy receipts. Primary timestamps can only move eligibility later.
    /// </summary>
    public static RecoveryRetentionHistoryProofV1 VerifyHistory(
        string ownerId,
        string libraryId,
        Guid assetId,
        Guid? editId,
        string category,
        string contentSha256,
        string trustedSigningKeyId,
        IReadOnlyList<Guid> completePointIds,
        IReadOnlyList<RecoveryRetentionPointObservationV1> observations,
        DateTimeOffset nowUtc)
    {
        RecoveryValidation.Text(ownerId, nameof(ownerId), 200);
        RecoveryValidation.Text(libraryId, nameof(libraryId), 200);
        if (assetId == Guid.Empty || editId == Guid.Empty || category is not ("original" or "xmp") ||
            category == "xmp" && editId is null || category == "original" && editId is not null)
            throw new InvalidDataException("retention_history_scope_invalid");
        RecoveryValidation.Hash(contentSha256, nameof(contentSha256));
        RecoveryValidation.Text(trustedSigningKeyId, nameof(trustedSigningKeyId), 200);
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        if (completePointIds is null || observations is null || completePointIds.Count is < 1 or > 1_000_000 ||
            observations.Count != completePointIds.Count || completePointIds.Any(id => id == Guid.Empty) ||
            completePointIds.Distinct().Count() != completePointIds.Count || observations.Any(item => item is null))
            throw new InvalidDataException("retention_history_inventory_incomplete");

        var pointSetSha256 = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(ownerId, libraryId,
            completePointIds);
        var pointIds = completePointIds.ToHashSet();
        var ordered = observations.OrderBy(item => item.VerifiedAtUtc).ToArray();
        if (ordered.Select(item => item.RecoveryPointId).Distinct().Count() != ordered.Length ||
            !ordered.Select(item => item.RecoveryPointId).ToHashSet().SetEquals(pointIds))
            throw new InvalidDataException("retention_history_point_set_mismatch");

        DateTimeOffset? firstNonCurrentAt = null;
        RecoveryRetentionPointObservationV1? firstNonCurrent = null;
        DateTimeOffset? latestDeletedUntil = null;
        string? previousRetirementState = null;
        long previousLifecycleVersion = 0;
        long previousParentLifecycleVersion = 0;
        var targetWasObserved = false;
        var latestState = "unobserved";
        var holdTracks = new Dictionary<string, HoldTrack>(StringComparer.Ordinal);

        for (var index = 0; index < ordered.Length; index++)
        {
            var observation = ordered[index];
            ValidateObservation(observation, trustedSigningKeyId, nowUtc);
            if (index > 0 && observation.VerifiedAtUtc <= ordered[index - 1].VerifiedAtUtc)
                throw new InvalidDataException("retention_history_time_order_invalid");

            var state = ClassifyState(observation, contentSha256, targetWasObserved);
            if (state is "current" or "deleted") targetWasObserved = observation.CurrentContentSha256 == contentSha256;
            if (state == "absent_after_observation")
                throw new InvalidDataException("retention_history_identity_missing");

            if (state == "current")
            {
                firstNonCurrentAt = null;
                firstNonCurrent = null;
                previousRetirementState = null;
                previousLifecycleVersion = 0;
                previousParentLifecycleVersion = 0;
            }
            else if (state is "deleted" or "displaced")
            {
                if (firstNonCurrentAt is null || previousRetirementState != state ||
                    previousLifecycleVersion != observation.LifecycleVersion ||
                    previousParentLifecycleVersion != observation.ParentLifecycleVersion)
                {
                    firstNonCurrentAt = observation.VerifiedAtUtc;
                    firstNonCurrent = observation;
                }
                previousRetirementState = state;
                previousLifecycleVersion = observation.LifecycleVersion;
                previousParentLifecycleVersion = observation.ParentLifecycleVersion;
            }
            latestState = state;
            latestDeletedUntil = state == "deleted" ? observation.SourceDeletedUntilUtc : null;

            ApplyHolds(holdTracks, observation);
        }

        var latest = ordered[^1];
        var activeHold = holdTracks.Values.Any(item => item.IsActive);
        var holdEligibleAt = holdTracks.Values
            .Where(item => item.WasActive && !item.IsActive)
            .Select(item => Max(item.ProtectedUntilUtc, item.ReleasedObservedAtUtc?.AddDays(MinimumRecoveryRetentionDays)))
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .DefaultIfEmpty()
            .Max();
        DateTimeOffset? eligibleAt = null;
        if (firstNonCurrentAt is { } firstAt && !activeHold && latestState is "deleted" or "displaced")
        {
            eligibleAt = firstAt.AddDays(MinimumRecoveryRetentionDays);
            eligibleAt = Max(eligibleAt, holdEligibleAt == default ? null : holdEligibleAt);
            eligibleAt = Max(eligibleAt, latestDeletedUntil);
        }

        var canonicalObservations = ordered.Select(item => new
        {
            item.RecoveryPointId,
            item.SnapshotCutoffUtc,
            item.VerifiedAtUtc,
            item.CheckpointPayloadSha256,
            item.CheckpointEnvelopeSha256,
            item.SigningKeyId,
            item.LifecycleState,
            item.LifecycleVersion,
            item.ParentLifecycleVersion,
            item.CurrentContentSha256,
            item.SourceDeletedUntilUtc,
            Holds = item.Holds.OrderBy(hold => hold.Kind, StringComparer.Ordinal)
                .ThenBy(hold => hold.HoldId).ToArray()
        }).ToArray();
        var canonicalHistory = new
        {
            schemaVersion = 1,
            ownerId,
            libraryId,
            assetId,
            editId,
            category,
            contentSha256,
            signingKeyId = trustedSigningKeyId,
            completePointCount = completePointIds.Count,
            completePointSetSha256 = pointSetSha256,
            observations = canonicalObservations
        };
        var historySha256 = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(canonicalHistory));
        var holdSetSha256 = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(
            canonicalObservations.Select(item => new { item.RecoveryPointId, item.VerifiedAtUtc, item.Holds }).ToArray()));
        var firstObservation = firstNonCurrent ?? latest;
        return new(1, ownerId, libraryId, assetId, editId, category, contentSha256, completePointIds.Count,
            pointSetSha256, historySha256, holdSetSha256, trustedSigningKeyId,
            firstObservation.RecoveryPointId, firstObservation.CheckpointPayloadSha256, firstObservation.CheckpointEnvelopeSha256,
            firstObservation.VerifiedAtUtc, latest.RecoveryPointId, latest.CheckpointPayloadSha256,
            latest.CheckpointEnvelopeSha256, latest.VerifiedAtUtc, latestState, latest.LifecycleVersion,
            latest.ParentLifecycleVersion,
            activeHold, eligibleAt);
    }

    public static RecoveryRetentionDecisionV1 Assess(
        RecoveryRetentionCandidateV1 candidate,
        RecoveryRetirementPurpose purpose,
        DateTimeOffset nowUtc) => Assess(candidate, purpose, nowUtc, null);

    public static RecoveryRetentionDecisionV1 Assess(
        RecoveryRetentionCandidateV1 candidate,
        RecoveryRetirementPurpose purpose,
        DateTimeOffset nowUtc,
        RecoveryRetentionHistoryProofV1? history)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RecoveryValidation.Utc(nowUtc, nameof(nowUtc));
        RecoveryValidation.Text(candidate.ScopeId, nameof(candidate.ScopeId), 512);
        RecoveryValidation.Text(candidate.ObjectIdentity, nameof(candidate.ObjectIdentity), 2048);
        RecoveryValidation.Utc(candidate.EligibleAtUtc, nameof(candidate.EligibleAtUtc));
        if (candidate.IndependentlyVerifiedReplacementCount < 0 || candidate.Holds is null)
            throw new InvalidDataException("retention_candidate_invalid");
        foreach (var hold in candidate.Holds)
        {
            if (hold is null) throw new InvalidDataException("retention_hold_missing");
            hold.Validate();
        }
        if (candidate.PurgeApprovalExpiresAtUtc is { } approvalExpiry)
            RecoveryValidation.Utc(approvalExpiry, nameof(candidate.PurgeApprovalExpiresAtUtc));

        if (!candidate.ExactVersionVerified)
            return new(false, "recovery_copy_unverified", null);
        if (candidate.HasUnsettledWriter)
            return new(false, "recovery_copy_pending", null);

        try
        {
            if (history is null) throw new InvalidDataException("retention_history_missing");
            ValidateHistoryProof(history, history.OwnerId, history.LibraryId, history.AssetId, history.EditId,
                history.Category, history.ContentSha256, history.CompletePointCount, history.CompletePointSetSha256);
            var expectedScope = RecoveryFenceScope.MediaScopeId(
                new RecoveryLogicalReference(history.OwnerId, history.LibraryId, history.AssetId, history.EditId),
                history.Category, history.ContentSha256);
            if (!string.Equals(candidate.ScopeId, expectedScope, StringComparison.Ordinal))
                throw new InvalidDataException("retention_history_scope_mismatch");
        }
        catch (InvalidDataException)
        {
            return new(false, "recovery_deletion_history_unverified", null);
        }

        if (history.HasActiveHold || history.EligibleAtUtc is null)
            return new(false, history.HasActiveHold ? "retention_hold" : "recovery_identity_current", null);

        var eligibleAt = Max(candidate.EligibleAtUtc, history.EligibleAtUtc)!.Value;
        if (nowUtc < eligibleAt)
            return new(false, "retention_hold", eligibleAt);
        if (candidate.Holds.Any(hold => hold.IsActiveAt(nowUtc)))
            return new(false, "retention_hold", null);

        if (candidate.RetainedForSharedIdentity)
            return new(false, "shared_recovery_copy_retained", null);

        if (purpose == RecoveryRetirementPurpose.AuthorizedScopedPurge)
        {
            if (!candidate.HasExactPurgeAuthorization || candidate.PurgeApprovalExpiresAtUtc is not { } expiry || expiry <= nowUtc)
                return new(false, "purge_approval_required", null);
            return new(true, "eligible", null);
        }

        // Routine retention must preserve the final copy. An independently authorized
        // scoped purge intentionally removes eligible copies, after the history, holds,
        // shared identities and unsettled writers above have all been checked.
        if (candidate.IndependentlyVerifiedReplacementCount < 1)
            return new(false, "final_recovery_copy", null);

        return new(true, "eligible", null);
    }

    public static void ValidateHistoryProof(
        RecoveryRetentionHistoryProofV1 history,
        string ownerId,
        string libraryId,
        Guid assetId,
        Guid? editId,
        string category,
        string contentSha256,
        long pointCount,
        string pointSetSha256)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.SchemaVersion != 1 || history.AssetId == Guid.Empty || history.AssetId != assetId ||
            history.EditId != editId || history.OwnerId != ownerId || history.LibraryId != libraryId ||
            history.Category != category || history.ContentSha256 != contentSha256 ||
            history.CompletePointCount != pointCount || history.CompletePointCount is < 1 or > 1_000_000 ||
            history.CompletePointSetSha256 != pointSetSha256 || history.FirstNonCurrentPointId == Guid.Empty ||
            history.LatestPointId == Guid.Empty || history.LatestState is not ("deleted" or "displaced") ||
            history.LatestParentLifecycleVersion < 0 || history.HasActiveHold || history.EligibleAtUtc is null)
            throw new InvalidDataException("retention_history_proof_invalid");
        RecoveryValidation.Text(history.OwnerId, nameof(history.OwnerId), 200);
        RecoveryValidation.Text(history.LibraryId, nameof(history.LibraryId), 200);
        RecoveryValidation.Text(history.SigningKeyId, nameof(history.SigningKeyId), 200);
        RecoveryValidation.Hash(history.ContentSha256, nameof(history.ContentSha256));
        RecoveryValidation.Hash(history.CompletePointSetSha256, nameof(history.CompletePointSetSha256));
        RecoveryValidation.Hash(history.HistorySha256, nameof(history.HistorySha256));
        RecoveryValidation.Hash(history.HoldSetSha256, nameof(history.HoldSetSha256));
        RecoveryValidation.Hash(history.FirstCheckpointPayloadSha256, nameof(history.FirstCheckpointPayloadSha256));
        RecoveryValidation.Hash(history.FirstCheckpointEnvelopeSha256, nameof(history.FirstCheckpointEnvelopeSha256));
        RecoveryValidation.Hash(history.LatestCheckpointPayloadSha256, nameof(history.LatestCheckpointPayloadSha256));
        RecoveryValidation.Hash(history.LatestCheckpointEnvelopeSha256, nameof(history.LatestCheckpointEnvelopeSha256));
        RecoveryValidation.Utc(history.FirstObservedAtUtc, nameof(history.FirstObservedAtUtc));
        RecoveryValidation.Utc(history.LatestVerifiedAtUtc, nameof(history.LatestVerifiedAtUtc));
        RecoveryValidation.Utc(history.EligibleAtUtc.Value, nameof(history.EligibleAtUtc));
        if (history.LatestVerifiedAtUtc < history.FirstObservedAtUtc ||
            history.EligibleAtUtc.Value < history.FirstObservedAtUtc.AddDays(MinimumRecoveryRetentionDays))
            throw new InvalidDataException("retention_history_timeline_invalid");
        var expectedScope = RecoveryFenceScope.MediaScopeId(
            new RecoveryLogicalReference(history.OwnerId, history.LibraryId, history.AssetId, history.EditId),
            history.Category, history.ContentSha256);
        if (category is not ("original" or "xmp") ||
            category == "original" && editId is not null || category == "xmp" && editId is null ||
            !string.Equals(expectedScope, RecoveryFenceScope.MediaScopeId(
                new RecoveryLogicalReference(ownerId, libraryId, assetId, editId), category, contentSha256), StringComparison.Ordinal))
            throw new InvalidDataException("retention_history_scope_invalid");
    }

    private static string ClassifyState(RecoveryRetentionPointObservationV1 observation,
        string contentSha256, bool targetWasObserved)
    {
        if (observation.LifecycleState is null)
            return targetWasObserved ? "absent_after_observation" : "not_yet_present";
        if (observation.LifecycleState == "integrity-error")
            throw new InvalidDataException("retention_history_integrity_error");
        if (observation.LifecycleState == "prepared")
            return targetWasObserved ? "absent_after_observation" : "not_yet_present";
        if (observation.CurrentContentSha256 == contentSha256)
        {
            return observation.LifecycleState switch
            {
                "active" or "restored" => "current",
                "recoverable-deleted" => "deleted",
                _ => throw new InvalidDataException("retention_history_state_invalid")
            };
        }
        if (observation.CurrentContentSha256 is null)
            return targetWasObserved ? "absent_after_observation" : "not_yet_present";
        if (!targetWasObserved) return "not_yet_present";
        return observation.LifecycleState is "active" or "restored"
            ? "displaced"
            : throw new InvalidDataException("retention_history_state_invalid");
    }

    private static void ValidateObservation(RecoveryRetentionPointObservationV1 observation,
        string signingKeyId, DateTimeOffset nowUtc)
    {
        if (observation.RecoveryPointId == Guid.Empty || observation.LifecycleVersion < 0 || observation.ParentLifecycleVersion < 0 ||
            observation.LifecycleState is null && observation.LifecycleVersion != 0 ||
            observation.LifecycleState is not null && observation.LifecycleVersion < 1 ||
            observation.Holds is null || observation.Holds.Any(item => item is null))
            throw new InvalidDataException("retention_history_observation_invalid");
        RecoveryValidation.Utc(observation.SnapshotCutoffUtc, nameof(observation.SnapshotCutoffUtc));
        RecoveryValidation.Utc(observation.VerifiedAtUtc, nameof(observation.VerifiedAtUtc));
        if (observation.SnapshotCutoffUtc > observation.VerifiedAtUtc || observation.VerifiedAtUtc > nowUtc ||
            observation.SigningKeyId != signingKeyId)
            throw new InvalidDataException("retention_history_checkpoint_invalid");
        RecoveryValidation.Hash(observation.CheckpointPayloadSha256, nameof(observation.CheckpointPayloadSha256));
        RecoveryValidation.Hash(observation.CheckpointEnvelopeSha256, nameof(observation.CheckpointEnvelopeSha256));
        if (observation.CurrentContentSha256 is { } digest) RecoveryValidation.Hash(digest, nameof(observation.CurrentContentSha256));
        if (observation.SourceDeletedUntilUtc is { } expiry) RecoveryValidation.Utc(expiry, nameof(observation.SourceDeletedUntilUtc));
        if (observation.Holds.Select(HoldIdentity).Distinct(StringComparer.Ordinal).Count() != observation.Holds.Count)
            throw new InvalidDataException("retention_history_hold_duplicate");
        foreach (var hold in observation.Holds)
        {
            if (hold.HoldId == Guid.Empty || hold.Kind is not ("lifecycle" or "recovery"))
                throw new InvalidDataException("retention_history_hold_invalid");
            RecoveryValidation.Hash(hold.ScopeSha256, nameof(hold.ScopeSha256));
            if (hold.ExpiresAtUtc is { } holdExpiry) RecoveryValidation.Utc(holdExpiry, nameof(hold.ExpiresAtUtc));
        }
    }

    private static void ApplyHolds(Dictionary<string, HoldTrack> tracks,
        RecoveryRetentionPointObservationV1 observation)
    {
        var present = observation.Holds.Select(HoldIdentity).ToHashSet(StringComparer.Ordinal);
        foreach (var track in tracks.Values.Where(item => item.IsActive && !item.RequiresIndependentRelease &&
                     !present.Contains(item.Kind + ":" + item.HoldId.ToString("D").ToLowerInvariant())))
        {
            track.IsActive = false;
            track.ReleasedObservedAtUtc = observation.VerifiedAtUtc;
        }
        foreach (var hold in observation.Holds)
        {
            var identity = HoldIdentity(hold);
            if (!tracks.TryGetValue(identity, out var track))
            {
                track = new(hold.HoldId, hold.Kind, hold.ScopeSha256);
                tracks.Add(identity, track);
            }
            else if (track.Kind != hold.Kind || track.ScopeSha256 != hold.ScopeSha256)
            {
                throw new InvalidDataException("retention_history_hold_scope_changed");
            }

            if (hold.ExpiresAtUtc is { } expiry) track.ProtectedUntilUtc = Max(track.ProtectedUntilUtc, expiry);
            if (hold.Kind == "recovery" || hold.ExpiresAtUtc is null)
            {
                // Neither primary release timestamps nor row deletion are authority to clear
                // indefinite or recovery-owned holds. This protocol has no signed hold-release
                // receipt yet, so preserve them as active and fail closed.
                track.RequiresIndependentRelease = true;
                track.WasActive = true;
                track.IsActive = true;
                track.ReleasedObservedAtUtc = null;
            }
            else if (hold.ActiveAtSnapshot)
            {
                track.WasActive = true;
                track.IsActive = true;
                track.ReleasedObservedAtUtc = null;
            }
            else if (track.IsActive && !track.RequiresIndependentRelease)
            {
                track.IsActive = false;
                track.ReleasedObservedAtUtc = observation.VerifiedAtUtc;
            }
        }
    }

    private static string HoldIdentity(RecoveryRetentionHoldObservationV1 hold) =>
        hold.Kind + ":" + hold.HoldId.ToString("D").ToLowerInvariant();

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left.Value >= right.Value ? left : right;

    private sealed class HoldTrack(Guid holdId, string kind, string scopeSha256)
    {
        public Guid HoldId { get; } = holdId;
        public string Kind { get; } = kind;
        public string ScopeSha256 { get; } = scopeSha256;
        public bool WasActive { get; set; }
        public bool IsActive { get; set; }
        public bool RequiresIndependentRelease { get; set; }
        public DateTimeOffset? ProtectedUntilUtc { get; set; }
        public DateTimeOffset? ReleasedObservedAtUtc { get; set; }
    }
}
