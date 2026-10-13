using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryRetentionHistoryChecks
{
    internal static async Task RunAsync()
    {
        var artifact = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(artifact, [1, 2, 3]);
            if (!(await RecoveryPurgeRunner.ReadBoundedArtifactAsync(artifact, 3, default)).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }))
                throw new InvalidOperationException("Bounded purge input changed bytes");
            foreach (var length in new[] { 0L, 4L, 1_000_000L })
            {
                using (var file = new FileStream(artifact, FileMode.Open, FileAccess.Write)) file.SetLength(length);
                try
                {
                    _ = await RecoveryPurgeRunner.ReadBoundedArtifactAsync(artifact, 3, default);
                    throw new InvalidOperationException("Empty or oversized purge input was allocated/read");
                }
                catch (RecoveryWorkerException error) when (error.SafeCode == "purge_artifact_size_invalid") { }
            }
        }
        finally { File.Delete(artifact); }
        var now = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        var first = now.AddDays(-100);
        var asset = Guid.NewGuid();
        var digest = new string('a', 64);
        var other = new string('b', 64);
        var hold = Guid.NewGuid();
        var holdRow = new Dictionary<string, string?>
        {
            ["hold_id"] = hold.ToString("D"),
            ["owner_id"] = "owner",
            ["library_id"] = "library",
            ["asset_id"] = asset.ToString("D"),
            ["edit_id"] = null,
            ["reason_code"] = "restore",
            ["source_reference"] = "private-operation",
            ["recovery_point_id"] = Guid.NewGuid().ToString("D"),
            ["session_id"] = null,
            ["created_at_utc"] = first.AddDays(-1).ToString("O"),
            ["expires_at_utc"] = null,
            ["released_at_utc"] = null
        };
        var capturedHold = RecoveryPurgeRunner.ReadHoldObservation(holdRow, "recovery", first);
        Require(capturedHold.HoldId == hold && capturedHold.ActiveAtSnapshot, "captured_recovery_hold_uses_actual_schema_id");
        foreach (var column in new[] { "created_at_utc", "expires_at_utc", "reason_code", "source_reference",
                     "recovery_point_id", "session_id", "owner_id", "library_id", "asset_id", "edit_id", "generation" })
        {
            var changed = new Dictionary<string, string?>(holdRow);
            changed[column] = column.EndsWith("_utc", StringComparison.Ordinal) ? first.ToString("O") : "changed";
            Require(RecoveryPurgeRunner.ReadHoldObservation(changed, "recovery", first).ScopeSha256 != capturedHold.ScopeSha256,
                "changed_hold_definition_cannot_inherit_release");
        }
        var primaryReleased = new Dictionary<string, string?>(holdRow) { ["released_at_utc"] = first.ToString("O") };
        Require(RecoveryPurgeRunner.ReadHoldObservation(primaryReleased, "recovery", first) == capturedHold,
            "primary_release_has_no_independent_authority");
        var missingCreation = new Dictionary<string, string?>(holdRow) { ["created_at_utc"] = null };
        try
        {
            _ = RecoveryPurgeRunner.ReadHoldObservation(missingCreation, "recovery", first);
            throw new InvalidOperationException("Recovery hold without captured creation was accepted");
        }
        catch (RecoveryWorkerException error) when (error.SafeCode == "purge_hold_definition_invalid") { }
        RecoveryRetentionPointObservationV1 Observe(DateTimeOffset time, string? state = "recoverable-deleted",
            long version = 2, long parentVersion = 0, string? content = null,
            IReadOnlyList<RecoveryRetentionHoldObservationV1>? holds = null) =>
            new(Guid.NewGuid(), time.AddMinutes(-1), time, other, other, "trusted-key", state,
                version, parentVersion, content ?? digest, first.AddYears(-10), holds ?? []);
        RecoveryRetentionHistoryProofV1 Verify(params RecoveryRetentionPointObservationV1[] points) =>
            RetentionService.VerifyHistory("owner", "library", asset, null, "original", digest,
                "trusted-key", points.Select(point => point.RecoveryPointId).ToArray(), points, now);
        var start = Observe(first);
        var end = Observe(now.AddMinutes(-1));
        var history = Verify(start, end);
        Require(history.EligibleAtUtc == first.AddDays(90), "primary_backdate_cannot_shorten_retention");
        Require(Verify(start, Observe(now.AddDays(-10), version: 3), end with { LifecycleVersion = 3 })
            .EligibleAtUtc == now.AddDays(80), "lifecycle_generation_resets_retention");
        Require(Verify(start, Observe(now.AddDays(-10), parentVersion: 4), end with { ParentLifecycleVersion = 4 })
            .EligibleAtUtc == now.AddDays(80), "parent_generation_resets_retention");
        Require(Verify(start, Observe(now.AddDays(-10), "restored"), end)
            .EligibleAtUtc == end.VerifiedAtUtc.AddDays(90), "restoration_resets_retention");
        var initialCurrent = Observe(first.AddDays(-1), "active", version: 1);
        Require(Verify(initialCurrent, start with { CurrentContentSha256 = other, LifecycleState = "active" },
            end with { CurrentContentSha256 = other, LifecycleState = "active" }).LatestState == "displaced",
            "displacement_requires_observed_target");
        Reject(() => Verify(start, end with { LifecycleState = null, LifecycleVersion = 0, CurrentContentSha256 = null }),
            "missing_identity_rejected");
        Reject(() => Verify(start, end with { SigningKeyId = "untrusted" }), "untrusted_checkpoint_rejected");
        Reject(() => Verify(start, end with { VerifiedAtUtc = now.AddMinutes(1) }), "future_checkpoint_rejected");
        Reject(() => RetentionService.VerifyHistory("owner", "library", asset, null, "original", digest,
            "trusted-key", [start.RecoveryPointId, end.RecoveryPointId], [start], now), "incomplete_history_rejected");
        var indefinite = new RecoveryRetentionHoldObservationV1(hold, "lifecycle", other, null, true);
        Require(Verify(start with { Holds = [indefinite] }, end).HasActiveHold,
            "removed_indefinite_hold_remains_protected");
        Require(Verify(start with { Holds = [indefinite with { Kind = "recovery", ExpiresAtUtc = first.AddDays(1) }] },
            end).HasActiveHold, "removed_recovery_hold_remains_protected");
        var expiring = indefinite with { ExpiresAtUtc = now.AddDays(-5) };
        Require(Verify(start with { Holds = [expiring] }, end).EligibleAtUtc == end.VerifiedAtUtc.AddDays(90),
            "hold_removal_observation_starts_retention");
        Reject(() => Verify(start with { Holds = [expiring] }, end with
        {
            Holds = [expiring with { ScopeSha256 = digest, ActiveAtSnapshot = false }]
        }), "hold_scope_mutation_rejected");
        var scope = RecoveryFenceScope.MediaScopeId(new("owner", "library", asset, null), "original", digest);
        var candidate = new RecoveryRetentionCandidateV1(scope, "exact-version", first.AddDays(90), true,
            false, 0, [], false, true, now.AddMinutes(5));
        RecoveryRetentionDecisionV1 Assess(RecoveryRetentionCandidateV1 item,
            RecoveryRetirementPurpose purpose = RecoveryRetirementPurpose.AuthorizedScopedPurge) =>
            RetentionService.Assess(item, purpose, now, history);
        Require(Assess(candidate, RecoveryRetirementPurpose.Routine).SafeCode == "final_recovery_copy",
            "routine_final_copy_preserved");
        Require(Assess(candidate).MayRetire, "qualified_exact_purge_can_remove_final_eligible_copy");
        Require(!Assess(candidate with { HasExactPurgeAuthorization = false }).MayRetire, "purge_needs_exact_approval");
        Require(!Assess(candidate with { PurgeApprovalExpiresAtUtc = now }).MayRetire, "expired_approval_rejected");
        Require(!Assess(candidate with { RetainedForSharedIdentity = true }).MayRetire, "shared_copy_preserved");
        Require(!Assess(candidate with { HasUnsettledWriter = true }).MayRetire, "writer_blocks_retirement");
        Require(!Assess(candidate with { ExactVersionVerified = false }).MayRetire, "unverified_copy_preserved");
        Require(!Assess(candidate with { ScopeId = "foreign" }).MayRetire, "foreign_scope_rejected");
        Require(!Assess(candidate with { Holds = [new("scope", "legal", first, null, first)] }).MayRetire,
            "primary_release_cannot_clear_indefinite_hold");
        Require(!RetentionService.Assess(candidate, RecoveryRetirementPurpose.AuthorizedScopedPurge, now).MayRetire,
            "no_history_no_retirement");
        var approvalPayload = new RecoveryPurgeApprovalV1(1, Guid.NewGuid(), Guid.NewGuid(), other, other,
            "owner", "library", asset, null, "original", digest, 1, "independent-mfa",
            now.AddMinutes(-5), now.AddMinutes(10));
        var approval = new VerifiedRecoveryPurgeApproval(approvalPayload,
            RecoveryEvidenceCodec.SerializeCanonical(approvalPayload), other, "owner-key", other, []);
        RecoveryPurgeRunner.RequireFreshApproval(approval, now);
        foreach (var checkAt in new[] { now.AddMinutes(-6), now.AddMinutes(10), now.AddMinutes(11) })
        {
            try { RecoveryPurgeRunner.RequireFreshApproval(approval, checkAt); }
            catch (RecoveryWorkerException exception) when (exception.SafeCode == "purge_approval_expired") { continue; }
            throw new InvalidOperationException("stale_or_future_mfa_approval_accepted");
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }

    private static void Reject(Action action, string code)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(code);
    }
}
