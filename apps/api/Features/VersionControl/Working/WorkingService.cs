using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Api.Features.VersionControl.Refs;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Working;

public sealed class WorkingService(NpgsqlDataSource dataSource, S3ObjectStore? storage = null)
{
    public async Task<WorkingChangeView> PutAsync(string owner, string library, string client, WorkingChangeRequest request, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await PutInTransactionAsync(connection, transaction, owner, library, client, request, operationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkingChangeView> PutInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string owner, string library, string client,
        WorkingChangeRequest request, string operationId, CancellationToken cancellationToken)
    {
        var branch = RefNames.Normalize(request.Branch);
        var entry = new VersionControlEntry(request.AssetId, request.EditId, request.Change, request.BaseContentSha256, request.ContentSha256, request.Path, request.Lifecycle);
        _ = VersionControlManifestCodec.Write(new(library, [], owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "validate", [entry]));
        await LockClientAsync(connection, owner, library, client, cancellationToken);
        await RefNames.LockAsync(connection, owner, library, branch, cancellationToken);
        await VersionControlAdapters.ValidateEntriesAsync(connection, owner, [entry], storage, cancellationToken);

        var id = Guid.NewGuid();
        if (request.ExpectedVersion == 0)
        {
            try
            {
                var inserted = await ExecuteAsync(connection, """
                    INSERT INTO vc_working_changes(id,owner_id,library_id,client_id,branch_name,asset_id,edit_id,base_commit_id,base_content_sha256,current_content_sha256,change_kind,display_path,lifecycle_state)
                    SELECT $1,$2,$3,$4,$5,$6,$7,(SELECT head_commit_id FROM vc_refs WHERE owner_id=$2 AND library_id=$3 AND normalized_name=$5 AND kind='branch'),$8,$9,$10,$11,$12
                    WHERE $5='main' OR EXISTS(SELECT 1 FROM vc_refs WHERE owner_id=$2 AND library_id=$3 AND normalized_name=$5 AND kind='branch')
                    """, cancellationToken, id, owner, library, client, branch, request.AssetId, request.EditId, request.BaseContentSha256, request.ContentSha256, request.Change, request.Path, request.Lifecycle);
                if (inserted != 1) throw new VersionControlProblem("ref_not_found", 404);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation) { throw new VersionControlProblem("working_state_conflict"); }
        }
        else
        {
            var updated = await ExecuteAsync(connection, """
                UPDATE vc_working_changes SET base_content_sha256=$8,current_content_sha256=$9,change_kind=$10,display_path=$11,lifecycle_state=$12,version=version+1,updated_at=now()
                WHERE owner_id=$2 AND library_id=$3 AND client_id=$4 AND branch_name=$5 AND asset_id=$6 AND edit_id IS NOT DISTINCT FROM $7 AND version=$1
                """, cancellationToken, request.ExpectedVersion, owner, library, client, branch, request.AssetId, request.EditId, request.BaseContentSha256, request.ContentSha256, request.Change, request.Path, request.Lifecycle);
            if (updated != 1) throw new VersionControlProblem("working_state_conflict");
            id = (Guid)(await ScalarAsync(connection, "SELECT id FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 AND asset_id=$5 AND edit_id IS NOT DISTINCT FROM $6", cancellationToken, owner, library, client, branch, request.AssetId, request.EditId) ?? throw new VersionControlProblem("working_state_conflict"));
        }
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.working-change", id, "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "version-control.working-change", "working-change", id.ToString("D"),
            new[] { request.BaseContentSha256, request.ContentSha256 }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        return new(id, request.AssetId, request.EditId, branch, request.Change, request.BaseContentSha256, request.ContentSha256, request.Path, request.Lifecycle, request.ExpectedVersion + 1, false);
    }

    public async Task<IReadOnlyList<WorkingChangeView>> ListAsync(string owner, string library, string client, string branch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT w.id,w.asset_id,w.edit_id,w.branch_name,w.change_kind,w.base_content_sha256,w.current_content_sha256,w.display_path,w.lifecycle_state,w.version,
                   EXISTS(SELECT 1 FROM vc_staged_entries se JOIN vc_staging_sets s ON s.id=se.staging_set_id WHERE se.working_change_id=w.id AND s.status='open')
            FROM vc_working_changes w WHERE owner_id=$1 AND library_id=$2 AND client_id=$3 AND branch_name=$4 ORDER BY w.asset_id,w.edit_id,w.id
            """, owner, library, client, RefNames.Normalize(branch));
        var result = new List<WorkingChangeView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<Guid> StageAsync(string owner, string library, string client, string branchValue, StageRequest request, string operationId, CancellationToken cancellationToken)
    {
        if (request.WorkingChangeIds.Count == 0 || request.WorkingChangeIds.Distinct().Count() != request.WorkingChangeIds.Count || request.ExpectedVersion != 0) throw new VersionControlProblem("invalid_request", 400);
        var branch = RefNames.Normalize(branchValue);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockClientAsync(connection, owner, library, client, cancellationToken);
        await RefNames.LockAsync(connection, owner, library, branch, cancellationToken);
        if (branch != "main" && await ScalarAsync(connection, "SELECT 1 FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch) is null) throw new VersionControlProblem("ref_not_found", 404);
        var actualHead = (string?)await ScalarAsync(connection, "SELECT head_commit_id FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 AND kind='branch'", cancellationToken, owner, library, branch);
        actualHead = actualHead?.Trim();
        if (!string.Equals(actualHead, request.ExpectedHead, StringComparison.Ordinal)) throw new VersionControlProblem("stale_head");
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT INTO vc_staging_sets(id,owner_id,library_id,client_id,branch_name,expected_head,status) VALUES($1,$2,$3,$4,$5,$6,'open')", cancellationToken, id, owner, library, client, branch, request.ExpectedHead);
        for (var ordinal = 0; ordinal < request.WorkingChangeIds.Count; ordinal++)
        {
            var workingId = request.WorkingChangeIds[ordinal];
            var version = await ScalarAsync(connection, "SELECT version FROM vc_working_changes WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND client_id=$4 AND branch_name=$5", cancellationToken, workingId, owner, library, client, branch);
            if (version is not long workingVersion) throw new VersionControlProblem("working_change_not_found", 404);
            await ExecuteAsync(connection, "INSERT INTO vc_staged_entries(staging_set_id,ordinal,working_change_id,working_version) VALUES($1,$2,$3,$4)", cancellationToken, id, ordinal, workingId, workingVersion);
        }
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.staged", id, "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "version-control.staged", "staging-set", id.ToString("D"), [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task UnstageAsync(string owner, string library, Guid setId, Guid workingId, long expectedVersion, string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var client = await ScalarAsync(connection, "SELECT client_id FROM vc_staging_sets WHERE id=$1 AND owner_id=$2 AND library_id=$3", cancellationToken, setId, owner, library) as string ?? throw new VersionControlProblem("staging_conflict");
        await LockClientAsync(connection, owner, library, client, cancellationToken);
        var deleted = await ExecuteAsync(connection, "DELETE FROM vc_staged_entries e USING vc_staging_sets s WHERE e.staging_set_id=s.id AND s.id=$1 AND s.owner_id=$2 AND s.library_id=$3 AND s.version=$4 AND s.status='open' AND e.working_change_id=$5", cancellationToken, setId, owner, library, expectedVersion, workingId);
        if (deleted != 1) throw new VersionControlProblem("staging_conflict");
        await ExecuteAsync(connection, "UPDATE vc_staging_sets SET version=version+1 WHERE id=$1", cancellationToken, setId);
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.unstaged", setId, "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "version-control.unstaged", "staging-set", setId.ToString("D"), [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static WorkingChangeView Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5).Trim(), reader.IsDBNull(6) ? null : reader.GetString(6).Trim(), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9), reader.GetBoolean(10));

    public static Task LockClientAsync(NpgsqlConnection connection, string owner, string library, string client, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,6))", cancellationToken, owner + "\n" + library + "\n" + client);
}
