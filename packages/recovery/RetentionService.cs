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
        ReleasedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);
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

/// <summary>Fail-closed retention decisions shared by the operator purge executor and tests.</summary>
public static class RetentionService
{
    public static RecoveryRetentionDecisionV1 Assess(
        RecoveryRetentionCandidateV1 candidate,
        RecoveryRetirementPurpose purpose,
        DateTimeOffset nowUtc)
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

        if (nowUtc < candidate.EligibleAtUtc)
            return new(false, "retention_hold", candidate.EligibleAtUtc);
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

        if (candidate.IndependentlyVerifiedReplacementCount < 1)
            return new(false, "final_recovery_copy", null);
        return new(true, "eligible", null);
    }
}
