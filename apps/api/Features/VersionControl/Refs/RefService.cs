using Npgsql;
using Platform.Api.Features.VersionControl.Adapters;
using Platform.Contracts.VersionControl;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.VersionControl.Rewrite;
using Platform.Api.Features.VersionControl.Projection;
using Platform.Api.Features.VersionControl.Integrity;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Refs;

public sealed class RefService(NpgsqlDataSource dataSource, IntegrityService? integrity = null, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private IntegrityService Integrity => integrity ?? new IntegrityService(dataSource, clock: clock);

    public Task<RefView> CreateAsync(string owner, string library, string kind, RefRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken) =>
        CreateAsync(owner, library, kind, request, idempotencyKey, operationId, null, cancellationToken);

    internal async Task<RefView> CreateAsync(string owner, string library, string kind, RefRequest request, string idempotencyKey, string operationId, object? fingerprintPayload, CancellationToken cancellationToken)
    {
        using var measurement = VersionControlTelemetry.Measure("ref.create");
        if (kind is not ("branch" or "tag")) throw new VersionControlProblem("invalid_request", 400);
        if (request.CommitId is null) throw new VersionControlProblem("invalid_request", 400);
        var name = RefNames.Normalize(request.Name);
        var fingerprint = VersionControlIdempotency.Fingerprint(fingerprintPayload ?? new { operation = "ref.create", kind, request });
        await using (var replayConnection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            var earlyReplay = await VersionControlIdempotency.ReadAsync<RefView>(replayConnection, owner, library, idempotencyKey, fingerprint, cancellationToken);
            if (earlyReplay is not null) return earlyReplay;
        }
        if (!(await Integrity.VerifyAsync(owner, library, cancellationToken)).Healthy) throw new VersionControlProblem("graph_corrupt");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await HistoryRetention.LockAsync(connection, owner, library, cancellationToken);
        await ProjectionWriter.LockAsync(connection, owner, library, cancellationToken);
        await RefNames.LockAsync(connection, owner, library, name, cancellationToken);
        var replay = await VersionControlIdempotency.ReadAsync<RefView>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay;
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_ref_quarantine WHERE owner_id=$1 AND library_id=$2 LIMIT 1", cancellationToken, owner, library) is not null) throw new VersionControlProblem("graph_corrupt");
        if (await ScalarAsync(connection, "SELECT 1 FROM vc_commits WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND integrity_state='verified'", cancellationToken, request.CommitId, owner, library) is null) throw new VersionControlProblem("cross_library_reference", 403);
        try
        {
            await ExecuteAsync(connection, "INSERT INTO vc_refs(owner_id,library_id,normalized_name,display_name,kind,head_commit_id,protected) VALUES($1,$2,$3,$4,$5,$6,false)", cancellationToken, owner, library, name, request.Name, kind, request.CommitId);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation) { throw Conflict(); }
        var tagAuditId = kind == "tag" ? await TagAuditAsync(connection, owner, operationId,
            "created", name, request.CommitId, cancellationToken) : (Guid?)null;
        await ExecuteAsync(connection, "INSERT INTO vc_reflog(id,owner_id,library_id,normalized_name,old_head,new_head,actor_id,operation,reason,occurred_at,audit_event_id) VALUES($1,$2,$3,$4,NULL,$5,$2,'create',$6,$7,$8)", cancellationToken, Guid.NewGuid(), owner, library, name, request.CommitId, $"created {kind}", Now, tagAuditId);
        if (kind != "tag") await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.branch.created", StableResource(name), "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            $"version-control.{kind}.created", "ref", name, [request.CommitId], cancellationToken);
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        await ProjectionWriter.ReplaceAsync(connection, owner, library, name, request.CommitId, graph, Now, cancellationToken);
        await HistoryRetention.UpdateExpiryAsync(connection, RewriteService.Reachable(graph, request.CommitId), null, cancellationToken);
        var result = new RefView(name, kind, request.CommitId, false, 1);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, request.CommitId, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.RefMovements.Add(1, new KeyValuePair<string, object?>("operation", "create"));
        return result;
    }

    public async Task<IReadOnlyList<RefView>> ListAsync(string owner, string library, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, "SELECT normalized_name,kind,head_commit_id,protected,version FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND head_commit_id IS NOT NULL ORDER BY normalized_name LIMIT 1000", owner, library);
        var result = new List<RefView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetBoolean(3), reader.GetInt64(4)));
        return result;
    }

    public async Task<RefView> RenameAsync(string owner, string library, string currentName, RefRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        using var measurement = VersionControlTelemetry.Measure("ref.rename");
        var current = RefNames.Normalize(currentName);
        var next = RefNames.Normalize(request.Name);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await HistoryRetention.LockAsync(connection, owner, library, cancellationToken);
        await ProjectionWriter.LockAsync(connection, owner, library, cancellationToken);
        foreach (var lockedName in new[] { current, next }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) await RefNames.LockAsync(connection, owner, library, lockedName, cancellationToken);
        var fingerprint = VersionControlIdempotency.Fingerprint(new { operation = "ref.rename", current, request });
        var replay = await VersionControlIdempotency.ReadAsync<RefView>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay;
        var existing = await ReadForUpdateAsync(connection, owner, library, current, cancellationToken) ?? throw new VersionControlProblem("ref_not_found", 404);
        if (existing.Protected || existing.Kind == "tag") { await RejectedAsync(connection, owner, operationId, current, "protected_ref", cancellationToken); await transaction.CommitAsync(cancellationToken); throw new VersionControlProblem("protected_ref"); }
        if (request.ExpectedVersion != existing.Version) throw Conflict();
        if (await ScalarAsync(connection, """
            SELECT 1 WHERE EXISTS(SELECT 1 FROM vc_working_changes WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3)
               OR EXISTS(SELECT 1 FROM vc_staging_sets WHERE owner_id=$1 AND library_id=$2 AND branch_name=$3 AND status='open')
               OR EXISTS(SELECT 1 FROM vc_checkouts WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3)
               OR EXISTS(SELECT 1 FROM vc_stashes WHERE owner_id=$1 AND library_id=$2 AND source_ref=$3)
            """, cancellationToken, owner, library, current) is not null) throw new VersionControlProblem("ref_in_use");
        try { await ExecuteAsync(connection, "UPDATE vc_refs SET normalized_name=$4,display_name=$5,version=version+1 WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", cancellationToken, owner, library, current, next, request.Name); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation) { throw Conflict(); }
        await ExecuteAsync(connection, "UPDATE vc_materialized_state SET normalized_ref=$4 WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3", cancellationToken, owner, library, current, next);
        await ExecuteAsync(connection, "INSERT INTO vc_reflog(id,owner_id,library_id,normalized_name,old_head,new_head,actor_id,operation,reason,occurred_at) VALUES($1,$2,$3,$4,$5,$5,$2,'rename',$6,$7)", cancellationToken, Guid.NewGuid(), owner, library, next, existing.Head, $"renamed from {current}", Now);
        await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.branch.renamed", StableResource(next), "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            "version-control.branch.renamed", "ref", next, existing.Head is null ? [] : [existing.Head], cancellationToken);
        var result = new RefView(next, existing.Kind, existing.Head!, false, existing.Version + 1);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, existing.Head, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.RefMovements.Add(1, new KeyValuePair<string, object?>("operation", "rename"));
        return result;
    }

    public async Task<VersionControlResult> DeleteAsync(string owner, string library, string nameValue, RefRequest request, string idempotencyKey, string operationId, CancellationToken cancellationToken)
    {
        using var measurement = VersionControlTelemetry.Measure("ref.delete");
        var name = RefNames.Normalize(nameValue);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await HistoryRetention.LockAsync(connection, owner, library, cancellationToken);
        await ProjectionWriter.LockAsync(connection, owner, library, cancellationToken);
        await RefNames.LockAsync(connection, owner, library, name, cancellationToken);
        var fingerprint = VersionControlIdempotency.Fingerprint(new { operation = "ref.delete", name, request });
        var replay = await VersionControlIdempotency.ReadAsync<VersionControlResult>(connection, owner, library, idempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay with { OperationId = operationId };
        var existing = await ReadForUpdateAsync(connection, owner, library, name, cancellationToken) ?? throw new VersionControlProblem("ref_not_found", 404);
        if (existing.Protected || existing.Kind == "tag" && !request.Confirm)
        {
            await RejectedAsync(connection, owner, operationId, name, existing.Protected ? "protected_ref" : "confirmation_required", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new VersionControlProblem(existing.Protected ? "protected_ref" : "confirmation_required");
        }
        if (request.ExpectedVersion != existing.Version) throw Conflict();
        var graph = await QueryService.LoadAsync(connection, owner, library, cancellationToken);
        var retained = HistoryRetention.Reachable(graph, await HistoryRetention.RefHeadsAsync(connection, owner, library, name, cancellationToken));
        retained.UnionWith(await HistoryRetention.HiddenAsync(connection, graph, owner, library, cancellationToken));
        await ExecuteAsync(connection, "DELETE FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3", cancellationToken, owner, library, name);
        await ExecuteAsync(connection, "DELETE FROM vc_materialized_state WHERE owner_id=$1 AND library_id=$2 AND normalized_ref=$3", cancellationToken, owner, library, name);
        var expiry = Now.AddDays(90);
        var recoveryId = Guid.NewGuid();
        var deletedTagAuditId = existing.Kind == "tag" ? await TagAuditAsync(connection, owner, operationId,
            "deleted", name, existing.Head, cancellationToken) : (Guid?)null;
        await ExecuteAsync(connection, "INSERT INTO vc_reflog(id,owner_id,library_id,normalized_name,old_head,new_head,actor_id,operation,reason,occurred_at,recovery_expires_at,audit_event_id) VALUES($1,$2,$3,$4,$5,NULL,$2,'delete','confirmed ref deletion',$6,$7,$8)", cancellationToken, recoveryId, owner, library, name, existing.Head, Now, expiry, deletedTagAuditId);
        if (existing.Head is not null) await HistoryRetention.ReconcileAsync(connection, graph, owner, library, RewriteService.Reachable(graph, existing.Head), Now, cancellationToken);
        if (existing.Kind != "tag") await VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.branch.deleted", StableResource(name), "accepted", cancellationToken);
        await VersionControlAdapters.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            $"version-control.{existing.Kind}.deleted", "ref", name, existing.Head is null ? [] : [existing.Head], cancellationToken);
        var result = new VersionControlResult(operationId, existing.Head, name, existing.Version + 1, RecoveryId: recoveryId);
        await VersionControlIdempotency.SaveAsync(connection, owner, library, idempotencyKey, fingerprint, existing.Head, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        VersionControlTelemetry.RefMovements.Add(1, new KeyValuePair<string, object?>("operation", "delete"));
        return result;
    }

    internal static Guid StableResource(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static async Task<Guid> TagAuditAsync(NpgsqlConnection connection, string owner,
        string operationId, string action, string name, string? commitId, CancellationToken ct)
    {
        if (commitId is null) throw new VersionControlProblem("ref_not_found", 404);
        var auditId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO audit_events(id,owner_id,operation_id,event_type,resource_id,outcome)
            VALUES($1,$2,$3,$4,$5,'accepted')
            """, ct, auditId, owner, operationId, $"version-control.tag.{action}", StableResource(name));
        return auditId;
    }

    private static async Task<(string Kind, string? Head, bool Protected, long Version)?> ReadForUpdateAsync(NpgsqlConnection connection, string owner, string library, string name, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT kind,head_commit_id,protected,version FROM vc_refs WHERE owner_id=$1 AND library_id=$2 AND normalized_name=$3 FOR UPDATE", owner, library, name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1).Trim(), reader.GetBoolean(2), reader.GetInt64(3));
    }

    private static Task RejectedAsync(NpgsqlConnection connection, string owner, string operationId, string name, string code, CancellationToken cancellationToken) =>
        VersionControlAdapters.AuditAsync(connection, owner, operationId, "version-control.ref.rejected", StableResource(name), "denied", cancellationToken, code);

    private static VersionControlProblem Conflict()
    {
        VersionControlTelemetry.RefConflicts.Add(1);
        return new("ref_conflict");
    }
}
