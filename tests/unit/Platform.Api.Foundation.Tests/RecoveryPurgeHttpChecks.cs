using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Data;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.Recovery;
using Platform.Ingest.Worker;
using Platform.Api.Features.VersionControl.Working;
using Platform.Contracts.VersionControl;
using Platform.Recovery;
using Platform.Storage;
using static IngestChecks;
using static Platform.Storage.IngestDb;

internal static class RecoveryPurgeHttpChecks
{
    public static async Task<RecoveryPurgeProposalView> RunAsync(NpgsqlDataSource db, S3ObjectStore storage,
        IReadOnlyDictionary<string, string?> configuration, TimeProvider clock, Guid assetId,
        Guid sharedAssetId, string contentSha, byte[] payloadBytes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<RecoveryPurgeService>();
        builder.Services.AddSingleton<RecoveryHealthService>();
        builder.Services.AddSingleton<RecoveryOperationQueryService>();
        builder.Services.AddSingleton<RecoveryRestoreRequestService>();
        builder.Services.AddSingleton<RecoveryOwnerResolver>();
        builder.Services.AddAuthentication("recovery-purge-test")
            .AddScheme<AuthenticationSchemeOptions, RecoveryPurgeTestAuthenticationHandler>("recovery-purge-test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        RecoveryEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        // Start a valid upload before freeze, but leave publication pending. The worker must
        // recheck the digest fence at publication time after the API freeze commits.
        const string lateOwner = "late-foreign-owner";
        var imports = new ImportService(db, storage);
        var lateBatch = await imports.CreateAsync(lateOwner, "purge-race-upload", "purge-race-upload",
            new ImportRequest(false, [new ImportItem("late-shared.jpg", contentSha, payloadBytes.LongLength)]), default);
        var lateItem = lateBatch.Items.Single();
        await new TransferGrantIssuer(db).IssueAsync(lateItem.Id, lateOwner, lateItem.LifecycleVersion,
            DateTime.UtcNow, default);
        await imports.PutPartAsync(lateOwner, lateItem.Id, lateItem.LifecycleVersion, 1, payloadBytes,
            IngestChecks.Hash(payloadBytes), "purge-race-upload", default);
        await imports.CompleteAsync(lateOwner, lateItem.Id, lateItem.LifecycleVersion,
            "purge-race-upload", default);

        var anonymous = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized,
            "The purge proposal route requires authenticated owner authorization.");
        Check((await client.GetAsync("/v1/recovery/points")).StatusCode == HttpStatusCode.Unauthorized &&
            (await client.GetAsync($"/v1/recovery/sessions/{Guid.NewGuid():D}")).StatusCode == HttpStatusCode.Unauthorized,
            "Point and session status routes require authentication.");

        client.DefaultRequestHeaders.Add("X-Test-Owner", "purge-proposal-owner");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        var pointsResponse = await client.GetAsync("/v1/recovery/points");
        Check(pointsResponse.StatusCode == HttpStatusCode.OK && pointsResponse.Headers.CacheControl?.Private == true &&
            pointsResponse.Headers.CacheControl.NoStore &&
            (await client.GetAsync($"/v1/recovery/sessions/{Guid.NewGuid():D}")).StatusCode == HttpStatusCode.NotFound,
            "Authenticated operation status is private/no-store and unknown sessions fail closed.");
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner","test-owner");
        var available=await client.GetFromJsonAsync<RecoveryPointPage>("/v1/recovery/points")
            ?? throw new InvalidOperationException("Expected signed point listing.");
        var restoreRequest=new RecoveryRestoreRequest(available.Items.First().RecoveryPointId,new("library",null,null),"HTTP fixture request");
        Check((await client.PostAsJsonAsync("/v1/recovery/sessions",restoreRequest)).StatusCode==HttpStatusCode.BadRequest,
            "Restore requests require a bounded idempotency key.");
        client.DefaultRequestHeaders.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));
        var requested=await client.PostAsJsonAsync("/v1/recovery/sessions",restoreRequest);
        var restoreSession=await requested.Content.ReadFromJsonAsync<RecoverySessionView>()
            ?? throw new InvalidOperationException("Expected pending restore session.");
        Check(requested.StatusCode==HttpStatusCode.Accepted && restoreSession.State=="requested" &&
            requested.Headers.CacheControl?.Private==true && requested.Headers.CacheControl.NoStore,
            $"Recently authenticated restore requests enter private isolated pending state. HTTP {(int)requested.StatusCode}: {await requested.Content.ReadAsStringAsync()}");
        var replay=await client.PostAsJsonAsync("/v1/recovery/sessions",restoreRequest);
        Check(replay.StatusCode==HttpStatusCode.Accepted &&
            (await replay.Content.ReadFromJsonAsync<RecoverySessionView>())?.SessionId==restoreSession.SessionId &&
            (await client.PostAsJsonAsync("/v1/recovery/sessions",restoreRequest with { Reason="changed request" })).StatusCode==HttpStatusCode.Conflict &&
            (await client.GetAsync($"/v1/recovery/sessions/{restoreSession.SessionId:D}")).StatusCode==HttpStatusCode.OK,
            "HTTP retries preserve the original session and reject changed payloads.");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner","purge-proposal-owner");
        Check((await client.GetAsync($"/v1/recovery/sessions/{restoreSession.SessionId:D}")).StatusCode==HttpStatusCode.NotFound,
            "Foreign HTTP principals cannot discover another owner's restore session.");
        var created = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(created.StatusCode == HttpStatusCode.Created,
            "A recently authenticated owner can submit a purge proposal over HTTP.");
        var proposal = await created.Content.ReadFromJsonAsync<RecoveryPurgeProposalView>()
            ?? throw new InvalidOperationException("Expected the durable purge proposal response.");

        var purgeService = app.Services.GetRequiredService<RecoveryPurgeService>();
        var repeatableProposal = await CreateRaceProposalAsync(db, purgeService, clock,
            "repeatable-read-freeze-race-owner");
        var historyCommit = await InsertHistoryHoldFixtureAsync(db, repeatableProposal);
        await FreezeSnapshotRaceChecksAsync(db, purgeService, repeatableProposal, historyCommit,
            clock, IsolationLevel.RepeatableRead);
        var serializableProposal = await CreateRaceProposalAsync(db, purgeService, clock,
            "serializable-freeze-race-owner");
        var serializableHistoryCommit = await InsertHistoryHoldFixtureAsync(db, serializableProposal);
        await FreezeSnapshotRaceChecksAsync(db, purgeService, serializableProposal, serializableHistoryCommit,
            clock, IsolationLevel.Serializable);

        var read = await client.GetAsync($"/v1/recovery/purge/proposals/{proposal.Proposal.OperationId:D}");
        Check(read.StatusCode == HttpStatusCode.OK,
            "The owner can read the canonical proposal status over HTTP.");
        var freezePath = $"/v1/recovery/purge/proposals/{proposal.Proposal.OperationId:D}/freeze";
        var frozen = await client.PostAsync(freezePath, content: null);
        var freezeProblem = frozen.StatusCode == HttpStatusCode.OK ? "" : await frozen.Content.ReadAsStringAsync();
        Check(frozen.StatusCode == HttpStatusCode.OK,
            $"A recently authenticated owner can freeze the exact proposal scope before recovery capture ({(int)frozen.StatusCode}: {freezeProblem}).");
        var freeze = await frozen.Content.ReadFromJsonAsync<RecoveryPurgeFreezeView>()
            ?? throw new InvalidOperationException("Expected the durable freeze acknowledgement response.");
        Check(freeze.Acknowledgement.OperationId == proposal.Proposal.OperationId &&
              freeze.Acknowledgement.OwnerId == "purge-proposal-owner" &&
              freeze.Phase == "executing",
            "The freeze response binds the proposal owner and advances only its reversible phase.");
        var replayedFreeze = await client.PostAsync(freezePath, content: null);
        var replayed = await replayedFreeze.Content.ReadFromJsonAsync<RecoveryPurgeFreezeView>()
            ?? throw new InvalidOperationException("Expected the original freeze acknowledgement on replay.");
        Check(replayedFreeze.StatusCode == HttpStatusCode.OK &&
              replayed.Acknowledgement == freeze.Acknowledgement,
            "Freeze replay returns the original immutable acknowledgement.");
        var lifecycle = new AssetService(db, null!, clock);
        var lifecycleBlocked = false;
        try
        {
            await lifecycle.ChangeAsync("purge-proposal-owner", assetId, false, "restore", 5, null,
                "purge-frozen-restore", "purge-frozen-lifecycle", default);
        }
        catch (IngestException error) when (error.Code == "recovery_purge_frozen" && error.Status == 409)
        {
            lifecycleBlocked = true;
        }
        Check(lifecycleBlocked,
            "The freeze blocks an exact asset lifecycle transition through the production storage service.");

        var sharedRead = await new AssetService(db, storage, clock).DownloadAsync(
            "legitimate-foreign-owner", sharedAssetId, false, "existing-shared-read", default);
        await using (sharedRead.Bytes)
            Check(sharedRead.Address.Sha256 == contentSha && sharedRead.Bytes.Length == payloadBytes.LongLength,
                "An already-existing cross-owner reference remains readable while new references to its frozen digest are blocked.");

        var working = new WorkingService(db);
        var workingBlocked = false;
        try
        {
            await working.PutAsync("purge-proposal-owner", "main", "purge-test",
                new WorkingChangeRequest(assetId, null, "main", "modified", proposal.Proposal.ContentSha256,
                    proposal.Proposal.ContentSha256, "purge-test.jpg", "active", 0),
                "purge-frozen-working", default);
        }
        catch (VersionControlProblem error) when (error.Code == "recovery_purge_frozen")
        {
            workingBlocked = true;
        }
        Check(workingBlocked,
            "Working-copy writes cannot introduce new graph references to a frozen payload.");

        var foreignWorkingBlocked = false;
        try
        {
            await working.PutAsync("legitimate-foreign-owner", "main", "purge-test",
                new WorkingChangeRequest(sharedAssetId, null, "main", "modified", contentSha,
                    contentSha, "shared.jpg", "active", 0),
                "foreign-owner-working", default);
        }
        catch (Platform.Contracts.VersionControl.VersionControlProblem error)
            when (error.Code == "recovery_purge_frozen")
        { foreignWorkingBlocked = true; }
        Check(foreignWorkingBlocked,
            "A different owner cannot add a new version-graph reference to the frozen digest.");

        var newImportBlocked = false;
        try
        {
            await imports.CreateAsync("another-foreign-owner", "purge-race-after-freeze", "purge-race-after-freeze",
                new ImportRequest(false, [new ImportItem("late-shared.jpg", contentSha, payloadBytes.LongLength)]), default);
        }
        catch (IngestException error) when (error.Code == "recovery_purge_frozen" && error.Status == 409)
        {
            newImportBlocked = true;
        }
        Check(newImportBlocked,
            "A new cross-owner import session cannot admit the frozen digest after freeze.");

        var processor = new BatchProcessor(db, storage);
        BatchStatus lateStatus = lateBatch;
        for (var attempt = 0; attempt < 20 && lateStatus.State is not ("failed" or "completed"); attempt++)
        {
            if (!await processor.ProcessNextAsync(default)) break;
            lateStatus = await imports.StatusAsync(lateOwner, lateBatch.Id, default);
        }
        await using var verifyDb = await db.OpenConnectionAsync();
        var lateAssetCount = (long)(await ScalarAsync(verifyDb,
            "SELECT count(*) FROM assets WHERE owner_id=$1 AND original_sha256=$2", default,
            lateOwner, contentSha))!;
        Check(lateStatus.Items.Single().State == "quarantined" && lateStatus.Items.Single().Code == "recovery_purge_frozen" &&
              lateAssetCount == 0,
            $"A pending pre-freeze upload cannot publish a new foreign asset after freeze (state={lateStatus.Items.Single().State}, code={lateStatus.Items.Single().Code ?? "none"}, assetCount={lateAssetCount}).");

        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "foreign-purge-owner");
        var foreign = await client.GetAsync($"/v1/recovery/purge/proposals/{proposal.Proposal.OperationId:D}");
        Check(foreign.StatusCode == HttpStatusCode.NotFound,
            "A foreign owner cannot discover another owner’s purge proposal.");
        var foreignFreeze = await client.PostAsync(freezePath, content: null);
        Check(foreignFreeze.StatusCode == HttpStatusCode.NotFound,
            "A foreign owner cannot freeze another owner’s purge proposal.");

        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "purge-proposal-owner");
        client.DefaultRequestHeaders.Remove("X-Test-Auth-Time");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.AddMinutes(-6)
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var stale = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(stale.StatusCode == HttpStatusCode.Forbidden,
            "The purge proposal route rejects stale authentication before persisting another proposal.");
        var staleFreeze = await client.PostAsync(freezePath, content: null);
        Check(staleFreeze.StatusCode == HttpStatusCode.Forbidden,
            "The freeze route rejects stale authentication even when the request would replay an existing freeze.");

        client.DefaultRequestHeaders.Remove("X-Test-Auth-Time");
        client.DefaultRequestHeaders.Add("X-Test-Auth-Time", DateTimeOffset.UtcNow.AddMinutes(1)
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var future = await client.PostAsJsonAsync("/v1/recovery/purge/proposals",
            new RecoveryPurgeProposalRequest(assetId, null, "original"));
        Check(future.StatusCode == HttpStatusCode.Forbidden,
            "The purge proposal route rejects future authentication timestamps.");
        var futureFreeze = await client.PostAsync(freezePath, content: null);
        Check(futureFreeze.StatusCode == HttpStatusCode.Forbidden,
            "The freeze route rejects future authentication timestamps.");

        await app.StopAsync();
        return proposal;
    }

    private static async Task FreezeSnapshotRaceChecksAsync(NpgsqlDataSource db, RecoveryPurgeService purgeService,
        RecoveryPurgeProposalV1 proposal, string historyCommitId, TimeProvider clock, IsolationLevel isolation)
    {
        await using var blockerConnection = await db.OpenConnectionAsync();
        await using var blocker = await blockerConnection.BeginTransactionAsync();
        await using (var lockTarget = new NpgsqlCommand(
            "SELECT sha256 FROM content_objects WHERE sha256=$1 FOR UPDATE", blockerConnection, blocker))
        {
            lockTarget.Parameters.AddWithValue(proposal.ContentSha256);
            _ = await lockTarget.ExecuteScalarAsync();
        }

        await using var writerConnection = await db.OpenConnectionAsync();
        await using var writer = await writerConnection.BeginTransactionAsync(isolation);
        await using var historyWriterConnection = await db.OpenConnectionAsync();
        await using var historyWriter = await historyWriterConnection.BeginTransactionAsync(isolation);
        await using (var snapshot = new NpgsqlCommand(
            "SELECT count(*) FROM recovery_purge_proposals WHERE content_sha256=$1", writerConnection, writer))
        {
            snapshot.Parameters.AddWithValue(proposal.ContentSha256);
            _ = await snapshot.ExecuteScalarAsync();
        }
        await using (var snapshot = new NpgsqlCommand(
            "SELECT count(*) FROM recovery_purge_proposals WHERE content_sha256=$1", historyWriterConnection, historyWriter))
        {
            snapshot.Parameters.AddWithValue(proposal.ContentSha256);
            _ = await snapshot.ExecuteScalarAsync();
        }

        var freezeTask = purgeService.FreezeAsync(proposal.OwnerId, proposal.OperationId,
            clock.GetUtcNow(), "mvcc-freeze-" + isolation, default);
        Task<bool>? admissionTask = null;
        Task? holdMutationTask = null;
        var released = false;
        try
        {
            await WaitForLockWaitAsync(db, "UPDATE content_objects SET storage_version=storage_version%");
            admissionTask = Platform.Storage.IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
                writerConnection, [proposal.ContentSha256], default);
            await WaitForLockWaitAsync(db, "SELECT sha256 FROM content_objects WHERE sha256=ANY%");
            holdMutationTask = HistoryRetention.UpdateExpiryAsync(historyWriterConnection, [historyCommitId],
                DateTimeOffset.UtcNow.AddDays(1), default);
            await WaitForLockWaitAsync(db, "SELECT sha256 FROM content_objects WHERE sha256=ANY%");

            await blocker.CommitAsync();
            released = true;
            await freezeTask;

            var staleSnapshotFailed = false;
            try { _ = await admissionTask; }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure)
            { staleSnapshotFailed = true; }
            Check(staleSnapshotFailed,
                $"A {isolation} admission with a pre-freeze snapshot fails after waiting on the updated digest row.");
            var staleHoldMutationFailed = false;
            try { await holdMutationTask; }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure)
            { staleHoldMutationFailed = true; }
            Check(staleHoldMutationFailed,
                $"A {isolation} version-history hold mutation with a pre-freeze snapshot fails after waiting on the updated digest row.");
            await writer.RollbackAsync();
            await historyWriter.RollbackAsync();
        }
        finally
        {
            if (!released) await blocker.RollbackAsync();
            if (!freezeTask.IsCompleted)
            {
                try { await freezeTask; }
                catch (RecoveryPurgeException) { }
            }
            if (admissionTask is not null && !admissionTask.IsCompleted)
            {
                try { _ = await admissionTask; }
                catch (PostgresException) { }
            }
            if (holdMutationTask is not null && !holdMutationTask.IsCompleted)
            {
                try { await holdMutationTask; }
                catch (PostgresException) { }
            }
        }

        await using var freshConnection = await db.OpenConnectionAsync();
        await using var freshTransaction = await freshConnection.BeginTransactionAsync(isolation);
        var deniedOnRetry = await Platform.Storage.IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
            freshConnection, [proposal.ContentSha256], default);
        Check(deniedOnRetry,
            $"A {isolation} admission retry with a fresh snapshot sees the committed freeze and refuses the digest.");
        await freshTransaction.CommitAsync();

        await using var freshHistoryConnection = await db.OpenConnectionAsync();
        await using var freshHistoryTransaction = await freshHistoryConnection.BeginTransactionAsync(isolation);
        var freshHoldMutationDenied = false;
        try
        {
            await HistoryRetention.UpdateExpiryAsync(freshHistoryConnection, [historyCommitId],
                DateTimeOffset.UtcNow.AddDays(1), default);
        }
        catch (VersionControlProblem error) when (error.Code == "recovery_purge_frozen")
        {
            freshHoldMutationDenied = true;
        }
        Check(freshHoldMutationDenied,
            $"A fresh {isolation} history-hold mutation rechecks the global digest freeze and refuses the update.");
        await using var holdCheck = new NpgsqlCommand(
            "SELECT expires_at FROM lifecycle_holds WHERE source_reference=$1", freshHistoryConnection, freshHistoryTransaction);
        holdCheck.Parameters.AddWithValue("vc:" + historyCommitId);
        var retainedExpiry = (DateTime)(await holdCheck.ExecuteScalarAsync())!;
        Check(retainedExpiry.ToUniversalTime() < DateTime.UtcNow,
            $"The {isolation} hold update did not change the previously expired history hold after freeze.");
        await freshHistoryTransaction.RollbackAsync();
    }

    private static async Task<string> InsertHistoryHoldFixtureAsync(NpgsqlDataSource db,
        RecoveryPurgeProposalV1 proposal)
    {
        var commitId = RecoveryEvidenceCodec.Hash(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("history-hold-freeze-" + proposal.OperationId.ToString("D"))));
        var now = DateTimeOffset.UtcNow;
        await using (var commit = db.CreateCommand("""
            INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes)
            VALUES($1,$2,$3,$2,$4,$4,'freeze race history hold',1,1,$5)
            """))
        {
            commit.Parameters.AddWithValue(commitId);
            commit.Parameters.AddWithValue(proposal.OwnerId);
            commit.Parameters.AddWithValue(proposal.LibraryId);
            commit.Parameters.AddWithValue(now);
            commit.Parameters.AddWithValue(Array.Empty<byte>());
            await commit.ExecuteNonQueryAsync();
        }
        await using (var entry = db.CreateCommand("""
            INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,base_content_sha256,content_sha256,display_path,lifecycle_state)
            VALUES($1,0,$2,NULL,'modified',$3,$3,'freeze-race.jpg','active')
            """))
        {
            entry.Parameters.AddWithValue(commitId);
            entry.Parameters.AddWithValue(proposal.AssetId);
            entry.Parameters.AddWithValue(proposal.ContentSha256);
            await entry.ExecuteNonQueryAsync();
        }
        await using (var hold = db.CreateCommand("""
            INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference,expires_at)
            VALUES($1,$2,'freeze-race','vc:'||$3,$4)
            """))
        {
            hold.Parameters.AddWithValue(Guid.NewGuid());
            hold.Parameters.AddWithValue(proposal.AssetId);
            hold.Parameters.AddWithValue(commitId);
            hold.Parameters.AddWithValue(now.AddDays(-1));
            await hold.ExecuteNonQueryAsync();
        }
        return commitId;
    }

    private static async Task<RecoveryPurgeProposalV1> CreateRaceProposalAsync(NpgsqlDataSource db,
        RecoveryPurgeService purgeService, TimeProvider clock, string owner)
    {
        var assetId = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes("freeze-snapshot-race-" + owner);
        var digest = IngestChecks.Hash(bytes);
        await using (var content = db.CreateCommand("""
            INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
            VALUES($1,$2,'jpeg',$3,'snapshot-race-version','authoritative')
            """))
        {
            content.Parameters.AddWithValue(digest);
            content.Parameters.AddWithValue(bytes.LongLength);
            content.Parameters.AddWithValue("sha256/" + digest);
            await content.ExecuteNonQueryAsync();
        }
        await using (var asset = db.CreateCommand("""
            INSERT INTO assets(id,owner_id,original_sha256,state,lifecycle_version,deleted_until,format)
            VALUES($1,$2,$3,'recoverable-deleted',1,$4,'jpeg')
            """))
        {
            asset.Parameters.AddWithValue(assetId);
            asset.Parameters.AddWithValue(owner);
            asset.Parameters.AddWithValue(digest);
            asset.Parameters.AddWithValue(clock.GetUtcNow().AddDays(-100));
            await asset.ExecuteNonQueryAsync();
        }
        var proposal = await purgeService.ProposeAsync(owner,
            new RecoveryPurgeProposalRequest(assetId, null, "original"), "snapshot-race-proposal-" + owner, default);
        return proposal.Proposal;
    }

    private static async Task WaitForLockWaitAsync(NpgsqlDataSource db, string queryPrefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = await db.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(
                    SELECT 1 FROM pg_stat_activity
                     WHERE pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE $1)
                """, connection);
            command.Parameters.AddWithValue(queryPrefix + "%");
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Expected the database writer to wait on the frozen digest row.");
    }
}

internal sealed class RecoveryPurgeTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var owner = Request.Headers["X-Test-Owner"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(owner)) return Task.FromResult(AuthenticateResult.NoResult());
        var authTime = Request.Headers["X-Test-Auth-Time"].FirstOrDefault() ?? "0";
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, owner),
            new Claim("cognito:groups", "owner"),
            new Claim("auth_time", authTime)
        ], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
