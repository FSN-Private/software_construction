using Npgsql;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed class IntegrityService(NpgsqlDataSource dataSource, TimeProvider? clock = null)
{
    public async Task<GraphIntegrityResult> VerifyAsync(string owner, string library, CancellationToken cancellationToken)
    {
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

        var result = GraphIntegrity.Verify(library, commits);
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
