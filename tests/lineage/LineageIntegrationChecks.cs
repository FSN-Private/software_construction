using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using NpgsqlTypes;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Platform.Api.Features.Lineage;
using Platform.Api.Features.Rendering;
using Platform.Api.Features.VersionControl.Publishing;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Working;
using Platform.Api.Features.VersionControl.Operations;
using Platform.Api.Features.VersionControl.Query;
using Platform.Contracts.Lineage;
using Platform.Contracts.VersionControl;
using Platform.Ingest.Worker;
using Platform.Storage;
using static Platform.Storage.IngestDb;

internal static class LineageIntegrationChecks
{
    public static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("LINEAGE_INTEGRATION") != "1")
        {
            Console.WriteLine("Set LINEAGE_INTEGRATION=1 for PostgreSQL/S3 lineage merge checks.");
            return;
        }
        const string connectionString = "Host=127.0.0.1;Port=54329;Username=ingest;Password=ingest-test-only;Database=ingest;Include Error Detail=false";
        const string owner = "lineage-test-owner";
        const string library = "lineage-test-library";
        var schema = "lineage_test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(connectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = NpgsqlDataSource.Create(connectionString + ";Search Path=" + schema);
            await DatabaseMigrator.ApplyAsync(db, default);
            using var s3 = new AmazonS3Client(new BasicAWSCredentials("ingest-test", "ingest-test-only"),
                new AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
            var bucket = "lineage-merge-" + Guid.NewGuid().ToString("N");
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
            var storage = new S3ObjectStore(s3, bucket, "ingest-test-key");
            var asset = Guid.NewGuid();
            var edit = Guid.NewGuid();
            var baseSha = await StoreAsync(db, storage, Xmp("0", "base"));
            var targetSha = await StoreAsync(db, storage, Xmp("0", "target"));
            var sourceSha = await StoreAsync(db, storage, Xmp("4", "base"));
            await using (var connection = await db.OpenConnectionAsync())
            {
                await ExecuteAsync(connection, """
                    INSERT INTO assets(id,owner_id,state,format) VALUES($1,$2,'active','dng');
                    INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance)
                    VALUES($3,$1,0,$4,'fixture');
                    INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256)
                    VALUES($2,$5,'main',$1,$3,$6),($2,$5,'source',$1,$3,$7)
                    """, default, asset, owner, edit, baseSha, library, targetSha, sourceSha);
            }
            var commits = new CommitService(db, storage);
            var at = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
            var root = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "main", null, "Root", at),
                "lineage-root", "op-root", default, preparedEntries: [new(asset, edit, "added", null, baseSha, "image.dng", "active")]);
            _ = await new RefService(db).CreateAsync(owner, library, "branch", new RefRequest("source", root.CommitId),
                "lineage-ref", "op-ref", default);
            var tag = await new RefService(db).CreateAsync(owner, library, "tag",
                new RefRequest("reviewed-tag", root.CommitId), "lineage-tag-create", "op-tag-create", default);
            _ = await new RefService(db).DeleteAsync(owner, library, "reviewed-tag",
                new RefRequest("reviewed-tag", ExpectedHead: root.CommitId, ExpectedVersion: tag.Version, Confirm: true),
                "lineage-tag-delete", "op-tag-delete", default);
            await using (var connection = await db.OpenConnectionAsync())
                Check((long)(await ScalarAsync(connection, """
                    SELECT count(*) FROM vc_reflog r JOIN audit_events a ON a.id=r.audit_event_id
                    WHERE a.owner_id=$1 AND r.actor_id=$1 AND r.library_id=$2 AND r.normalized_name='reviewed-tag'
                      AND COALESCE(r.old_head,r.new_head)=$3 AND a.outcome='accepted'
                      AND ((a.operation_id='op-tag-create' AND a.event_type='version-control.tag.created')
                        OR (a.operation_id='op-tag-delete' AND a.event_type='version-control.tag.deleted'))
                    """, default, owner, library, root.CommitId))! == 2,
                    "Each accepted tag action must link owner, name, affected commit, and outcome by its audit event.");
            var target = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "main", root.CommitId, "Target edit", at.AddSeconds(1)),
                "lineage-target", "op-target", default, preparedEntries: [new(asset, edit, "modified", baseSha, targetSha, "image.dng", "active")]);
            var source = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "source", root.CommitId, "Source edit", at.AddSeconds(2)),
                "lineage-source", "op-source", default, preparedEntries: [new(asset, edit, "modified", baseSha, sourceSha, "image.dng", "active")]);
            var merge = new LineageMergeService(db, storage, commits,
                new LineageService(db, new RenderingService(db,
                    new VersionControlSelectedVersionResolver(new QueryService(db)), new ConfigurationBuilder().Build()), storage));
            var prepared = new MergePrepareRequest("main", "source", target.CommitId!, source.CommitId!);
            var proposal = await merge.PrepareAsync(owner, library, prepared, default);
            Check(proposal.BaseCommitId == root.CommitId && proposal.Affected.Count == 1 &&
                  proposal.Affected[0].Conflicts.Count == 0 && proposal.Affected[0].ProposedXmpSha256 is not null,
                "Disjoint target/source XMP must produce a complete clean proposal.");
            await using (var connection = await db.OpenConnectionAsync())
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commits", default))! == 3 &&
                      (long)(await ScalarAsync(connection, "SELECT count(*) FROM lineage_merge_content", default))! == 0,
                    "Preparation must not mutate commit or generated-content evidence.");
            var reviewRequest = new MergeReviewRequest(prepared, proposal.ProposalDigest, new Dictionary<string, string>());
            var review = await merge.ReviewAsync(owner, library, reviewRequest, default);
            Check(review.Resolved.Single().XmpSha256 == proposal.Affected.Single().ProposedXmpSha256 &&
                  review.Resolved.Single().XmpBase64 is not null, "Exact output bytes must be reviewable before publication.");
            var confirm = new MergeConfirmRequest(reviewRequest, review.ReviewDigest,
                "Merge source into main", at.AddSeconds(3), true);
            using (var interrupted = new CancellationTokenSource())
            {
                interrupted.Cancel();
                try { await merge.ConfirmAsync(owner, library, "fixture", confirm,
                    "lineage-interrupted", "op-interrupted", interrupted.Token);
                    throw new InvalidOperationException("Canceled confirmation must stop before publication."); }
                catch (OperationCanceledException) { }
            }
            await using (var connection = await db.OpenConnectionAsync())
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commits", default))! == 3 &&
                      (long)(await ScalarAsync(connection, "SELECT count(*) FROM lineage_merge_content", default))! == 0,
                    "A canceled confirmation must leave no partial commit or generated-content evidence.");
            var source2Sha = await StoreAsync(db, storage, Xmp("3", "base"));
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256) VALUES($1,$2,'source',$3,$4,$5)",
                    default, owner, library, asset, edit, source2Sha);
            var source2 = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "source", source.CommitId,
                "Source moved", at.AddSeconds(3)), "lineage-source-2", "op-source-2", default,
                preparedEntries: [new(asset, edit, "modified", sourceSha, source2Sha, "image.dng", "active")]);
            await RejectCodeAsync(() => commits.PublishAsync(owner, library, "fixture",
                    new(Guid.Empty, "main", target.CommitId, "Stale source", at.AddSeconds(3)),
                    "lineage-atomic-source", "op-atomic-source", default,
                    new(root.CommitId, source.CommitId!, target.CommitId!, [], "source"),
                    [new(asset, edit, "modified", targetSha, targetSha, "image.dng", "active")]), "stale_head");
            await RejectCodeAsync(() => merge.ConfirmAsync(owner, library, "fixture", confirm, "lineage-stale-source", "op-stale-source", default),
                "stale_head");
            var prepared2 = prepared with { ExpectedSourceHead = source2.CommitId! };
            var proposal2 = await merge.PrepareAsync(owner, library, prepared2, default);
            var target2Sha = await StoreAsync(db, storage, Xmp("0", "target two"));
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256) VALUES($1,$2,'main',$3,$4,$5)",
                    default, owner, library, asset, edit, target2Sha);
            var target2 = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "main", target.CommitId,
                "Target moved", at.AddSeconds(4)), "lineage-target-2", "op-target-2", default,
                preparedEntries: [new(asset, edit, "modified", targetSha, target2Sha, "image.dng", "active")]);
            await RejectCodeAsync(() => merge.ConfirmAsync(owner, library, "fixture",
                new MergeConfirmRequest(new(prepared2, proposal2.ProposalDigest, new Dictionary<string, string>()),
                    review.ReviewDigest, "Stale target merge", at.AddSeconds(5), true),
                "lineage-stale-target", "op-stale-target", default),
                "stale_head");
            prepared = prepared2 with { ExpectedTargetHead = target2.CommitId! };
            proposal = await merge.PrepareAsync(owner, library, prepared, default);
            reviewRequest = new(prepared, proposal.ProposalDigest, new Dictionary<string, string>());
            review = await merge.ReviewAsync(owner, library, reviewRequest, default);
            confirm = new MergeConfirmRequest(reviewRequest, review.ReviewDigest,
                "Merge source into main", at.AddSeconds(6), true);
            var result = await merge.ConfirmAsync(owner, library, "fixture", confirm, "lineage-confirm", "op-merge", default);
            Check(result.CommitId is not null, "Confirmation must create a commit.");
            var replay = await merge.ConfirmAsync(owner, library, "fixture", confirm, "lineage-confirm", "op-replay", default);
            Check(replay.CommitId == result.CommitId, "Duplicate confirmation must replay the same commit.");
            var history = new LineageService(db, new RenderingService(db,
                new VersionControlSelectedVersionResolver(new QueryService(db)), new ConfigurationBuilder().Build()), storage);
            var mainHistory = await history.HistoryAsync(owner, library, asset, "main", false, 0, 100, default);
            var sourceHistory = await history.HistoryAsync(owner, library, asset, "source", false, 0, 100, default);
            Check(mainHistory.Nodes.Any(node => node.Commit.CommitId == source2.CommitId) &&
                  sourceHistory.Nodes.All(node => node.Commit.CommitId != target2.CommitId),
                "The indexed image history must import merge ancestry without mixing unrelated branch commits.");
            await using (var connection = await db.OpenConnectionAsync())
            {
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commit_parents WHERE commit_id=$1", default, result.CommitId))! == 2,
                    "The merge must have target and source parents.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM lineage_merge_content WHERE owner_id=$1 AND library_id=$2 AND asset_id=$3 AND edit_id=$4 AND sha256=$5", default,
                    owner, library, asset, edit, proposal.Affected[0].ProposedXmpSha256))! == 1,
                    "Generated XMP must have exact owner/asset/edit provenance.");
                Check((long)(await ScalarAsync(connection, "SELECT count(*) FROM vc_commits", default))! == 6,
                    "Replay must not duplicate the merge commit.");
                await RejectCodeAsync(() => VersionControlAdapters.ValidateEntriesAsync(connection, owner,
                    [new(Guid.NewGuid(), edit, "modified", baseSha, targetSha)], storage, default),
                    "cross_library_reference");
                await RejectCodeAsync(() => VersionControlAdapters.ValidateEntriesAsync(connection, owner,
                    [new(asset, edit, "modified", baseSha, new string('f', 64))], storage, default),
                    "content_not_authoritative");
                await ExecuteAsync(connection,
                    "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,'lineage-index','lineage-index','branch',$3,false)",
                    default, owner, library, result.CommitId);
                Check((long)(await ScalarAsync(connection,
                    "SELECT count(*) FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index'",
                    default, owner, library))! == mainHistory.Nodes.Count,
                    "A branch created from existing history must backfill its reachability index.");
                await ExecuteAsync(connection,
                    "DELETE FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index'",
                    default, owner, library);
                await ExecuteAsync(connection,
                    "SELECT vc_rebuild_lineage_reachable($1,$2,'lineage-index',$3::char(64))",
                    default, owner, library, result.CommitId);
                Check((long)(await ScalarAsync(connection,
                    "SELECT count(*) FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index'",
                    default, owner, library))! == mainHistory.Nodes.Count,
                    "The migration/recovery rebuild must reconstruct branch membership from immutable history.");
                await ExecuteAsync(connection,
                    "UPDATE vc_refs SET head_commit_id=$3 WHERE owner_id=$1 AND library_id=$2 AND normalized_name='lineage-index'",
                    default, owner, library, root.CommitId);
                Check((long)(await ScalarAsync(connection,
                    "SELECT count(*) FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index'",
                    default, owner, library))! == 1,
                    "A rewritten branch must remove displaced image commits from its index.");
                await ExecuteAsync(connection,
                    "UPDATE vc_refs SET head_commit_id=$3 WHERE owner_id=$1 AND library_id=$2 AND normalized_name='lineage-index'",
                    default, owner, library, result.CommitId);
                await ExecuteAsync(connection,
                    "UPDATE vc_refs SET normalized_name='lineage-index-renamed' WHERE owner_id=$1 AND library_id=$2 AND normalized_name='lineage-index'",
                    default, owner, library);
                Check((long)(await ScalarAsync(connection,
                    "SELECT count(*) FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index-renamed'",
                    default, owner, library))! == mainHistory.Nodes.Count,
                    "Branch rename and rewrite must retain an exact image index.");
                await ExecuteAsync(connection,
                    "DELETE FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name='lineage-index-renamed'",
                    default, owner, library);
                Check((long)(await ScalarAsync(connection,
                    "SELECT count(*) FROM vc_lineage_reachable WHERE owner_id=$1 AND library_id=$2 AND normalized_ref='lineage-index-renamed'",
                    default, owner, library))! == 0,
                    "Deleting a branch must remove its derived image index.");
            }
            var overlapTargetSha = sourceSha;
            var overlapSourceSha = await StoreAsync(db, storage, Xmp("2", "base"));
            _ = await new RefService(db).CreateAsync(owner, library, "branch", new RefRequest("conflict-target", root.CommitId),
                "lineage-conflict-ref-target", "op-conflict-ref-target", default);
            _ = await new RefService(db).CreateAsync(owner, library, "branch", new RefRequest("conflict-source", root.CommitId),
                "lineage-conflict-ref-source", "op-conflict-ref-source", default);
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256) VALUES($1,$2,'conflict-target',$3,$4,$5),($1,$2,'conflict-source',$3,$4,$6)",
                    default, owner, library, asset, edit, overlapTargetSha, overlapSourceSha);
            var overlapTarget = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "conflict-target", root.CommitId,
                "Target rating", at.AddSeconds(7)), "lineage-conflict-target", "op-conflict-target", default,
                preparedEntries: [new(asset, edit, "modified", baseSha, overlapTargetSha, "image.dng", "active")]);
            var overlapSource = await commits.PublishAsync(owner, library, "fixture", new(Guid.Empty, "conflict-source", root.CommitId,
                "Source rating", at.AddSeconds(8)), "lineage-conflict-source", "op-conflict-source", default,
                preparedEntries: [new(asset, edit, "modified", baseSha, overlapSourceSha, "image.dng", "active")]);
            var overlapPrepared = new MergePrepareRequest("conflict-target", "conflict-source", overlapTarget.CommitId!, overlapSource.CommitId!);
            var overlapProposal = await merge.PrepareAsync(owner, library, overlapPrepared, default);
            Check(overlapProposal.Affected.Single().Conflicts.Contains("rating"), "Concurrent rating edits must require a choice.");
            var overlapKey = $"{asset:D}/{edit:D}:rating";
            var overlapReviewRequest = new MergeReviewRequest(overlapPrepared, overlapProposal.ProposalDigest,
                new Dictionary<string, string> { [overlapKey] = "source" });
            var overlapReview = await merge.ReviewAsync(owner, library, overlapReviewRequest, default);
            Check(overlapReview.Resolved.Single().XmpBase64 is not null, "Resolved conflict bytes must be reviewable.");
            var overlapResult = await merge.ConfirmAsync(owner, library, "fixture",
                new(overlapReviewRequest, overlapReview.ReviewDigest, "Resolve rating", at.AddSeconds(9), true),
                "lineage-conflict-confirm", "op-conflict-confirm", default);
            Check(overlapResult.CommitId is not null, "Resolved conflict must publish after confirmation.");
            var capacityHead = await CapacityAsync(db, storage, owner, library, asset, edit, baseSha, targetSha);
            var noBase = await merge.PrepareAsync(owner, library,
                new("conflict-target", "capacity", overlapResult.CommitId!, capacityHead), default);
            Check(noBase.BaseCommitId is null && noBase.Affected.Any(item => item.Conflicts.Contains("whole_xmp")),
                "Disconnected histories must require explicit whole-XMP resolution.");
            _ = await new WorkingService(db, storage).PutAsync(owner, library, "device-conflict",
                new(asset, edit, "source", "modified", source2Sha, source2Sha, "image.dng", "active", 0),
                "op-dirty", default);
            await RejectCodeAsync(() => merge.PrepareAsync(owner, library,
                new("main", "source", result.CommitId!, source2.CommitId!), default), "stale_working_state");
            await using (var connection = await db.OpenConnectionAsync())
                await ExecuteAsync(connection, "DELETE FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id='device-conflict'",
                    default, owner, library);
            var operations = new OperationService(db, new QueryService(db), commits);
            var galleryWorking = await new WorkingService(db, storage).PutAsync(owner, library, "__gallery_web__",
                new(asset, edit, "main", "modified", proposal.Affected.Single().ProposedXmpSha256,
                    target2Sha, "image.dng", "active", 0), "op-gallery-working", default);
            var galleryStage = await new WorkingService(db, storage).StageAsync(owner, library, "__gallery_web__", "main",
                new([galleryWorking.Id], result.CommitId, 0), "op-gallery-stage", default);
            Check((await operations.CheckoutStateAsync(owner, library, "__lineage_web__", default)).GalleryWorkingCount == 1,
                "Browser checkout must see gallery web working changes.");
            await RejectCodeAsync(() => operations.CheckoutAsync(owner, library, "__lineage_web__",
                new("source", "block", false, 0), "lineage-checkout-block", "op-checkout-block", default),
                "dirty_checkout");
            var preserved = await operations.CheckoutAsync(owner, library, "__lineage_web__",
                new("source", "preserve", false, 0), "lineage-checkout-preserve", "op-checkout-preserve", default);
            Check(preserved.StashId is not null &&
                  (await operations.CheckoutStateAsync(owner, library, "__lineage_web__", default)).GalleryWorkingCount == 0,
                "Preserve must stash gallery changes and switch in one transaction.");
            _ = await new WorkingService(db, storage).PutAsync(owner, library, "__gallery_web__",
                new(asset, edit, "source", "modified", source2Sha, targetSha, "image.dng", "active", 0),
                "op-gallery-after-switch", default);
            await RejectCodeAsync(() => operations.CheckoutAsync(owner, library, "__lineage_web__",
                new("main", "discard", false, 1), "lineage-checkout-unconfirmed", "op-checkout-unconfirmed", default),
                "confirmation_required");
            _ = await operations.CheckoutAsync(owner, library, "__lineage_web__",
                new("main", "discard", true, 1), "lineage-checkout-discard", "op-checkout-discard", default);
            Check((await operations.CheckoutStateAsync(owner, library, "__lineage_web__", default)).GalleryWorkingCount == 0,
                "Confirmed discard must remove the current gallery working change.");
            _ = await operations.RestoreStashAsync(owner, library, "__lineage_web__", preserved.StashId!.Value,
                "lineage-stash-restore", "op-stash-restore", default);
            Check((await operations.CheckoutStateAsync(owner, library, "__lineage_web__", default)).GalleryWorkingCount == 1,
                "Restoring a web stash must restore the gallery working identity.");
            await using (var connection = await db.OpenConnectionAsync())
                Check((long)(await ScalarAsync(connection, """
                    SELECT count(*) FROM vc_staging_sets s JOIN vc_staged_entries e ON e.staging_set_id=s.id
                    WHERE s.id=$1 AND s.owner_id=$2 AND s.library_id=$3 AND s.client_id='__gallery_web__'
                      AND s.status='open' AND e.working_change_id=$4
                    """, default, galleryStage, owner, library, galleryWorking.Id))! == 1,
                    "Restoring a web stash must reopen the gallery staging set and preserve its selected change.");
            _ = await new WorkingService(db, storage).PutAsync(owner, library, "second-device",
                new(asset, edit, "main", "modified", proposal.Affected.Single().ProposedXmpSha256,
                    sourceSha, "image.dng", "active", 0), "op-device-conflict", default);
            Check((await operations.CheckoutStateAsync(owner, library, "__lineage_web__", default)).DeviceConflictCount > 0,
                "A differing device change must appear in checkout state.");
            await RejectCodeAsync(() => operations.CheckoutAsync(owner, library, "__lineage_web__",
                new("source", "preserve", false, 2), "lineage-device-block", "op-device-block", default),
                "device_conflict");
            await HiddenConnectorHistoryAsync(db, storage, commits, history, owner, library, asset, edit, root.CommitId!, baseSha, at);
            Console.WriteLine("PostgreSQL/S3 lineage merge checks passed.");
        }
        finally
        {
            if (!schema.StartsWith("lineage_test_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test schema");
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task HiddenConnectorHistoryAsync(NpgsqlDataSource db, S3ObjectStore storage,
        CommitService commits, LineageService history, string owner, string library, Guid asset, Guid edit,
        string rootId, string baseSha, DateTimeOffset at)
    {
        var otherAsset = Guid.NewGuid();
        var otherEdit = Guid.NewGuid();
        var otherSha = await StoreAsync(db, storage, Xmp("0", "connector one"));
        var otherNextSha = await StoreAsync(db, storage, Xmp("0", "connector two"));
        var otherMergeSha = await StoreAsync(db, storage, Xmp("0", "connector merge"));
        var hiddenSha = await StoreAsync(db, storage, Xmp("1", "hidden image"));
        var visibleSha = await StoreAsync(db, storage, Xmp("2", "visible image"));
        var mergedSha = await StoreAsync(db, storage, Xmp("3", "visible after merge"));
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, """
                INSERT INTO assets(id,owner_id,state,format) VALUES($1,$2,'active','dng');
                INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance)
                    VALUES($3,$1,0,$4,'fixture');
                INSERT INTO gallery_web_content(owner_id,library_id,branch_name,asset_id,edit_id,sha256)
                    VALUES($2,$5,'hidden-gap',$1,$3,$4),($2,$5,'hidden-gap',$1,$3,$6),
                          ($2,$5,'hidden-merge',$1,$3,$4),
                          ($2,$5,'hidden-gap',$7,$8,$9),($2,$5,'hidden-gap',$7,$8,$10),
                          ($2,$5,'hidden-gap',$7,$8,$11),($2,$5,'hidden-merge',$7,$8,$9),
                          ($2,$5,'hidden-gap',$1,$3,$12)
                """, default, otherAsset, owner, otherEdit, otherSha, library, otherNextSha,
                asset, edit, hiddenSha, visibleSha, mergedSha, otherMergeSha);
        var refs = new RefService(db);
        _ = await refs.CreateAsync(owner, library, "branch", new RefRequest("hidden-gap", rootId), "gap-ref", "gap-ref", default);
        _ = await refs.CreateAsync(owner, library, "branch", new RefRequest("hidden-merge", rootId), "merge-ref", "merge-ref", default);
        var connector = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", rootId, "Other image", at.AddMinutes(1)), "gap-connector", "gap-connector", default,
            preparedEntries: [new(otherAsset, otherEdit, "added", null, otherSha, "other.dng", "active")]);
        var hidden = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", connector.CommitId, "Hidden image", at.AddMinutes(2)), "gap-hidden", "gap-hidden", default,
            preparedEntries: [new(asset, edit, "modified", baseSha, hiddenSha, "image.dng", "active")]);
        var connector2 = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", hidden.CommitId, "Other image again", at.AddMinutes(3)), "gap-connector2", "gap-connector2", default,
            preparedEntries: [new(otherAsset, otherEdit, "modified", otherSha, otherNextSha, "other.dng", "active")]);
        var visible = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", connector2.CommitId, "Visible image", at.AddMinutes(4)), "gap-visible", "gap-visible", default,
            preparedEntries: [new(asset, edit, "modified", hiddenSha, visibleSha, "image.dng", "active")]);
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection,
                "INSERT INTO vc_visibility(owner_id,library_id,commit_id,hidden,changed_at,changed_by) VALUES($1,$2,$3,true,now(),$1)",
                default, owner, library, hidden.CommitId);
        var normal = await history.HistoryAsync(owner, library, asset, "hidden-gap", false, 0, 100, default);
        Check(normal.Nodes[0].Commit.CommitId == visible.CommitId &&
              normal.Nodes[0].HiddenParentIds.SequenceEqual([hidden.CommitId!]) &&
              normal.Nodes.All(node => node.Commit.CommitId != hidden.CommitId),
            "A hidden image commit beyond unrelated connectors must remain visibly collapsed.");
        var revealed = await history.HistoryAsync(owner, library, asset, "hidden-gap", true, 0, 100, default);
        Check(revealed.Nodes.Any(node => node.Commit.CommitId == hidden.CommitId && node.Commit.Hidden &&
              node.Commit.Parents.SequenceEqual([connector.CommitId!])),
            "Show-hidden must reveal exact hidden image identity and canonical connector parent.");

        var sourceConnector = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-merge", rootId, "Source other image", at.AddMinutes(5)), "merge-connector", "merge-connector", default,
            preparedEntries: [new(otherAsset, otherEdit, "added", null, otherSha, "other.dng", "active")]);
        var sourceHidden = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-merge", sourceConnector.CommitId, "Source hidden image", at.AddMinutes(6)), "merge-hidden", "merge-hidden", default,
            preparedEntries: [new(asset, edit, "modified", baseSha, hiddenSha, "image.dng", "active")]);
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection,
                "INSERT INTO vc_visibility(owner_id,library_id,commit_id,hidden,changed_at,changed_by) VALUES($1,$2,$3,true,now(),$1)",
                default, owner, library, sourceHidden.CommitId);
        var merge = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", visible.CommitId, "Join hidden path", at.AddMinutes(7)), "gap-merge", "gap-merge", default,
            new MergeEvidence(rootId, sourceHidden.CommitId!, visible.CommitId!, [], "hidden-merge"),
            [new(otherAsset, otherEdit, "modified", otherNextSha, otherMergeSha, "other.dng", "active")]);
        var afterMerge = await commits.PublishAsync(owner, library, "fixture",
            new(Guid.Empty, "hidden-gap", merge.CommitId, "Visible after merge", at.AddMinutes(8)), "gap-after-merge", "gap-after-merge", default,
            preparedEntries: [new(asset, edit, "modified", visibleSha, mergedSha, "image.dng", "active")]);
        var mergedHistory = await history.HistoryAsync(owner, library, asset, "hidden-gap", false, 0, 100, default);
        Check(mergedHistory.Nodes[0].Commit.CommitId == afterMerge.CommitId &&
              mergedHistory.Nodes[0].HiddenParentIds.Contains(sourceHidden.CommitId!),
            "A hidden image commit on an omitted merge-parent path must remain visibly collapsed.");
    }

    private static async Task<string> CapacityAsync(NpgsqlDataSource db, S3ObjectStore storage,
        string owner, string library, Guid asset, Guid edit, string sha, string alternateSha)
    {
        var ids = new List<string>(500);
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < 500; index++)
            {
                var at = DateTimeOffset.UnixEpoch.AddSeconds(index);
                var content = index % 2 == 0 ? sha : alternateSha;
                var baseline = index == 0 ? null : (index - 1) % 2 == 0 ? sha : alternateSha;
                var manifest = new VersionControlManifest(library, index == 0 ? [] : [ids[index - 1]],
                    owner, at, at, "Capacity version " + index,
                    [new VersionControlEntry(asset, edit, index == 0 ? "added" : "modified", baseline, content, "image.dng", "active")]);
                var bytes = VersionControlManifestCodec.Write(manifest);
                var id = VersionControlManifestCodec.Digest(bytes);
                await writer.StartRowAsync();
                await writer.WriteAsync(id, NpgsqlDbType.Char);
                await writer.WriteAsync(owner, NpgsqlDbType.Text);
                await writer.WriteAsync(library, NpgsqlDbType.Text);
                await writer.WriteAsync(owner, NpgsqlDbType.Text);
                await writer.WriteAsync(at, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(at, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(manifest.Message, NpgsqlDbType.Text);
                await writer.WriteAsync(1, NpgsqlDbType.Integer);
                await writer.WriteAsync(1, NpgsqlDbType.Integer);
                await writer.WriteAsync(bytes, NpgsqlDbType.Bytea);
                ids.Add(id);
            }
            await writer.CompleteAsync();
        }
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commit_parents(commit_id,ordinal,parent_id) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 1; index < ids.Count; index++)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(ids[index], NpgsqlDbType.Char);
                await writer.WriteAsync(0, NpgsqlDbType.Integer);
                await writer.WriteAsync(ids[index - 1], NpgsqlDbType.Char);
            }
            await writer.CompleteAsync();
        }
        await using (var writer = await connection.BeginBinaryImportAsync("COPY vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,base_content_sha256,content_sha256,display_path,lifecycle_state) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < ids.Count; index++)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(ids[index], NpgsqlDbType.Char);
                await writer.WriteAsync(0, NpgsqlDbType.Integer);
                await writer.WriteAsync(asset, NpgsqlDbType.Uuid);
                await writer.WriteAsync(edit, NpgsqlDbType.Uuid);
                await writer.WriteAsync(index == 0 ? "added" : "modified", NpgsqlDbType.Text);
                if (index == 0) await writer.WriteNullAsync();
                else await writer.WriteAsync((index - 1) % 2 == 0 ? sha : alternateSha, NpgsqlDbType.Char);
                await writer.WriteAsync(index % 2 == 0 ? sha : alternateSha, NpgsqlDbType.Char);
                await writer.WriteAsync("image.dng", NpgsqlDbType.Text);
                await writer.WriteAsync("active", NpgsqlDbType.Text);
            }
            await writer.CompleteAsync();
        }
        await ExecuteAsync(connection,
            "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,'capacity','capacity','branch',$3,false)",
            default, owner, library, ids[^1]);
        await transaction.CommitAsync();
        var rendering = new RenderingService(db, new VersionControlSelectedVersionResolver(new QueryService(db)),
            new ConfigurationBuilder().Build());
        var lineage = new LineageService(db, rendering, storage);
        var historyTimes = new List<double>();
        var comparisonTimes = new List<double>();
        for (var index = 0; index < 20; index++)
        {
            var timer = Stopwatch.StartNew();
            var page = await lineage.HistoryAsync(owner, library, asset, "capacity", false, 0, 100, default);
            timer.Stop();
            Check(page.Nodes.Count == 100 && page.NextOffset == 100, "The 500-version initial page must be bounded to 100.");
            historyTimes.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            var compared = await lineage.CompareAsync(owner, library, asset, ids[^1], ids[^2], default);
            timer.Stop();
            Check(compared.TargetCommitId == ids[^1] && compared.SourceCommitId == ids[^2] &&
                  compared.MetadataDifferences?.Single().Fields.Count > 0,
                "Capacity comparison must retain exact versions and read verified XMP differences.");
            comparisonTimes.Add(timer.Elapsed.TotalMilliseconds);
        }
        var historyP95 = historyTimes.Order().ElementAt(18);
        var comparisonP95 = comparisonTimes.Order().ElementAt(18);
        Check(historyP95 < 2000 && comparisonP95 < 3000,
            $"500-version service p95 exceeded: history {historyP95:F0} ms, comparison {comparisonP95:F0} ms.");
        Console.WriteLine($"500-version service p95: history {historyP95:F0} ms, comparison {comparisonP95:F0} ms.");
        return ids[^1];
    }

    private static async Task<string> StoreAsync(NpgsqlDataSource db, S3ObjectStore storage, byte[] payload)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        var address = new ContentAddress(digest, payload.Length);
        await using var bytes = new MemoryStream(payload, writable: false);
        var version = await storage.PublishVerifiedAsync(address, bytes, default);
        await using var connection = await db.OpenConnectionAsync();
        await ExecuteAsync(connection,
            "INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state) VALUES($1,$2,'xmp',$3,$4,'authoritative')",
            default, digest, payload.Length, address.AuthoritativeKey, version);
        return digest;
    }

    private static byte[] Xmp(string rating, string title) => Encoding.UTF8.GetBytes(
        $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><rdf:RDF><rdf:Description xmp:Rating=\"{rating}\"><dc:title><rdf:Alt><rdf:li>{title}</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RejectCodeAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (VersionControlProblem problem) when (problem.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code} without a merge commit.");
    }
}
