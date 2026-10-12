using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Npgsql;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryRestoreBindingV1(int SchemaVersion, RecoveryRestoreProposalV1 Proposal,
    string RuntimeBindingSha256, string CheckpointEnvelopeSha256, string CatalogPayloadSha256);

/// <summary>Retains an isolated clone for review. It does not launch normal access,
/// promote, or substitute a historical access proof for access to the new clone.</summary>
internal static class RecoveryRestoreRunner
{
    internal static async Task RunAsync(RecoveryWorkerConfiguration configuration, string proposalPath, CancellationToken ct)
    {
        await using var input = new FileStream(proposalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 64 * 1024) throw new RecoveryWorkerException("restore_proposal_size_invalid");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        if (input.ReadByte() != -1) throw new RecoveryWorkerException("restore_proposal_size_invalid");
        var proposal = ParseProposal(bytes, configuration);
        if (configuration.Required("RecoveryOnly") != "true") throw new RecoveryWorkerException("restore_recovery_only_required");
        var region = configuration.RequiredRegion("DestinationRegion");
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        if ((await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct)).Account != configuration.RequiredAccountId("DestinationAccountId"))
            throw new RecoveryWorkerException("recovery_task_account_mismatch");
        using var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
        using var dynamo = new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(region));
        using var kms = new AmazonKeyManagementServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        using var secrets = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var signer = await AwsKmsEvidenceSigner.CreateAsync(kms, configuration.Required("SigningKeyId"),
            configuration.RequiredHash("SigningPublicKeySha256"), ct);
        var directory = CreateScratchDirectory(configuration, proposal.SessionId);
        try
        {
            var database = await RecoveryDrillRunner.ReadRecoveryDatabaseAsync(configuration, secrets, directory, ct);
            await ExecuteAsync(configuration, s3, dynamo, database, proposal, signer, signer.Trust, directory, ct);
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch { } }
    }

    internal static RecoveryRestoreProposalV1 ParseProposal(byte[] bytes, RecoveryWorkerConfiguration configuration)
    {
        if (bytes.Length is < 1 or > 64 * 1024) throw new RecoveryWorkerException("restore_proposal_size_invalid");
        try
        {
            var proposal = JsonSerializer.Deserialize<RecoveryRestoreProposalV1>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException();
            proposal.Validate();
            if (!RecoveryEvidenceCodec.SerializeCanonical(proposal).AsSpan().SequenceEqual(bytes) ||
                proposal.OwnerId != configuration.Required("OwnerId") || proposal.LibraryId != configuration.Required("LibraryId"))
                throw new InvalidDataException();
            return proposal;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or NullReferenceException)
        { throw new RecoveryWorkerException("restore_proposal_invalid", error); }
    }

    internal static async Task ExecuteAsync(RecoveryWorkerConfiguration configuration, IAmazonS3 s3,
        IAmazonDynamoDB dynamo, RecoveryDrillRunner.RecoveryDatabaseConnection database,
        RecoveryRestoreProposalV1 proposal, IRecoveryEvidenceSigner signer, RecoverySigningTrust trust,
        string directory, CancellationToken ct)
    {
        proposal = ParseProposal(RecoveryEvidenceCodec.SerializeCanonical(proposal), configuration);
        if (configuration.Required("RecoveryOnly") != "true") throw new RecoveryWorkerException("restore_recovery_only_required");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = proposal.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new RecoveryWorkerException("restore_proposal_invalid");
        deadline.CancelAfter(remaining);
        ct = deadline.Token;
        var indexed = await RecoveryLatestPointDiscovery.ReadIndexedPointAsync(dynamo, s3,
            configuration.Required("ControlTableName"), proposal.OwnerId, proposal.LibraryId, proposal.RecoveryPointId, trust, ct);
        var catalog = await RecoveryControlReader.ReadCatalogEnvelopeAsync(dynamo, configuration, trust, proposal.RecoveryPointId, ct)
            ?? throw new RecoveryWorkerException("restore_point_not_verified");
        var verifiedCatalog = RecoveryCatalogCodec.Verify(catalog, trust);
        var point = await RecoveryVerifierRunner.VerifyPointAsync(configuration, s3, indexed.Pointer, trust, ct, historicalOwnerAccess: true);
        if (point.FinalManifest is null || verifiedCatalog.Payload.FullManifestSha256 != point.FinalManifest.Digest() ||
            verifiedCatalog.Payload.CheckpointSha256 != point.CheckpointPayloadSha256)
            throw new RecoveryWorkerException("restore_catalog_checkpoint_mismatch");
        await RecoveryVerifierRunner.VerifyReleaseBindingsAsync(configuration, s3, point, ct);
        var genesis = await RecoveryVerifierRunner.ReadAuditGenesisAsync(configuration, s3, trust, ct);
        var auditProof = await RecoveryAuditHeadProofStore.ReadAsync(dynamo, configuration.Required("VerificationTableName"),
            proposal.RecoveryPointId, trust, ct) ?? throw new RecoveryWorkerException("audit_head_proof_missing");
        var auditHead = RecoveryAuditHeadProofStore.ValidateForDrill(auditProof, configuration, point, genesis);
        var binding = new RecoveryRestoreBindingV1(1, proposal, RecoveryDrillCompletion.Binding(configuration, proposal.RequestDigest),
            point.CheckpointEnvelopeSha256, verifiedCatalog.PayloadSha256);
        var journalKey = $"evidence/restores/{proposal.SessionId:D}/request.v1.json";
        await RecoveryApiLaunchJournal.PersistAsync(s3, configuration, journalKey, "recovery_restore_request", binding, signer, trust, ct);

        // Keep the existing trusted clone namespace and schema constraints. A signed
        // request never grants permission to overwrite or delete an existing database.
        var name = DatabaseName(proposal);
        proposal.Validate();
        await using var administration = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        await administration.OpenAsync(ct);
        if (!await TryAcquireSessionLeaseAsync(administration, proposal.SessionId, ct)) throw new RecoveryWorkerException("restore_session_busy");
        await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=$1)", administration);
        exists.Parameters.AddWithValue(name);
        var existing = await exists.ExecuteScalarAsync(ct) is true;
        if (!existing)
        {
            await CaptureRunner.RequirePostgres18Async(configuration.Optional("PgDumpPath") ?? "pg_dump", ct);
            await CaptureRunner.RequirePostgres18Async(configuration.Optional("PgRestorePath") ?? "pg_restore", ct);
            var dump = point.Receipts.Single(item => item.DataClass == nameof(RecoveryDataClass.DatabaseSnapshot));
            var dumpPath = Path.Combine(directory, "snapshot.dump");
            await RecoveryDrillRunner.DownloadExactFileAsync(s3, dump, dumpPath, ct);
            proposal.Validate();
            await RecoveryDrillRunner.CreateDatabaseAsync(database, name, ct);
            await RecoveryTrustedDumpRestore.RestoreAsync(database, name, dumpPath,
                configuration.Optional("MigrationDirectory") ?? "/app/migrations", configuration.RequiredHash("MigrationDigest"),
                configuration.Optional("PgDumpPath") ?? "pg_dump", configuration.Optional("PgRestorePath") ?? "pg_restore", ct);
            await RecoveryDrillRunner.ValidateRestoredPointWithAuditHeadAsync(database, name, point.SourceManifest, trust, genesis,
                configuration.RequiredHash("MigrationDigest"), configuration.Optional("MigrationDirectory") ?? "/app/migrations",
                configuration.RequiredAccountId("DestinationAccountId"), auditHead, configuration, ct);
        }
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = name }.ConnectionString);
        await connection.OpenAsync(ct);
        string state;
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            if (existing)
            {
                _ = await ReadExistingCloneStateAsync(connection, transaction, proposal, ct);
            }
            await RestoreSessionStore.ImportVerifiedPointAsync(connection, transaction, catalog, trust, ct);
            await RestoreSessionStore.ImportProposalAsync(connection, transaction, proposal, ct);
            state = await RestoreSessionStore.ReadBoundSessionStateAsync(connection, transaction, proposal, DateTimeOffset.UtcNow, ct);
            if (state == "requested") { await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId, state, "restoring", DateTimeOffset.UtcNow, ct); state = "restoring"; }
            if (state is not ("restoring" or "validating")) throw new RecoveryWorkerException("restore_session_not_retrievable");
            await RequireRequestedScopeAsync(connection, transaction, proposal, ct);
            await transaction.CommitAsync(ct);
        }
        var materialized = await RecoveryMaterializer.MaterializeAsync(s3, configuration, point,
            new DynamoFenceIntentStore(dynamo, configuration.Required("FenceTableName"), configuration.Required("IntentTableName")), proposal.SessionId, ct);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await RestoreSessionStore.RebindMaterializedObjectsAsync(connection, transaction, proposal.SessionId, materialized, ct);
            if (state == "restoring") await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId, state, "validating", DateTimeOffset.UtcNow, ct);
            await transaction.CommitAsync(ct);
        }
        var validation = await RecoveryDrillRunner.ValidateMaterializedCandidateAsync(connection, point.SourceManifest, materialized,
            configuration.Required("SourceBucket"), configuration.Required("TargetBucket"), trust, genesis,
            configuration.Optional("MigrationDirectory") ?? "/app/migrations", configuration.RequiredHash("MigrationDigest"),
            configuration.RequiredAccountId("DestinationAccountId"), auditHead, configuration, ct);
        var targetDigest = RecoveryEvidenceCodec.Hash(RecoveryEvidenceCodec.SerializeCanonical(new
        { name, targetBucket = configuration.Required("TargetBucket"), application = configuration.RequiredHash("ApiBuildDigest"), validation }));
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await AppendValidatedCloneAsync(connection, transaction, proposal, verifiedCatalog, targetDigest, DateTimeOffset.UtcNow, ct);
            await transaction.CommitAsync(ct);
        }
        Console.WriteLine($"recovery_restore_retained session={proposal.SessionId:D} point={proposal.RecoveryPointId:D} state=validating access=owner_access_pending");
    }

    internal static string DatabaseName(RecoveryRestoreProposalV1 proposal) =>
        "recovery_drill_" + proposal.SessionId.ToString("N") + "_" + proposal.RecoveryPointId.ToString("N")[..8];

    internal static async Task<string> ReadExistingCloneStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RecoveryRestoreProposalV1 proposal, CancellationToken ct)
    {
        try { return await RestoreSessionStore.ReadBoundSessionStateAsync(connection, transaction, proposal, DateTimeOffset.UtcNow, ct); }
        catch (Exception error) when (error is InvalidDataException || error is PostgresException { SqlState: "42P01" or "42703" })
        { throw new RecoveryWorkerException("restore_clone_reconciliation_required", error); }
    }

    internal static string CreateScratchDirectory(RecoveryWorkerConfiguration configuration, Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new RecoveryWorkerException("restore_session_invalid");
        var directory = Path.Combine(configuration.Optional("ScratchDirectory") ?? Path.GetTempPath(),
            "recovery-restore", sessionId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static async Task<bool> TryAcquireSessionLeaseAsync(NpgsqlConnection connection, Guid sessionId, CancellationToken ct)
    {
        if (sessionId == Guid.Empty) throw new RecoveryWorkerException("restore_session_invalid");
        await using var lease = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        lease.Parameters.AddWithValue(BitConverter.ToInt64(SHA256.HashData(sessionId.ToByteArray()), 0));
        return await lease.ExecuteScalarAsync(ct) is true;
    }

    internal static Task<long> AppendValidatedCloneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RecoveryRestoreProposalV1 proposal, VerifiedRecoveryCatalog catalog, string targetDigest, DateTimeOffset atUtc, CancellationToken ct)
    {
        if (catalog.Payload.RecoveryPointId != proposal.RecoveryPointId || catalog.Payload.OwnerId != proposal.OwnerId ||
            catalog.Payload.LibraryId != proposal.LibraryId) throw new RecoveryWorkerException("restore_catalog_checkpoint_mismatch");
        // The imported point identity is the authenticated final manifest (including
        // owner-access evidence). Source closure validation above remains source-bound.
        return RestoreSessionStore.AppendValidationAsync(connection, transaction, proposal.SessionId,
            catalog.Payload.FullManifestSha256, targetDigest, "verified", [], atUtc, ct);
    }

    private static async Task RequireRequestedScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RecoveryRestoreProposalV1 proposal, CancellationToken ct)
    {
        if (proposal.RequestedScope.Kind == "full_library") return;
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM assets a WHERE a.id=$1 AND a.owner_id=$2 AND
                ($3::uuid IS NULL OR EXISTS(SELECT 1 FROM edit_identities i WHERE i.id=$3 AND i.asset_id=a.id)))
            """, connection, transaction);
        command.Parameters.AddWithValue(proposal.RequestedScope.AssetId!.Value); command.Parameters.AddWithValue(proposal.OwnerId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = proposal.RequestedScope.EditId is { } id ? id : DBNull.Value });
        if (await command.ExecuteScalarAsync(ct) is not true) throw new RecoveryWorkerException("restore_requested_scope_missing");
    }
}
