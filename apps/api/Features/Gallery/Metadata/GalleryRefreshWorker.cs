using Npgsql;

namespace Platform.Api.Features.Gallery.Metadata;

public sealed class GalleryRefreshWorker(NpgsqlDataSource db, GalleryRefreshService refresh, IConfiguration configuration, ILogger<GalleryRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var library = configuration["Gallery:LibraryId"];
        if (string.IsNullOrWhiteSpace(library)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var connection = await db.OpenConnectionAsync(stoppingToken);
                await using var command = new NpgsqlCommand("""
                    SELECT owner_id,branch_name FROM (
                        SELECT DISTINCT owner_id,'main'::text AS branch_name FROM assets
                        UNION
                        SELECT DISTINCT r.owner_id,r.normalized_name FROM vc_refs r
                        WHERE r.library_id=@library AND r.kind='branch' AND EXISTS (SELECT 1 FROM assets a WHERE a.owner_id=r.owner_id)
                    ) branches
                    WHERE NOT EXISTS (SELECT 1 FROM gallery_refresh g WHERE g.owner_id=branches.owner_id AND g.library_id=@library AND g.branch_name=branches.branch_name AND g.state='complete')
                       OR EXISTS (SELECT 1 FROM gallery_refresh g WHERE g.owner_id=branches.owner_id AND g.library_id=@library AND g.branch_name=branches.branch_name AND g.state IN ('queued','running','failed'))
                    ORDER BY owner_id,branch_name LIMIT 100
                    """, connection);
                command.Parameters.AddWithValue("library", library);
                var pending = new List<(string Owner, string Branch)>();
                await using (var reader = await command.ExecuteReaderAsync(stoppingToken))
                    while (await reader.ReadAsync(stoppingToken)) pending.Add((reader.GetString(0), reader.GetString(1)));
                foreach (var item in pending)
                {
                    try { await refresh.RefreshAsync(item.Owner, library, item.Branch, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        // Owner IDs, paths, tags and exception messages are private gallery metadata.
                        logger.LogWarning("Gallery refresh failed {ErrorType}", error.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Gallery refresh discovery failed {ErrorType}", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
