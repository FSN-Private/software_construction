using Npgsql;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Working;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;
using Platform.Ingest.Worker;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Operations;
using Platform.Api.Features.VersionControl.Rewrite;
using Platform.Api.Features.VersionControl.Visibility;
using Platform.Api.Features.VersionControl.MergePublishing;
using Platform.Api.Features.VersionControl.Integrity;
using Platform.Api.Features.VersionControl.Projection;
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Storage;
using NpgsqlTypes;
using System.Security.Cryptography;

internal static class CommitChecks
{
    public static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("VERSION_CONTROL_INTEGRATION") != "1")
        {
            Console.WriteLine("Set VERSION_CONTROL_INTEGRATION=1 for PostgreSQL atomicity/concurrency checks.");
            return;
        }

        const string baseConnection = "Host=127.0.0.1;Port=54329;Username=ingest;Password=ingest-test-only;Database=ingest;Include Error Detail=false;Command Timeout=180";
        var schema = "vc_test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(baseConnection);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = NpgsqlDataSource.Create(baseConnection + ";Search Path=" + schema);
            await ApplyMigrationsAsync(db);
            await SeedAsync(db, 100);
            var working = new WorkingService(db);
            await RejectCodeAsync(() => working.PutAsync("owner-1", "library-1", "provenance-client", new(Id(1), Id(1001), "main", "added", null, Digest(309), null, null, 0), "op_wrong_provenance", default), "content_not_authoritative");
            var ids = new List<Guid>();
            for (var index = 0; index < 100; index++)
            {
                var asset = Id(index + 1);
                var edit = Id(index + 1001);
                var result = await working.PutAsync("owner-1", "library-1", "client-1", new(asset, edit, "main", "added", null, Digest(index + 1), null, null, 0), "op_working", default);
                ids.Add(result.Id);
            }
            var stage = await working.StageAsync("owner-1", "library-1", "client-1", "main", new(ids, null, 0), "op_stage", default);
            var request = new CommitRequest(stage, "main", null, "Atomic 100-image commit", DateTimeOffset.Parse("2026-09-10T07:00:00+07:00"));
            var checkpoints = new[] { "ref-locked", "content-validated", "commit-inserted", "ref-advanced", "holds-published", "audit-recorded" };
            foreach (var checkpoint in checkpoints)
            {
                var service = new CommitService(db, TimeProvider.System, value => { if (value == checkpoint) throw new InjectedFailure(); });
                await RejectAsync<InjectedFailure>(() => service.PublishAsync("owner-1", "library-1", "client-1", request, "failure-" + checkpoint, "op_failure", default));
                await using var connection = await db.OpenConnectionAsync();
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commits", default))! == 0, $"Failure at {checkpoint} must roll back every commit row.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_refs", default))! == 0, $"Failure at {checkpoint} must roll back the ref advance.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes", default))! == 100, $"Failure at {checkpoint} must preserve working changes.");
            }

            var commits = new CommitService(db);
            var commitTimer = Stopwatch.StartNew();
            var published = await commits.PublishAsync("owner-1", "library-1", "client-1", request, "publish-once", "op_publish", default);
            commitTimer.Stop();
            Check((await new QueryService(db).GetAsync("owner-1", "library-1", published.CommitId!, default)).AuthoredAt == DateTimeOffset.Parse("2026-09-10T00:00:00Z"), "Authored timestamps must preserve the input instant when normalized to canonical UTC.");
            var liveProjection = new ProjectionService(db, new IntegrityService(db));
            Check((await liveProjection.AssetAsync("owner-1", "library-1", "main", Id(1), default)) is [{ ContentSha256: var projected }] && projected == Digest(1), "Commit publication must update the branch-state projection atomically.");
            for (var replay = 0; replay < 100; replay++)
            {
                var repeated = await commits.PublishAsync("owner-1", "library-1", "client-1", request, "publish-once", "op_replay", default);
                Check(repeated.CommitId == published.CommitId, "One hundred replays must return the original commit identity.");
            }
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commits", default))! == 1, "Replay must not duplicate graph state.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_reflog", default))! == 1, "Replay must not duplicate the branch transition.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commit_entries", default))! == 100, "The commit must publish every staged entry.");
            }
            using (var s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true }))
            {
                var assets = new AssetService(db, new S3ObjectStore(s3, "not-used", "not-used"));
                await RejectIngestCodeAsync(() => assets.ChangeAsync("owner-1", Id(1), false, "delete", 1, null, "held-delete", "op_held_delete", default), "retention_hold");
            }

            var drifted = await working.PutAsync("owner-1", "library-1", "drift-client", new(Id(20), Id(1020), "main", "modified", Digest(20), Digest(220), null, null, 0), "op_drift", default);
            var driftStage = await working.StageAsync("owner-1", "library-1", "drift-client", "main", new([drifted.Id], published.CommitId, 0), "op_drift_stage", default);
            _ = await working.PutAsync("owner-1", "library-1", "drift-client", new(Id(20), Id(1020), "main", "modified", Digest(20), Digest(221), null, null, 1), "op_drift_update", default);
            await RejectCodeAsync(() => commits.PublishAsync("owner-1", "library-1", "drift-client", new(driftStage, "main", published.CommitId, "Drifted stage", DateTimeOffset.UtcNow), "drift-publish", "op_drift_publish", default), "staging_conflict");
            var staleBase = await working.PutAsync("owner-1", "library-1", "base-client", new(Id(21), Id(1021), "main", "modified", Digest(22), Digest(222), null, null, 0), "op_bad_base", default);
            var staleStage = await working.StageAsync("owner-1", "library-1", "base-client", "main", new([staleBase.Id], published.CommitId, 0), "op_bad_base_stage", default);
            await RejectCodeAsync(() => commits.PublishAsync("owner-1", "library-1", "base-client", new(staleStage, "main", published.CommitId, "Stale base", DateTimeOffset.UtcNow), "bad-base-publish", "op_bad_base_publish", default), "stale_working_base");

            var expectedHead = published.CommitId!;
            var attempts = new List<(string Client, CommitRequest Request)>();
            for (var index = 0; index < 10; index++)
            {
                var client = "racer-" + index;
                var changed = await working.PutAsync("owner-1", "library-1", client, new(Id(index + 1), Id(index + 1001), "main", "modified", Digest(index + 1), Digest(200 + index), null, null, 0), "op_race_working", default);
                var staged = await working.StageAsync("owner-1", "library-1", client, "main", new([changed.Id], expectedHead, 0), "op_race_stage", default);
                attempts.Add((client, new(staged, "main", expectedHead, "Concurrent edit " + index, DateTimeOffset.Parse("2026-09-10T00:01:00Z"))));
            }
            var outcomes = await Task.WhenAll(attempts.Select(async (attempt, index) =>
            {
                try { await commits.PublishAsync("owner-1", "library-1", attempt.Client, attempt.Request, "race-" + index, "op_race", default); return "winner"; }
                catch (VersionControlProblem problem)
                {
                    if (problem.Code == "stale_head" && (!problem.Extensions.ContainsKey("baseCommitId") || !problem.Extensions.ContainsKey("oursCommitId") || !problem.Extensions.ContainsKey("theirs"))) return "stale_head_without_evidence";
                    return problem.Code;
                }
            }));
            Check(outcomes.Count(value => value == "winner") == 1 && outcomes.Count(value => value == "stale_head") == 9, "Ten expected-head attempts must produce one winner and nine stale heads.");
            string mainHead;
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes WHERE client_id LIKE 'racer-%'", default))! == 9, "Stale writers must retain their working XMP identities.");
                mainHead = ((string)(await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE normalized_name='main'", default))!).Trim();
            }

            var refs = new RefService(db);
            Check((await refs.ListAsync("owner-1", "library-1", default)) is [{ Name: "main", Protected: true }], "The default branch must be protected and exactly resolvable.");
            await RejectCodeAsync(() => refs.DeleteAsync("owner-1", "library-1", "main", new("main", ExpectedVersion: 3, Confirm: true), "delete-main", "op_protected", default), "protected_ref");
            var tag = await refs.CreateAsync("owner-1", "library-1", "tag", new("release-1", published.CommitId), "create-tag", "op_tag", default);
            Check((await refs.CreateAsync("owner-1", "library-1", "tag", new("release-1", published.CommitId), "create-tag", "op_tag_replay", default)).CommitId == tag.CommitId, "Committed ref creation must replay its original outcome.");
            await RejectCodeAsync(() => refs.RenameAsync("owner-1", "library-1", tag.Name, new("release-renamed", ExpectedVersion: 1), "rename-tag", "op_tag_rename", default), "protected_ref");
            await refs.CreateAsync("owner-1", "library-1", "branch", new("Experiment", mainHead), "create-experiment", "op_branch", default);
            await using (var connection = await db.OpenConnectionAsync())
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_reflog WHERE operation='create'", default))! == 2, "Every branch/tag creation must append immutable reflog evidence.");
            var renamed = await refs.RenameAsync("owner-1", "library-1", "experiment", new("lab", ExpectedVersion: 1), "rename-experiment", "op_rename", default);
            Check((await refs.RenameAsync("owner-1", "library-1", "experiment", new("lab", ExpectedVersion: 1), "rename-experiment", "op_rename_replay", default)).Name == "lab", "Committed rename must replay after the source name no longer exists.");
            Check(renamed.Name == "lab" && renamed.CommitId == mainHead, "Branch rename must preserve exact head identity.");
            await refs.CreateAsync("owner-1", "library-1", "branch", new("delete/me", mainHead), "create-delete-me", "op_delete_branch", default);
            var deleted = await refs.DeleteAsync("owner-1", "library-1", "delete/me", new("delete/me", ExpectedVersion: 1, Confirm: true), "delete-me", "op_delete", default);
            var deletedReplay = await refs.DeleteAsync("owner-1", "library-1", "delete/me", new("delete/me", ExpectedVersion: 1, Confirm: true), "delete-me", "op_delete_replay", default);
            Check(deleted.RecoveryId == deletedReplay.RecoveryId, "Committed ref deletion must replay its recovery identifier after the ref is gone.");
            Check((await new RewriteService(db).ListAsync("owner-1", "library-1", default)).Any(item => item.Id == deleted.RecoveryId), "Reflog listing must expose the recovery identifier returned by deletion.");
            await refs.CreateAsync("owner-1", "library-1", "branch", new("dirty/branch", mainHead), "create-dirty-branch", "op_dirty_branch", default);
            await working.PutAsync("owner-1", "library-1", "dirty-rename-client", new(Id(80), Id(1080), "dirty/branch", "modified", Digest(80), Digest(281), null, null, 0), "op_dirty_rename", default);
            await RejectCodeAsync(() => refs.RenameAsync("owner-1", "library-1", "dirty/branch", new("renamed-dirty", ExpectedVersion: 1), "rename-dirty", "op_rename_dirty", default), "ref_in_use");

            var queries = new QueryService(db);
            Check((await queries.GetAsync("owner-1", "library-1", published.CommitId!, default)).CommitId == published.CommitId, "Direct commit lookup must preserve hidden or unreachable identities.");
            Check((await queries.ProjectAsync("owner-1", "library-1", "dirty/branch", null, false, 0, 100, default)).Count > 0, "Slash-containing ref names must resolve through the graph contract.");
            var comparison = await queries.CompareAsync("owner-1", "library-1", published.CommitId!, mainHead, default);
            Check(comparison.BaseCommitId == published.CommitId && comparison.Changes.Count == 1, "Database divergence must expose the shared base and exact changed identity.");
            Check((await queries.ProjectAsync("owner-1", "library-1", "main", null, false, 0, 100, default)).Count == 2, "Whole-library projection must traverse both commits.");

            var operations = new OperationService(db, queries, commits);
            await refs.CreateAsync("owner-1", "library-1", "branch", new("rename/race", mainHead), "create-rename-race", "op_create_rename_race", default);
            var checkoutRefLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCheckout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racingOperations = new OperationService(db, queries, commits, async (point, _) =>
            {
                if (point == "target-ref-locked")
                {
                    checkoutRefLocked.SetResult();
                    await releaseCheckout.Task;
                }
            });
            var racingCheckout = racingOperations.CheckoutAsync("owner-1", "library-1", "rename-race-client", new("rename/race", "block"), "rename-race-checkout", "op_rename_race_checkout", default);
            await checkoutRefLocked.Task;
            var racingRename = refs.RenameAsync("owner-1", "library-1", "rename/race", new("rename/race-moved", ExpectedVersion: 1), "rename-race-move", "op_rename_race_move", default);
            await Task.Delay(100);
            Check(!racingRename.IsCompleted, "Branch rename must wait while checkout owns the target-ref fence.");
            releaseCheckout.SetResult();
            await racingCheckout;
            await RejectCodeAsync(() => racingRename, "ref_in_use");
            Task<WorkingChangeView> blockedWorking;
            await using (var lockConnection = await db.OpenConnectionAsync())
            await using (var lockTransaction = await lockConnection.BeginTransactionAsync())
            {
                await WorkingService.LockClientAsync(lockConnection, "owner-1", "library-1", "lock-client", default);
                blockedWorking = working.PutAsync("owner-1", "library-1", "lock-client", new(Id(82), Id(1082), "main", "modified", Digest(82), Digest(283), null, null, 0), "op_lock_client", default);
                await Task.Delay(100);
                Check(!blockedWorking.IsCompleted, "Working mutations must share checkout's client fence.");
                await lockTransaction.RollbackAsync();
            }
            await blockedWorking;
            var dirtyClient = "racer-" + Array.IndexOf(outcomes, "stale_head");
            await RejectCodeAsync(() => operations.CheckoutAsync("owner-1", "library-1", dirtyClient, new("lab", "block"), "checkout-block", "op_checkout_block", default), "dirty_checkout");
            var preserved = await operations.CheckoutAsync("owner-1", "library-1", dirtyClient, new("lab", "preserve"), "checkout-preserve", "op_checkout_preserve", default);
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_stashes WHERE client_id=$1", default, dirtyClient))! == 1, "Preserve checkout must retain dirty state in a stash.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_working_changes WHERE client_id=$1", default, dirtyClient))! == 0, "Preserve checkout must clear only the selected client's working state.");
            }
            Check(preserved.StashId is not null && (await operations.ListStashesAsync("owner-1", "library-1", dirtyClient, default)).Any(stash => stash.Id == preserved.StashId), "Preserve must return a retrievable stash identifier.");
            await operations.RestoreStashAsync("owner-1", "library-1", dirtyClient, preserved.StashId!.Value, "restore-stash", "op_restore_stash", default);
            Check((await working.ListAsync("owner-1", "library-1", dirtyClient, "main", default)).Count > 0, "Stash restoration must recover the exact working rows.");
            await RejectCodeAsync(() => operations.CheckoutAsync("owner-1", "library-1", dirtyClient, new("main", "block", ExpectedVersion: 0), "checkout-stale", "op_checkout_stale", default), "checkout_conflict");
            Check((await operations.CheckoutAsync("owner-1", "library-1", dirtyClient, new("main", "preserve", ExpectedVersion: 1), "checkout-switch", "op_checkout_switch", default)).Version == 2, "Checkout compare-and-swap must preserve restored work and advance its version exactly once.");

            var restored = await operations.RestoreAsync("owner-1", "library-1", "restore-client", new("main", published.CommitId!, "restore", "Restore initial library state", mainHead), "restore-key", "op_restore", default);
            Check((await operations.RestoreAsync("owner-1", "library-1", "restore-client", new("main", published.CommitId!, "restore", "Restore initial library state", mainHead), "restore-key", "op_restore_replay", default)).CommitId == restored.CommitId, "Restore must replay before inspecting the now-advanced branch.");
            var reverted = await operations.RestoreAsync("owner-1", "library-1", "revert-client", new("main", restored.CommitId!, "revert", "Revert restoration", restored.CommitId), "revert-key", "op_revert", default);
            Check(restored.CommitId != published.CommitId && reverted.CommitId != restored.CommitId, "Restore and revert must create new reachable commits.");
            var firstLater = await working.PutAsync("owner-1", "library-1", "non-tip-a", new(Id(80), Id(1080), "main", "modified", Digest(80), Digest(281), null, null, 0), "op_non_tip_a", default);
            var firstLaterSet = await working.StageAsync("owner-1", "library-1", "non-tip-a", "main", new([firstLater.Id], reverted.CommitId, 0), "op_non_tip_a_stage", default);
            var firstLaterCommit = await commits.PublishAsync("owner-1", "library-1", "non-tip-a", new(firstLaterSet, "main", reverted.CommitId, "First independent edit", DateTimeOffset.Parse("2026-09-10T00:01:10Z")), "non-tip-a", "op_non_tip_a_commit", default);
            var secondLater = await working.PutAsync("owner-1", "library-1", "non-tip-b", new(Id(81), Id(1081), "main", "modified", Digest(81), Digest(282), null, null, 0), "op_non_tip_b", default);
            var secondLaterSet = await working.StageAsync("owner-1", "library-1", "non-tip-b", "main", new([secondLater.Id], firstLaterCommit.CommitId, 0), "op_non_tip_b_stage", default);
            var secondLaterCommit = await commits.PublishAsync("owner-1", "library-1", "non-tip-b", new(secondLaterSet, "main", firstLaterCommit.CommitId, "Second independent edit", DateTimeOffset.Parse("2026-09-10T00:01:11Z")), "non-tip-b", "op_non_tip_b_commit", default);
            await RejectCodeAsync(() => operations.RestoreAsync("owner-1", "library-1", "stale-restore", new("main", published.CommitId!, "restore", "Stale restore", firstLaterCommit.CommitId), "stale-restore", "op_stale_restore", default), "stale_head");
            var nonTipRevert = await operations.RestoreAsync("owner-1", "library-1", "non-tip-revert", new("main", firstLaterCommit.CommitId!, "revert", "Revert first only", secondLaterCommit.CommitId), "non-tip-revert", "op_non_tip_revert", default);
            Check((await new QueryService(db).StateAsync("owner-1", "library-1", nonTipRevert.CommitId!, default)).State.Values.Any(value => value.AssetId == Id(81) && value.ContentSha256 == Digest(282)), "Non-tip revert must preserve unrelated later changes.");

            var visibility = new VisibilityService(db);
            await visibility.SetAsync("owner-1", "library-1", new(restored.CommitId!, true), "op_hide", default);
            Check(!(await queries.ProjectAsync("owner-1", "library-1", "main", null, false, 0, 100, default)).Any(commit => commit.CommitId == restored.CommitId), "Normal projections must omit hidden commits.");
            Check((await queries.ProjectAsync("owner-1", "library-1", "main", null, true, 0, 100, default)).Any(commit => commit.CommitId == restored.CommitId), "Show-hidden projections must preserve direct hidden history.");
            await visibility.SetAsync("owner-1", "library-1", new(restored.CommitId!, false), "op_unhide", default);

            await refs.CreateAsync("owner-1", "library-1", "branch", new("rewrite-test", reverted.CommitId), "create-rewrite", "op_rewrite_branch", default);
            var rewriteWorking = await working.PutAsync("owner-1", "library-1", "rewrite-client", new(Id(50), Id(1050), "rewrite-test", "modified", Digest(50), Digest(250), null, null, 0), "op_rewrite_working", default);
            var rewriteStage = await working.StageAsync("owner-1", "library-1", "rewrite-client", "rewrite-test", new([rewriteWorking.Id], reverted.CommitId, 0), "op_rewrite_stage", default);
            var rewriteCommit = await commits.PublishAsync("owner-1", "library-1", "rewrite-client", new(rewriteStage, "rewrite-test", reverted.CommitId, "Disposable branch edit", DateTimeOffset.Parse("2026-09-10T00:02:00Z")), "rewrite-commit", "op_rewrite_commit", default);
            var rewriteClock = new FixedClock(DateTimeOffset.Parse("2026-09-10T00:00:00Z"));
            var rewrite = new RewriteService(db, clock: rewriteClock);
            var preview = await rewrite.PreviewAsync("owner-1", "library-1", "rewrite-test", reverted.CommitId!, default);
            Check(preview.DisplacedCommitIds.SequenceEqual([rewriteCommit.CommitId!]), "Rewrite preview must identify only the branch-exclusive commit.");
            await rewrite.ApplyAsync("owner-1", "library-1", new("rewrite-test", reverted.CommitId!, preview.ConfirmationToken, true), "rewrite-apply", "op_rewrite", default);
            Check((await liveProjection.AssetAsync("owner-1", "library-1", "rewrite-test", Id(50), default)) is [{ ContentSha256: var rewritten }] && rewritten == Digest(50), "A ref rewrite must replace its materialized branch state.");
            Guid rewriteReflog;
            await using (var connection = await db.OpenConnectionAsync())
            {
                rewriteReflog = (Guid)(await ScalarAsync(connection, "SELECT id FROM vc_reflog WHERE operation='rewrite'", default))!;
                Check((DateTime)(await ScalarAsync(connection, "SELECT expires_at FROM lifecycle_holds WHERE source_reference=$1 LIMIT 1", default, "vc:" + rewriteCommit.CommitId))! == rewriteClock.GetUtcNow().UtcDateTime.AddDays(90), "Displaced content holds must use the exact 90-day deadline.");
            }
            var recovered = await new RewriteService(db, clock: new FixedClock(rewriteClock.GetUtcNow().AddDays(90))).RecoverAsync("owner-1", "library-1", rewriteReflog, "recovered-edit", "recover-edit", "op_recover", default);
            Check(recovered.CommitId == rewriteCommit.CommitId, "Day-90 recovery must restore the identical immutable commit.");
            var recoveredWorking = await working.PutAsync("owner-1", "library-1", "recovered-client", new(Id(50), Id(1050), "recovered-edit", "modified", Digest(250), Digest(251), null, null, 0), "op_recovered_working", default);
            var recoveredStage = await working.StageAsync("owner-1", "library-1", "recovered-client", "recovered-edit", new([recoveredWorking.Id], recovered.CommitId, 0), "op_recovered_stage", default);
            await commits.PublishAsync("owner-1", "library-1", "recovered-client", new(recoveredStage, "recovered-edit", recovered.CommitId, "Advance recovered branch", DateTimeOffset.Parse("2026-09-10T00:02:01Z")), "recovered-child", "op_recovered_child", default);

            await new RewriteService(db, clock: new FixedClock(rewriteClock.GetUtcNow().AddDays(90).AddSeconds(1))).ExpireAsync("owner-1", "library-1", default);
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check(await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE normalized_name=$1", default, $"recovery/{rewriteReflog:N}") is null, "Expired generated recovery refs must be removed.");
                Check(await ScalarAsync(connection, "SELECT expires_at FROM lifecycle_holds WHERE source_reference=$1 LIMIT 1", default, "vc:" + rewriteCommit.CommitId) is DBNull, "A recovered named branch must retain content beyond reflog expiry.");
            }
            Check((await new RewriteService(db, clock: new FixedClock(rewriteClock.GetUtcNow().AddDays(90).AddSeconds(1))).RecoverAsync("owner-1", "library-1", rewriteReflog, "recovered-edit", "recover-edit", "op_recover_replay", default)).CommitId == recovered.CommitId, "Committed recovery must replay after its reflog deadline expires.");

            var mergeBase = nonTipRevert.CommitId!;
            await refs.CreateAsync("owner-1", "library-1", "branch", new("merge-source", mergeBase), "create-merge-source", "op_merge_branch", default);
            var sourceWorking = await working.PutAsync("owner-1", "library-1", "source-client", new(Id(60), Id(1060), "merge-source", "modified", Digest(60), Digest(260), null, null, 0), "op_source_working", default);
            var sourceConflict = await working.PutAsync("owner-1", "library-1", "source-client", new(Id(62), Id(1062), "merge-source", "modified", Digest(62), Digest(263), null, null, 0), "op_source_conflict", default);
            var sourceStage = await working.StageAsync("owner-1", "library-1", "source-client", "merge-source", new([sourceWorking.Id, sourceConflict.Id], mergeBase, 0), "op_source_stage", default);
            var sourceCommit = await commits.PublishAsync("owner-1", "library-1", "source-client", new(sourceStage, "merge-source", mergeBase, "Source edit", DateTimeOffset.Parse("2026-09-10T00:03:00Z")), "source-commit", "op_source_commit", default);
            var targetWorking = await working.PutAsync("owner-1", "library-1", "target-client", new(Id(61), Id(1061), "main", "modified", Digest(61), Digest(261), null, null, 0), "op_target_working", default);
            var targetConflict = await working.PutAsync("owner-1", "library-1", "target-client", new(Id(62), Id(1062), "main", "modified", Digest(62), Digest(264), null, null, 0), "op_target_conflict", default);
            var targetStage = await working.StageAsync("owner-1", "library-1", "target-client", "main", new([targetWorking.Id, targetConflict.Id], mergeBase, 0), "op_target_stage", default);
            var targetCommit = await commits.PublishAsync("owner-1", "library-1", "target-client", new(targetStage, "main", mergeBase, "Target edit", DateTimeOffset.Parse("2026-09-10T00:03:01Z")), "target-commit", "op_target_commit", default);
            var resolvedWorking = await working.PutAsync("owner-1", "library-1", "merge-client", new(Id(60), Id(1060), "main", "modified", Digest(60), Digest(262), null, null, 0), "op_resolved_working", default);
            var incompleteStage = await working.StageAsync("owner-1", "library-1", "merge-client", "main", new([resolvedWorking.Id], targetCommit.CommitId, 0), "op_incomplete_merge_stage", default);
            var incompleteMerge = new MergeRequest(incompleteStage, "main", targetCommit.CommitId!, sourceCommit.CommitId!, mergeBase, "Incomplete merge", DateTimeOffset.Parse("2026-09-10T00:03:02Z"));
            await RejectCodeAsync(() => new MergeService(queries, commits).PublishAsync("owner-1", "library-1", "merge-client", incompleteMerge, "merge-incomplete", "op_merge_incomplete", default), "unresolved_divergence");
            await working.UnstageAsync("owner-1", "library-1", incompleteStage, resolvedWorking.Id, 1, "op_incomplete_unstage", default);
            var resolvedConflict = await working.PutAsync("owner-1", "library-1", "merge-client", new(Id(62), Id(1062), "main", "modified", Digest(264), Digest(265), null, null, 0), "op_resolved_conflict", default);
            var resolvedStage = await working.StageAsync("owner-1", "library-1", "merge-client", "main", new([resolvedWorking.Id, resolvedConflict.Id], targetCommit.CommitId, 0), "op_resolved_stage", default);
            var mergeRequest = new MergeRequest(resolvedStage, "main", targetCommit.CommitId!, sourceCommit.CommitId!, mergeBase, "Resolved merge", DateTimeOffset.Parse("2026-09-10T00:03:02Z"));
            var faultingMerge = new MergeService(queries, new CommitService(db, TimeProvider.System, value => { if (value == "holds-published") throw new InjectedFailure(); }));
            await RejectAsync<InjectedFailure>(() => faultingMerge.PublishAsync("owner-1", "library-1", "merge-client", mergeRequest, "merge-failure", "op_merge_failure", default));
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check(((string)(await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE normalized_name='main'", default))!).Trim() == targetCommit.CommitId, "Failed merge publication must leave the target ref unchanged.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_divergence WHERE state='resolved'", default))! == 0, "Failed merge publication must leave no resolved evidence row.");
            }
            var merged = await new MergeService(queries, commits).PublishAsync("owner-1", "library-1", "merge-client", mergeRequest, "merge-success", "op_merge", default);
            await using (var connection = await db.OpenConnectionAsync())
            {
                await using var parentCommand = Command(connection, "SELECT parent_id FROM vc_commit_parents WHERE commit_id=$1 ORDER BY ordinal", merged.CommitId);
                var parents = new List<string>();
                await using var reader = await parentCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync()) parents.Add(reader.GetString(0).Trim());
                Check(parents.SequenceEqual([targetCommit.CommitId!, sourceCommit.CommitId!]), "Resolved merge must retain ordered target/source parents.");
            }

            var integrity = new IntegrityService(db);
            Check((await integrity.VerifyAsync("owner-1", "library-1", default)).Healthy, "Persisted graph/content evidence must pass integrity verification.");
            await NormalizedPageIndexConstraintsAsync(db, published.CommitId!, merged.CommitId!);
            await ConcurrentIntegrityPublicationAsync(db);
            await WorkerStorageFailureAsync(db);
            var projections = new ProjectionService(db, integrity);
            Check(await projections.RebuildAsync("owner-1", "library-1", default) > 0, "Derived state must rebuild solely from immutable graph evidence.");
            var concurrentWorking = await working.PutAsync("owner-1", "library-1", "rebuild-client", new(Id(70), Id(1070), "main", "modified", Digest(70), Digest(271), null, null, 0), "op_rebuild_working", default);
            var concurrentStage = await working.StageAsync("owner-1", "library-1", "rebuild-client", "main", new([concurrentWorking.Id], merged.CommitId, 0), "op_rebuild_stage", default);
            var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRebuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedRebuild = new ProjectionService(db, integrity, TimeProvider.System, async (point, _) => { if (point == "snapshot-captured") { snapshotCaptured.SetResult(); await releaseRebuild.Task; } });
            var rebuildTask = blockedRebuild.RebuildAsync("owner-1", "library-1", default);
            await snapshotCaptured.Task;
            var concurrentCommit = commits.PublishAsync("owner-1", "library-1", "rebuild-client", new(concurrentStage, "main", merged.CommitId, "Concurrent with rebuild", DateTimeOffset.Parse("2026-09-10T00:04:00Z")), "rebuild-concurrent", "op_rebuild_concurrent", default);
            await Task.Delay(100);
            Check(!concurrentCommit.IsCompleted, "Ref publication must wait while rebuild owns the projection snapshot fence.");
            releaseRebuild.SetResult();
            await Task.WhenAll(rebuildTask, concurrentCommit);
            Check((await projections.AssetAsync("owner-1", "library-1", "main", Id(70), default)) is [{ ContentSha256: var rebuiltConcurrent }] && rebuiltConcurrent == Digest(271), "Concurrent publication after rebuild must remain in materialized state.");
            var inventory = await projections.InventoryAsync("owner-1", "library-1", default);
            Check(inventory.CommitRows >= 8 && inventory.EntryRows >= 100 && inventory.ProjectionRows > 0 && inventory.UniqueXmpBytes > 0, "Cost inventory must distinguish graph, projection, and unique XMP storage.");
            var assetLatencies = new List<double>();
            for (var sample = 0; sample < 20; sample++)
            {
                var timer = Stopwatch.StartNew();
                Check((await projections.AssetAsync("owner-1", "library-1", "main", Id(1), default)).Count == 1, "Materialized branch state must resolve exact per-asset content.");
                timer.Stop();
                assetLatencies.Add(timer.Elapsed.TotalMilliseconds);
            }
            Check(assetLatencies.Order().ElementAt(18) <= 200, "Per-asset branch state p95 must be at most 200 ms.");
            if (Environment.GetEnvironmentVariable("VERSION_CONTROL_CAPACITY") == "1") await PersistedLineageCapacityAsync(db);
            await PhysicalObjectIntegrityAsync(db);
            await using (var connection = await db.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await RejectAsync<PostgresException>(async () => { await ExecuteAsync(connection, "UPDATE vc_commits SET message='mutated' WHERE id=$1", default, merged.CommitId); });
                await transaction.RollbackAsync();
            }

            var corruptManifest = new VersionControlManifest("library-1", [], "owner-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Non-canonical fixture", [new(Id(1), Id(1001), "modified", Digest(1), Digest(2))]);
            var corruptBytes = new byte[] { (byte)' ' }.Concat(VersionControlManifestCodec.Write(corruptManifest)).ToArray();
            var corruptId = VersionControlManifestCodec.Digest(corruptBytes);
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner-1','library-1','owner-1',$2,$2,'Non-canonical fixture',1,1,$3)", default, corruptId, DateTimeOffset.UnixEpoch, corruptBytes);
            Check((await refs.CreateAsync("owner-1", "library-1", "tag", new("release-1", published.CommitId), "create-tag", "op_tag_replay_after_corruption", default)).CommitId == tag.CommitId, "Committed ref creation must replay before later graph corruption is inspected.");
            Check((await rewrite.ApplyAsync("owner-1", "library-1", new("rewrite-test", reverted.CommitId!, preview.ConfirmationToken, true), "rewrite-apply", "op_rewrite_replay_after_corruption", default)).CommitId == reverted.CommitId, "Committed rewrite must replay before later graph corruption is inspected.");
            Check((await new MergeService(queries, commits).PublishAsync("owner-1", "library-1", "merge-client", mergeRequest, "merge-success", "op_merge_replay_after_corruption", default)).CommitId == merged.CommitId, "Committed merge must replay before later graph corruption or consumed staging is inspected.");
            await RejectCodeAsync(() => refs.CreateAsync("owner-1", "library-1", "branch", new("blocked-by-integrity", mainHead), "blocked-ref", "op_corrupt_ref", default), "graph_corrupt");
            Check(!(await integrity.VerifyAsync("owner-1", "library-1", default)).Healthy, "Ref advancement must verify and quarantine persisted non-canonical corruption without a preceding manual verification request.");

            Console.WriteLine($"PostgreSQL checks passed: {commitTimer.Elapsed.TotalMilliseconds:F0} ms 100-entry commit, retries/CAS, staged/base drift rejection, refs, divergence, checkout, restore/revert, visibility, rewrite/recovery, holds, merge atomicity, rebuild, and {assetLatencies.Order().ElementAt(18):F1} ms asset-state p95.");
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task ApplyMigrationsAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using (var rollback = await connection.BeginTransactionAsync())
        {
            foreach (var file in new[] { "005_ingest_storage.sql", "005_ingest_workflows.sql", "006_xmp_version_control.sql", "007_windows_cloud_files_sync.sql", "008_windows_sync_lifecycle_revision.sql" }) await ExecuteAsync(connection, await File.ReadAllTextAsync("apps/api/Database/Migrations/" + file), default);
            await rollback.RollbackAsync();
        }
        var rolledBackTable = await ScalarAsync(connection, "SELECT to_regclass(current_schema()||'.vc_commits')::text", default);
        Check(rolledBackTable is null or DBNull, "Injected migration rollback must leave no partial Feature 006 schema.");
        await DatabaseMigrator.ApplyAsync(db, default);
        await DatabaseMigrator.ApplyAsync(db, default);
        Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM platform_schema_migrations", default))! == 5, "Repeated migration must retain one checksum record per release migration.");
    }

    private static async Task SeedAsync(NpgsqlDataSource db, int count)
    {
        await using var connection = await db.OpenConnectionAsync();
        for (var index = 0; index < count + 210; index++)
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,1,'xmp',$2,'v1','authoritative')", default, Digest(index + 1), "sha256/" + Digest(index + 1));
        for (var index = 0; index < count; index++)
        {
            var asset = Id(index + 1);
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,state,format) VALUES($1,'owner-1','active','dng')", default, asset);
            await ExecuteAsync(connection, "INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($1,$2,0,$3,'fixture')", default, Id(index + 1001), asset, Digest(index + 1));
        }
        var batch = Id(9000);
        await ExecuteAsync(connection, "INSERT INTO import_batches(id,owner_id,idempotency_key,atomic,state) VALUES($1,'owner-1','vc-provenance',true,'completed')", default, batch);
        var associations = Enumerable.Range(0, 10).Select(index => (Asset: Id(index + 1), Edit: Id(index + 1001), Digest: Digest(200 + index))).Concat(new (Guid Asset, Guid Edit, string Digest)[]
        {
            (Id(20), Id(1020), Digest(220)), (Id(20), Id(1020), Digest(221)), (Id(21), Id(1021), Digest(222)),
            (Id(21), Id(1021), Digest(22)),
            (Id(50), Id(1050), Digest(250)), (Id(50), Id(1050), Digest(251)), (Id(60), Id(1060), Digest(260)), (Id(60), Id(1060), Digest(262)), (Id(61), Id(1061), Digest(261)), (Id(62), Id(1062), Digest(263)), (Id(62), Id(1062), Digest(264)), (Id(62), Id(1062), Digest(265)), (Id(70), Id(1070), Digest(271)), (Id(80), Id(1080), Digest(281)), (Id(81), Id(1081), Digest(282)), (Id(82), Id(1082), Digest(283)),
        }).ToArray();
        for (var index = 0; index < associations.Length; index++)
        {
            var item = associations[index];
            await ExecuteAsync(connection, """
                INSERT INTO upload_sessions(id,batch_id,owner_id,target_key,expected_sha256,expected_length,state,expires_at,category,asset_id,edit_id,verified_at)
                VALUES($1,$2,'owner-1',$3,$4,1,'completed',$5,'xmp',$6,$7,$5)
                """, default, Id(9100 + index), batch, "vc-provenance/" + item.Digest, item.Digest, DateTimeOffset.UtcNow.AddYears(1), item.Asset, item.Edit);
        }
    }

    private static async Task PersistedLineageCapacityAsync(NpgsqlDataSource db)
    {
        const string owner = "capacity-owner";
        const string library = "capacity-library";
        const int commitCount = 10_000;
        const int entriesPerCommit = 100;
        var assets = Enumerable.Range(0, entriesPerCommit).Select(index => Id(50_000 + index)).ToArray();
        var edits = Enumerable.Range(0, entriesPerCommit).Select(index => Id(60_000 + index)).ToArray();
        var commitIds = new List<string>(commitCount);
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var writer = await connection.BeginBinaryImportAsync("COPY assets(id,owner_id,state,format) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var asset in assets) { await writer.StartRowAsync(); await writer.WriteAsync(asset, NpgsqlDbType.Uuid); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync("active", NpgsqlDbType.Text); await writer.WriteAsync("dng", NpgsqlDbType.Text); }
            await writer.CompleteAsync();
        }
        await using (var writer = await connection.BeginBinaryImportAsync("COPY edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < entriesPerCommit; index++) { await writer.StartRowAsync(); await writer.WriteAsync(edits[index], NpgsqlDbType.Uuid); await writer.WriteAsync(assets[index], NpgsqlDbType.Uuid); await writer.WriteAsync(0, NpgsqlDbType.Integer); await writer.WriteAsync(Digest(301), NpgsqlDbType.Char); await writer.WriteAsync("capacity", NpgsqlDbType.Text); }
            await writer.CompleteAsync();
        }
        var batch = Id(290_000);
        await ExecuteAsync(connection, "INSERT INTO import_batches(id,owner_id,idempotency_key,atomic,state) VALUES($1,$2,'capacity-provenance',true,'completed')", default, batch, owner);
        await using (var writer = await connection.BeginBinaryImportAsync("COPY upload_sessions(id,batch_id,owner_id,target_key,expected_sha256,expected_length,state,expires_at,category,asset_id,edit_id,verified_at) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < entriesPerCommit; index++)
            {
                foreach (var digest in new[] { Digest(300), Digest(301) })
                {
                    var at = DateTimeOffset.UtcNow.AddYears(1);
                    await writer.StartRowAsync(); await writer.WriteAsync(Id(300_000 + index * 2 + (digest == Digest(301) ? 1 : 0)), NpgsqlDbType.Uuid); await writer.WriteAsync(batch, NpgsqlDbType.Uuid); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync($"capacity/{assets[index]:N}/{digest}", NpgsqlDbType.Text); await writer.WriteAsync(digest, NpgsqlDbType.Char); await writer.WriteAsync(1L, NpgsqlDbType.Bigint); await writer.WriteAsync("completed", NpgsqlDbType.Text); await writer.WriteAsync(at, NpgsqlDbType.TimestampTz); await writer.WriteAsync("xmp", NpgsqlDbType.Text); await writer.WriteAsync(assets[index], NpgsqlDbType.Uuid); await writer.WriteAsync(edits[index], NpgsqlDbType.Uuid); await writer.WriteAsync(at, NpgsqlDbType.TimestampTz);
                }
            }
            await writer.CompleteAsync();
        }
        string? parent = null;
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < commitCount; index++)
            {
                var baseDigest = index == 0 ? null : Digest(300 + (index - 1) % 2);
                var contentDigest = Digest(300 + index % 2);
                var entries = Enumerable.Range(0, entriesPerCommit).Select(ordinal => new VersionControlEntry(assets[ordinal], edits[ordinal], index == 0 ? "added" : "modified", baseDigest, contentDigest)).ToArray();
                var at = DateTimeOffset.UnixEpoch.AddSeconds(index);
                var manifest = new VersionControlManifest(library, parent is null ? [] : [parent], owner, at, at, "Persisted capacity " + index, entries);
                var bytes = VersionControlManifestCodec.Write(manifest);
                var id = VersionControlManifestCodec.Digest(bytes);
                await writer.StartRowAsync();
                await writer.WriteAsync(id, NpgsqlDbType.Char); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync(library, NpgsqlDbType.Text); await writer.WriteAsync(owner, NpgsqlDbType.Text);
                await writer.WriteAsync(at, NpgsqlDbType.TimestampTz); await writer.WriteAsync(at, NpgsqlDbType.TimestampTz); await writer.WriteAsync(manifest.Message, NpgsqlDbType.Text);
                await writer.WriteAsync(1, NpgsqlDbType.Integer); await writer.WriteAsync(1, NpgsqlDbType.Integer); await writer.WriteAsync(bytes, NpgsqlDbType.Bytea);
                commitIds.Add(id);
                parent = id;
            }
            await writer.CompleteAsync();
        }
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commit_parents(commit_id,ordinal,parent_id) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 1; index < commitIds.Count; index++) { await writer.StartRowAsync(); await writer.WriteAsync(commitIds[index], NpgsqlDbType.Char); await writer.WriteAsync(0, NpgsqlDbType.Integer); await writer.WriteAsync(commitIds[index - 1], NpgsqlDbType.Char); }
            await writer.CompleteAsync();
        }
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,base_content_sha256,content_sha256) FROM STDIN (FORMAT BINARY)"))
        {
            for (var commitIndex = 0; commitIndex < commitIds.Count; commitIndex++)
            {
                var baseDigest = commitIndex == 0 ? null : Digest(300 + (commitIndex - 1) % 2);
                var contentDigest = Digest(300 + commitIndex % 2);
                for (var ordinal = 0; ordinal < entriesPerCommit; ordinal++)
                {
                    await writer.StartRowAsync(); await writer.WriteAsync(commitIds[commitIndex], NpgsqlDbType.Char); await writer.WriteAsync(ordinal, NpgsqlDbType.Integer); await writer.WriteAsync(assets[ordinal], NpgsqlDbType.Uuid); await writer.WriteAsync(edits[ordinal], NpgsqlDbType.Uuid); await writer.WriteAsync(commitIndex == 0 ? "added" : "modified", NpgsqlDbType.Text);
                    if (baseDigest is null) await writer.WriteNullAsync(); else await writer.WriteAsync(baseDigest, NpgsqlDbType.Char);
                    await writer.WriteAsync(contentDigest, NpgsqlDbType.Char);
                }
            }
            await writer.CompleteAsync();
        }
        await transaction.CommitAsync();
        var additionalAssets = Enumerable.Range(0, 99_900).Select(index => Id(70_000 + index)).ToArray();
        await using (var scaleTransaction = await connection.BeginTransactionAsync())
        {
            await using (var writer = await connection.BeginBinaryImportAsync("COPY assets(id,owner_id,state,format) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var asset in additionalAssets) { await writer.StartRowAsync(); await writer.WriteAsync(asset, NpgsqlDbType.Uuid); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync("active", NpgsqlDbType.Text); await writer.WriteAsync("dng", NpgsqlDbType.Text); }
                await writer.CompleteAsync();
            }
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,'capacity','capacity','branch',$3,false)", default, owner, library, parent);
            await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_materialized_state(owner_id,library_id,normalized_ref,source_commit_id,asset_id,edit_id,content_sha256,rebuilt_at) FROM STDIN (FORMAT BINARY)"))
            {
                for (var index = 0; index < assets.Length; index++)
                {
                    await writer.StartRowAsync(); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync(library, NpgsqlDbType.Text); await writer.WriteAsync("capacity", NpgsqlDbType.Text); await writer.WriteAsync(parent!, NpgsqlDbType.Char); await writer.WriteAsync(assets[index], NpgsqlDbType.Uuid); await writer.WriteAsync(edits[index], NpgsqlDbType.Uuid); await writer.WriteAsync(Digest(301), NpgsqlDbType.Char); await writer.WriteAsync(DateTimeOffset.UtcNow, NpgsqlDbType.TimestampTz);
                }
                foreach (var asset in additionalAssets)
                {
                    await writer.StartRowAsync(); await writer.WriteAsync(owner, NpgsqlDbType.Text); await writer.WriteAsync(library, NpgsqlDbType.Text); await writer.WriteAsync("capacity", NpgsqlDbType.Text); await writer.WriteAsync(parent!, NpgsqlDbType.Char); await writer.WriteAsync(asset, NpgsqlDbType.Uuid); await writer.WriteNullAsync(); await writer.WriteAsync(Digest(301), NpgsqlDbType.Char); await writer.WriteAsync(DateTimeOffset.UtcNow, NpgsqlDbType.TimestampTz);
                }
                await writer.CompleteAsync();
            }
            await scaleTransaction.CommitAsync();
        }
        Check(Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM assets WHERE owner_id=$1", default, owner), System.Globalization.CultureInfo.InvariantCulture) == 100_000, "Persisted capacity must contain 100,000 library assets.");
        Check(Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id WHERE c.owner_id=$1 AND c.library_id=$2", default, owner, library), System.Globalization.CultureInfo.InvariantCulture) == 1_000_000, "Persisted capacity must contain one million immutable commit entries.");
        Check((await new IntegrityService(db).VerifyAsync(owner, library, default)).Healthy, "Persisted capacity history must pass production graph/content provenance verification.");
        var queries = new QueryService(db);
        var projection = new ProjectionService(db, new IntegrityService(db));
        var lineage = new List<double>();
        var state = new List<double>();
        var entryLookup = new List<double>();
        for (var sample = 0; sample < 20; sample++)
        {
            var timer = Stopwatch.StartNew();
            Check((await queries.ProjectAsync(owner, library, "capacity", null, false, 0, 100, default)).Count == 100, "Persisted 10,000-node lineage must return a complete first page.");
            timer.Stop(); lineage.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            Check((await projection.AssetAsync(owner, library, "capacity", assets[0], default)).Count == 1, "Persisted capacity state must resolve the indexed asset.");
            timer.Stop(); state.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            Check(Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM vc_commit_entries WHERE asset_id=$1 AND edit_id=$2", default, assets[0], edits[0]), System.Globalization.CultureInfo.InvariantCulture) == commitCount, "Persisted commit-entry index must resolve every revision for one edit identity.");
            timer.Stop(); entryLookup.Add(timer.Elapsed.TotalMilliseconds);
        }
        var lineageP95 = lineage.Order().ElementAt(18);
        var stateP95 = state.Order().ElementAt(18);
        var entryLookupP95 = entryLookup.Order().ElementAt(18);
        Check(lineageP95 <= 2000 && stateP95 <= 200 && entryLookupP95 <= 200, $"Persisted reference-scale p95 targets must hold: lineage {lineageP95:F0} ms, state {stateP95:F1} ms, entry index {entryLookupP95:F1} ms.");
        Console.WriteLine($"Persisted capacity checks passed at 1M commit entries/100k assets: 10k-node lineage p95 {lineageP95:F0} ms; asset-state p95 {stateP95:F1} ms; entry-index p95 {entryLookupP95:F1} ms.");
    }

    private static async Task NormalizedPageIndexConstraintsAsync(NpgsqlDataSource db, string root, string other)
    {
        await using var connection = await db.OpenConnectionAsync();
        await RejectAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO vc_commit_parents(commit_id,ordinal,parent_id) VALUES($1,0,$2)", default, root, other));

        var at = DateTimeOffset.Parse("2026-09-10T00:07:00Z");
        var entry = new VersionControlEntry(Id(1), Id(1001), "modified", Digest(1), Digest(2));
        var missingParent = new VersionControlManifest("library-1", [root], "owner-1", at, at, "Missing normalized parent", [entry]);
        var missingParentBytes = VersionControlManifestCodec.Write(missingParent);
        await RejectAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner-1','library-1','owner-1',$2,$2,$3,1,1,$4)", default, VersionControlManifestCodec.Digest(missingParentBytes), at, missingParent.Message, missingParentBytes));

        var timestampMismatch = missingParent with { Parents = [], Message = "Mismatched normalized timestamp" };
        var timestampBytes = VersionControlManifestCodec.Write(timestampMismatch);
        await RejectAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,'owner-1','library-1','owner-1',$2,$3,$4,1,1,$5)", default, VersionControlManifestCodec.Digest(timestampBytes), at, at.AddSeconds(1), timestampMismatch.Message, timestampBytes));
    }

    private static async Task PhysicalObjectIntegrityAsync(NpgsqlDataSource db)
    {
        const string owner = "storage-owner";
        const string library = "storage-library";
        var asset = Id(9700);
        var edit = Id(19700);
        var payload = "storage-backed-xmp"u8.ToArray();
        var address = new ContentAddress(Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"), new AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
        var bucket = "vc-integrity-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        var storage = new S3ObjectStore(s3, bucket, "ingest-test-key");
        await using var stream = new MemoryStream(payload);
        var version = await storage.PublishVerifiedAsync(address, stream, default);
        var at = DateTimeOffset.Parse("2026-09-10T00:05:00Z");
        var entry = new VersionControlEntry(asset, edit, "added", null, address.Sha256);
        var manifest = new VersionControlManifest(library, [], owner, at, at, "Storage-backed commit", [entry]);
        var bytes = VersionControlManifestCodec.Write(manifest);
        var commit = VersionControlManifestCodec.Digest(bytes);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative')", default, address.Sha256, address.Length, address.AuthoritativeKey, version);
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,state,format) VALUES($1,$2,'active','dng'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($3,$1,0,$4,'fixture')", default, asset, owner, edit, address.Sha256);
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$2,$4,$4,'Storage-backed commit',1,1,$5)", default, commit, owner, library, at, bytes);
            await ExecuteAsync(connection, "INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,content_sha256) VALUES($1,0,$2,$3,'added',$4)", default, commit, asset, edit, address.Sha256);
        }

        var integrity = new IntegrityService(db, storage);
        Check((await integrity.VerifyAsync(owner, library, default)).Healthy, "Pinned storage object version must verify before ref advancement.");
        var performanceEntries = new List<VersionControlEntry>();
        for (var index = 0; index < 100; index++)
        {
            var objectBytes = System.Text.Encoding.UTF8.GetBytes("storage-capacity-xmp-" + index);
            var objectAddress = new ContentAddress(Convert.ToHexStringLower(SHA256.HashData(objectBytes)), objectBytes.Length);
            await using var objectStream = new MemoryStream(objectBytes);
            var objectVersion = await storage.PublishVerifiedAsync(objectAddress, objectStream, default);
            var performanceAsset = Id(30_000 + index);
            var performanceEdit = Id(40_000 + index);
            await using var connection = await db.OpenConnectionAsync();
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative'); INSERT INTO assets(id,owner_id,state,format) VALUES($5,$6,'active','dng'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($7,$5,0,$1,'fixture')", default, objectAddress.Sha256, objectAddress.Length, objectAddress.AuthoritativeKey, objectVersion, performanceAsset, owner, performanceEdit);
            performanceEntries.Add(new(performanceAsset, performanceEdit, "added", null, objectAddress.Sha256));
        }
        await using (var connection = await db.OpenConnectionAsync())
            for (var index = 1; index < 20; index++)
                await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,protected) VALUES($1,$2,$3,$3,'branch',false)", default, owner, library, "storage-capacity-" + index);
        var storageCommitLatencies = new List<double>();
        var storageCommits = new CommitService(db, storage);
        for (var sample = 0; sample < 20; sample++)
        {
            var timer = Stopwatch.StartNew();
            await storageCommits.PublishAsync(owner, library, "storage-capacity-client-" + sample, new(Guid.Empty, sample == 0 ? "main" : "storage-capacity-" + sample, null, "Storage-backed capacity " + sample, at.AddSeconds(sample + 1)), "storage-capacity-" + sample, "op_storage_capacity", default, preparedEntries: performanceEntries);
            timer.Stop();
            storageCommitLatencies.Add(timer.Elapsed.TotalMilliseconds);
        }
        var storageCommitP95 = storageCommitLatencies.Order().ElementAt(18);
        Check(storageCommitP95 <= 2000, "Production storage-backed 100-entry commit p95 must be at most two seconds.");
        Console.WriteLine($"Storage-backed 100-entry commit p95 {storageCommitP95:F0} ms.");
        await using (var wrong = new MemoryStream("wrong-xmp"u8.ToArray()))
        {
            var corrupted = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = address.AuthoritativeKey, InputStream = wrong, ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS, ServerSideEncryptionKeyManagementServiceKeyId = "ingest-test-key", ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
            await using var connection = await db.OpenConnectionAsync();
            await ExecuteAsync(connection, "UPDATE content_objects SET storage_version=$2 WHERE sha256=$1", default, address.Sha256, corrupted.VersionId);
        }
        Check((await integrity.VerifyAsync(owner, library, default)).Codes.Contains("altered_object"), "Altered pinned storage bytes must fail integrity verification.");
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE content_objects SET storage_version=$2 WHERE sha256=$1", default, address.Sha256, version);
        await storage.DeleteVersionAsync(address.AuthoritativeKey, version, default);
        Check((await integrity.VerifyAsync(owner, library, default)).Codes.Contains("missing_object"), "Missing pinned storage version must fail integrity verification.");
        await RejectCodeAsync(() => new CommitService(db, storage).PublishAsync(owner, "missing-storage-library", "storage-client", new(Guid.NewGuid(), "main", null, "Reject missing storage", at.AddSeconds(1)), "missing-storage-commit", "op_missing_storage_commit", default, preparedEntries: [entry]), "content_not_authoritative");
        await RejectCodeAsync(() => new RefService(db, integrity).CreateAsync(owner, library, "branch", new("broken-storage", commit), "broken-storage", "op_broken_storage", default), "graph_corrupt");
    }

    private static async Task ConcurrentIntegrityPublicationAsync(NpgsqlDataSource db)
    {
        const string owner = "integrity-race-owner";
        const string library = "integrity-race-library";
        var asset = Id(9800);
        var edit = Id(19800);
        var digest = Digest(398);
        var entry = new VersionControlEntry(asset, edit, "added", null, digest);
        var at = DateTimeOffset.Parse("2026-09-10T00:06:00Z");
        var manifest = new VersionControlManifest(library, [], owner, at, at, "Integrity race root", [entry]);
        var bytes = VersionControlManifestCodec.Write(manifest);
        var commit = VersionControlManifestCodec.Digest(bytes);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,1,'xmp',$2,'v1','authoritative')", default, digest, "sha256/" + digest);
            await ExecuteAsync(connection, "INSERT INTO assets(id,owner_id,state,format) VALUES($1,$2,'active','dng'); INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($3,$1,0,$4,'fixture')", default, asset, owner, edit, digest);
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$2,$4,$4,'Integrity race root',1,1,$5); INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,content_sha256) VALUES($1,0,$6,$7,'added',$8); INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($2,$3,'main','main','branch',$1,true)", default, commit, owner, library, at, bytes, asset, edit, digest);
        }

        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new IntegrityService(db, checkpoint: async (point, _) =>
        {
            if (point == "snapshot-captured")
            {
                snapshotCaptured.SetResult();
                await releaseHealthy.Task;
            }
        });
        var healthyVerification = first.VerifyAsync(owner, library, default);
        await snapshotCaptured.Task;
        await using (var connection = await db.OpenConnectionAsync())
        {
            var corruptBytes = "not-json"u8.ToArray();
            await ExecuteAsync(connection, "INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) VALUES($1,$2,$3,$2,$4,$4,'Concurrent corruption',1,1,$5)", default, VersionControlManifestCodec.Digest(corruptBytes), owner, library, at.AddSeconds(1), corruptBytes);
        }
        var unhealthyVerification = new IntegrityService(db).VerifyAsync(owner, library, default);
        await Task.Delay(100);
        Check(!unhealthyVerification.IsCompleted, "A newer integrity decision must wait for the older snapshot to publish its result.");
        releaseHealthy.SetResult();
        Check((await healthyVerification).Healthy && !(await unhealthyVerification).Healthy, "Serialized verifiers must publish the newer unhealthy result last.");
        await using (var connection = await db.OpenConnectionAsync())
            Check(await ScalarAsync(connection, "SELECT 1 FROM vc_ref_quarantine WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='main'", default, owner, library) is not null, "A stale healthy verifier must not erase a newer corruption quarantine.");
    }

    private static async Task WorkerStorageFailureAsync(NpgsqlDataSource db)
    {
        using var s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:1",
            ForcePathStyle = true,
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(1),
        });
        var logger = new RecordingLogger<IntegrityVerificationWorker>();
        var worker = new IntegrityVerificationWorker(db, new IntegrityService(db, new S3ObjectStore(s3, "unavailable", "unavailable")), logger, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        await worker.VerifyOnceAsync(default);
        Check(logger.Messages.Any(message => message.Contains("version-control.integrity.failure", StringComparison.Ordinal)), "A storage-provider failure must emit the integrity failure signal without escaping the scheduled worker.");
    }

    private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
    private static string Digest(int value) => value.ToString("x64");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class InjectedFailure : Exception;

    private static async Task RejectCodeAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (VersionControlProblem problem) when (problem.Code == code) { return; }
        throw new InvalidOperationException($"Expected version-control problem {code}.");
    }

    private static async Task RejectIngestCodeAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (IngestException problem) when (problem.Code == code) { return; }
        throw new InvalidOperationException($"Expected ingest problem {code}.");
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
