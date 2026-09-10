using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed class IntegrityService(NpgsqlDataSource dataSource, TimeProvider? clock = null)
{
    public async Task<GraphIntegrityResult> VerifyAsync(string owner, string library, CancellationToken cancellationToken)
    {
        using var measurement = VersionControlTelemetry.Measure("integrity.verify");
        VersionControlTelemetry.IntegrityVerifications.Add(1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var commits = new List<StoredCommit>();
        await using (var command = Command(connection, """
            SELECT c.id,c.library_id,c.manifest_bytes,
                   COALESCE(array_agg(p.parent_id ORDER BY p.ordinal) FILTER (WHERE p.parent_id IS NOT NULL),ARRAY[]::bpchar[])
            FROM vc_commits c LEFT JOIN vc_commit_parents p ON p.commit_id=c.id
            WHERE c.owner_id=$1 AND c.library_id=$2 GROUP BY c.id,c.library_id,c.manifest_bytes
            """, owner, library))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) commits.Add(new(reader.GetString(0).Trim(), reader.GetString(1), (byte[])reader[2], reader.GetFieldValue<string[]>(3).Select(value => value.Trim()).ToArray()));
        }

        var content = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, "SELECT sha256 FROM content_objects WHERE category='xmp' AND integrity_state='authoritative'"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) content.Add(reader.GetString(0).Trim());
        var result = GraphIntegrity.Verify(library, commits, content);
        var crossScope = await ScalarAsync(connection, """
            SELECT 1
            WHERE EXISTS(
                SELECT 1 FROM vc_commit_parents p
                JOIN vc_commits child ON child.id=p.commit_id
                JOIN vc_commits parent ON parent.id=p.parent_id
                WHERE child.owner_id=$1 AND child.library_id=$2
                  AND (parent.owner_id<>$1 OR parent.library_id<>$2))
               OR EXISTS(
                SELECT 1 FROM vc_commit_entries e
                JOIN vc_commits c ON c.id=e.commit_id JOIN assets a ON a.id=e.asset_id
                WHERE c.owner_id=$1 AND c.library_id=$2 AND a.owner_id<>$1)
            """, cancellationToken, owner, library) is not null;
        if (crossScope && !result.Codes.Contains("cross_library_reference", StringComparer.Ordinal))
        {
            VersionControlTelemetry.IntegrityFailures.Add(1, new KeyValuePair<string, object?>("library", "redacted"));
            result = result with { Healthy = false, Codes = result.Codes.Append("cross_library_reference").Order(StringComparer.Ordinal).ToArray() };
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (result.Healthy)
        {
            await ExecuteAsync(connection, "DELETE FROM vc_ref_quarantine WHERE owner_id=$1 AND library_id=$2", cancellationToken, owner, library);
        }
        else
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            await ExecuteAsync(connection, """
                INSERT INTO vc_ref_quarantine(owner_id,library_id,normalized_ref,reason_code,detected_at)
                SELECT owner_id,library_id,normalized_name,$3,$4 FROM vc_refs WHERE owner_id=$1 AND library_id=$2
                ON CONFLICT(owner_id,library_id,normalized_ref) DO UPDATE SET reason_code=EXCLUDED.reason_code,detected_at=EXCLUDED.detected_at
                """, cancellationToken, owner, library, string.Join(',', result.Codes), now);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
