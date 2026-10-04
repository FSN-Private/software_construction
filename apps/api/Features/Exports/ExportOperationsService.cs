using Npgsql;
using Platform.Contracts.Exports;
using System.Security.Claims;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed record ExportOperationsView(bool Paused, bool Draining, long Pending, long Active, long CleanupPending, long UnknownWrites, long Failed);
public sealed class ExportOperationsService(NpgsqlDataSource source, IConfiguration config)
{
    private string Operator(ClaimsPrincipal user)
    {
        var name = user.FindFirst("cognito:username")?.Value;
        if (user.Identity?.IsAuthenticated != true || !user.HasClaim("cognito:groups", "operator") || string.IsNullOrEmpty(name) || name != config["Exports:OperationsUsername"]) throw new ExportProblem("export_operation_forbidden", 403);
        return name;
    }
    public async Task<ExportOperationsView> GetAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        _ = Operator(user); await using var db = await source.OpenConnectionAsync(ct); return await ReadAsync(db, ct);
    }
    public async Task<ExportOperationsView> SetAsync(ClaimsPrincipal user, string action, CancellationToken ct)
    {
        var actor = Operator(user); if (action is not ("pause" or "resume" or "drain" or "cancel-pending")) throw new ExportProblem("invalid_export_operation", 400);
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        await ExecuteAsync(db, "SELECT singleton FROM export_operations WHERE singleton FOR UPDATE", ct);
        await ExecuteAsync(db, "UPDATE export_operations SET paused=$1,draining=$2 WHERE singleton", ct, action != "resume", action == "drain");
        if (action == "cancel-pending") await ExecuteAsync(db, "UPDATE export_requests SET state='cancelling',reason='cancelled',generation=generation+1 WHERE state IN ('pending','retryable','preparing'); UPDATE export_download_grants g SET revoked_at=now() FROM export_requests r WHERE g.request_id=r.id AND r.state='cancelling'", ct);
        await AuditAsync(db, actor, ExportDatabase.OperationId, "exports.operation_" + action.Replace('-', '_'), Guid.Empty, "accepted", ct);
        var result = await ReadAsync(db, ct); await tx.CommitAsync(ct); return result;
    }
    private static async Task<ExportOperationsView> ReadAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var paused = (bool)(await ScalarAsync(db, "SELECT paused FROM export_operations WHERE singleton", ct))!; var draining = (bool)(await ScalarAsync(db, "SELECT draining FROM export_operations WHERE singleton", ct))!;
        async Task<long> Count(string sql) => Convert.ToInt64(await ScalarAsync(db, sql, ct));
        return new(paused, draining, await Count("SELECT count(*) FROM export_requests WHERE state IN ('pending','retryable')"), await Count("SELECT count(*) FROM export_requests WHERE state='preparing'"), await Count("SELECT count(*) FROM export_requests WHERE state='cancelling'"), await Count("SELECT count(*) FROM export_write_intents WHERE state IN ('unknown','admitted')"), await Count("SELECT count(*) FROM export_requests WHERE state='failed'"));
    }
}
