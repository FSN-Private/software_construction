using Npgsql;
using Platform.Contracts.Exports;
using System.Security.Claims;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed record ExportOperationsView(bool Paused, bool Draining, long Pending, long Active, long CleanupPending, long UnknownWrites, long Failed, long BasketItems, double OldestQueueAgeSeconds, long Expired, long Cancelled, IReadOnlyDictionary<string, long> ItemOutcomes);
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
        if (action == "cancel-pending")
        {
            var requests = new List<(Guid Id, string Owner, string Library)>();
            await using (var cmd = Command(db, "SELECT id,owner_id,library_id FROM export_requests WHERE state IN ('pending','retryable','preparing') ORDER BY owner_id,library_id,id"))
            await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) requests.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
            foreach (var request in requests)
            {
                await ExportDatabase.LockAsync(db, request.Owner, request.Library, ct);
                if (await ExecuteAsync(db, "UPDATE export_requests SET state='cancelling',reason='cancelled',generation=generation+1 WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND state IN ('pending','retryable','preparing')", ct, request.Id, request.Owner, request.Library) == 0) continue;
                await ExecuteAsync(db, "UPDATE export_download_grants SET revoked_at=now() WHERE request_id=$1 AND revoked_at IS NULL", ct, request.Id);
                await ExecuteAsync(db, "UPDATE export_download_sessions SET revoked_at=now() WHERE id IN (SELECT session_id FROM export_download_grants WHERE request_id=$1) AND revoked_at IS NULL", ct, request.Id);
                await ExportDatabase.RecordChangeAsync(db, tx, request.Owner, request.Library, "exports.cancelled_by_operator", "export_request", request.Id.ToString("D"), await ExportDatabase.RequestContentDigestsAsync(db, request.Id, ct), ct, outcome: "cancelling");
            }
        }
        await ExecuteAsync(db, "SELECT singleton FROM export_operations WHERE singleton FOR UPDATE", ct);
        await ExecuteAsync(db, "UPDATE export_operations SET paused=$1,draining=$2 WHERE singleton", ct, action != "resume", action == "drain");
        await AuditAsync(db, actor, ExportDatabase.OperationId, "exports.operation_" + action.Replace('-', '_'), Guid.Empty, "accepted", ct);
        var result = await ReadAsync(db, ct); await tx.CommitAsync(ct); return result;
    }
    private static async Task<ExportOperationsView> ReadAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var paused = (bool)(await ScalarAsync(db, "SELECT paused FROM export_operations WHERE singleton", ct))!; var draining = (bool)(await ScalarAsync(db, "SELECT draining FROM export_operations WHERE singleton", ct))!;
        async Task<long> Count(string sql) => Convert.ToInt64(await ScalarAsync(db, sql, ct));
        var outcomes = new Dictionary<string, long>();
        await using (var cmd = Command(db, "SELECT outcome->>'status',count(*) FROM export_results GROUP BY outcome->>'status'"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) outcomes.Add(r.GetString(0), r.GetInt64(1));
        return new(paused, draining, await Count("SELECT count(*) FROM export_requests WHERE state IN ('pending','retryable')"), await Count("SELECT count(*) FROM export_requests WHERE state='preparing'"), await Count("SELECT count(*) FROM export_requests WHERE state='cancelling'"), await Count("SELECT count(*) FROM export_write_intents WHERE state IN ('unknown','admitted')"), await Count("SELECT count(*) FROM export_requests WHERE state='failed'"), await Count("SELECT count(*) FROM export_basket_items"), Convert.ToDouble(await ScalarAsync(db, "SELECT COALESCE(EXTRACT(EPOCH FROM now()-min(created_at)),0) FROM export_requests WHERE state IN ('pending','retryable')", ct)), await Count("SELECT count(*) FROM export_requests WHERE state='expired'"), await Count("SELECT count(*) FROM export_requests WHERE state IN ('cancelled','cancelling')"), outcomes);
    }
}
