using Npgsql;
using Amazon.Runtime;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed class IntegrityVerificationWorker(NpgsqlDataSource dataSource, IntegrityService integrity, ILogger<IntegrityVerificationWorker> logger, IConfiguration configuration) : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("VersionControl:IntegrityInterval", DefaultInterval);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("Version-control integrity interval must be between zero and ten minutes.");
        using var timer = new PeriodicTimer(interval);
        do { await VerifyOnceAsync(stoppingToken); } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task VerifyOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var scopes = new List<(string Owner, string Library)>();
            await using (var command = Command(connection, "SELECT DISTINCT owner_id,library_id FROM vc_commits ORDER BY owner_id,library_id"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) scopes.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var scope in scopes)
            {
                try
                {
                    var result = await integrity.VerifyAsync(scope.Owner, scope.Library, cancellationToken);
                    if (result.Healthy) logger.LogInformation("Version-control integrity heartbeat {AuditEventType} {Scope} {CommitsChecked}", "version-control.integrity.heartbeat", "redacted", result.CommitsChecked);
                    else logger.LogError("Version-control integrity failure {AuditEventType} {Scope} {ReasonCode}", "version-control.integrity.failure", "redacted", string.Join(',', result.Codes));
                }
                catch (Exception error) when (error is NpgsqlException or AmazonServiceException or HttpRequestException or TimeoutException or IOException)
                {
                    logger.LogError("Version-control integrity verification unavailable {AuditEventType} {Scope} {ReasonCode}", "version-control.integrity.failure", "redacted", "verification_unavailable");
                }
            }
            if (scopes.Count == 0) logger.LogInformation("Version-control integrity heartbeat {AuditEventType} {Scope} {CommitsChecked}", "version-control.integrity.heartbeat", "empty", 0);
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or IOException)
        {
            logger.LogError("Version-control integrity verification unavailable {AuditEventType} {Scope} {ReasonCode}", "version-control.integrity.failure", "redacted", "verification_unavailable");
        }
    }
}
