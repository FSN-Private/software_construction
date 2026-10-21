using Microsoft.Extensions.Configuration;
using Npgsql;
using Platform.Api.Features.Rendering;
using Platform.Contracts.Rendering;
using Platform.Ingest.Worker;
using static Platform.Storage.IngestDb;

internal static class RenderingRollbackChecks
{
    public static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("RENDERING_ROLLBACK_INTEGRATION") != "1" && Environment.GetEnvironmentVariable("INGEST_INTEGRATION") != "1") return;
        var schema = "render_rollback_" + Guid.NewGuid().ToString("N");
        var port = Environment.GetEnvironmentVariable("INGEST_POSTGRES_PORT") ?? "54329";
        var connectionString = $"Host=127.0.0.1;Port={port};Username=ingest;Password=ingest-test-only;Database=ingest";
        await using var admin = NpgsqlDataSource.Create(connectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = NpgsqlDataSource.Create(connectionString + ";Search Path=" + schema);
            await DatabaseMigrator.ApplyAsync(db, default);
            await using var connection = await db.OpenConnectionAsync();
            var original = (string)(await ScalarAsync(connection, "SELECT row_to_json(c)::text FROM rendering_compatibility c WHERE matrix_version='rendering-v1'", default))!;
            var priorDigest = (string)(await ScalarAsync(connection, "SELECT renderer_artifact_digest FROM rendering_compatibility WHERE matrix_version='rendering-v1'", default))!;
            var config = Configuration("sha256:" + new string('a', 64));
            var operations = new RenderOperationsService(db, config);
            foreach (var activated in new[] { false, true })
            {
                var receipt = (await operations.SetAsync("admit", "owner", "operator", default)).RollbackReceiptId!.Value;
                if (activated) await operations.SetAsync("activate", "owner", "operator", default); // Response deliberately discarded.
                var before = await StateAsync(connection);
                await RejectAsync(() => RenderPromotionRollback.RestoreAsync(db, config, receipt, "foreign", priorDigest, default), 403);
                await RejectAsync(() => RenderPromotionRollback.RestoreAsync(db, config, Guid.NewGuid(), "operator", priorDigest, default), 409);
                await RejectAsync(() => RenderPromotionRollback.RestoreAsync(db, config, receipt, "operator", "sha256:" + new string('b', 64), default), 409);
                IngestChecks.Check(await StateAsync(connection) == before, "Rejected rollback must not mutate compatibility/intake/candidates.");
                await RenderPromotionRollback.RestoreAsync(db, config, receipt, "operator", priorDigest, default);
                await RenderPromotionRollback.RestoreAsync(db, config, receipt, "operator", priorDigest, default);
                IngestChecks.Check(await ScalarAsync(connection, "SELECT row_to_json(c)::text FROM rendering_compatibility c WHERE matrix_version='rendering-v1'", default) as string == original,
                    "Rollback/replay must restore every prior compatibility field, including approval metadata.");
                IngestChecks.Check(await operations.IsPausedAsync(default) && !await operations.IsAcceptanceOwnerAsync("owner", default), "Rollback must close intake throughout ECS restoration.");
                var priorService = new RenderingService(db, new Resolver(), Configuration(priorDigest));
                IngestChecks.Check((await priorService.ExportCompatibilityAsync(connection, default)).Digest == priorDigest, "Restored API/task digest must pass the real RenderingService compatibility query.");
                await operations.SetAsync("resume", "owner", "operator", default);
                IngestChecks.Check(!await operations.IsPausedAsync(default), "Intake resumes only after the prior service is compatible.");
            }
            var stale = (await operations.SetAsync("admit", "owner", "operator", default)).RollbackReceiptId!.Value;
            var latest = (await operations.SetAsync("admit", "owner", "operator", default)).RollbackReceiptId!.Value;
            var unchanged = await StateAsync(connection);
            await RejectAsync(() => RenderPromotionRollback.RestoreAsync(db, config, stale, "operator", priorDigest, default), 409);
            IngestChecks.Check(await StateAsync(connection) == unchanged, "Superseded receipt must mutate nothing.");
            await ExecuteAsync(connection, "UPDATE rendering_compatibility SET renderer_artifact_digest=$1 WHERE matrix_version='rendering-v1'", default, "sha256:" + new string('c', 64));
            unchanged = await StateAsync(connection);
            await RejectAsync(() => RenderPromotionRollback.RestoreAsync(db, config, latest, "operator", priorDigest, default), 409);
            IngestChecks.Check(await StateAsync(connection) == unchanged, "A third compatibility state must never be overwritten by rollback.");
            await ExecuteAsync(connection, "UPDATE rendering_compatibility SET renderer_artifact_digest=$1 WHERE matrix_version='rendering-v1'", default, priorDigest);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var concurrentReceipt = (await operations.SetAsync("admit", "owner", "operator", default)).RollbackReceiptId!.Value;
                async Task ActivateAsync()
                {
                    try { await operations.SetAsync("activate", "owner", "operator", default); }
                    catch (RenderingProblem problem) when (problem.Code == "render_promotion_rejected") { }
                }
                await Task.WhenAll(ActivateAsync(), RenderPromotionRollback.RestoreAsync(db, config, concurrentReceipt, "operator", priorDigest, default));
                IngestChecks.Check(await ScalarAsync(connection, "SELECT row_to_json(c)::text FROM rendering_compatibility c WHERE matrix_version='rendering-v1'", default) as string == original && await operations.IsPausedAsync(default),
                    "Concurrent activation/rollback must serialize to the exact prior approval with intake closed.");
            }
            Console.WriteLine("Rendering rollback PostgreSQL checks passed: pre/post activation, lost response, full approval restoration, guarded receipts, real service compatibility and intake closure.");
        }
        finally
        {
            await using var cleanup = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static IConfiguration Configuration(string digest) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Rendering:DeployedArtifactDigest"] = digest,
        ["Rendering:PromotionEvidenceId"] = "trusted-linux-rollback-test",
        ["Rendering:OperationsUsername"] = "operator"
    }).Build();

    private static async Task<string> StateAsync(NpgsqlConnection connection) => (string)(await ScalarAsync(connection,
        "SELECT json_build_object('compatibility',(SELECT row_to_json(c) FROM rendering_compatibility c WHERE matrix_version='rendering-v1'),'intake',(SELECT row_to_json(i) FROM render_intake_control i),'candidates',(SELECT json_agg(c ORDER BY matrix_version,renderer_artifact_digest) FROM rendering_compatibility_candidates c))::text", default))!;

    private static async Task RejectAsync(Func<Task<RenderOperationsView>> action, int status)
    {
        try { await action(); } catch (RenderingProblem problem) when (problem.Status == status) { return; }
        throw new InvalidOperationException("Expected guarded rendering rollback rejection.");
    }

    private sealed class Resolver : ISelectedVersionResolver
    {
        public Task<SelectedVersionResolution> ResolveAsync(string ownerId, string libraryId, Guid assetId, Guid editId, string selectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
