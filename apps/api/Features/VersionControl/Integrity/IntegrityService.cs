using System.Net;
using Amazon.S3;
using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Integrity;

public sealed class IntegrityService(NpgsqlDataSource dataSource, S3ObjectStore? storage = null, TimeProvider? clock = null)
{
    public async Task<GraphIntegrityResult> VerifyAsync(string owner, string library, CancellationToken cancellationToken)
    {
        using var measurement = VersionControlTelemetry.Measure("integrity.verify");
        VersionControlTelemetry.IntegrityVerifications.Add(1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var commits = new Dictionary<string, StoredCommit>(StringComparer.Ordinal);
        await using (var command = Command(connection, """
            SELECT c.id,c.library_id,c.manifest_bytes,c.author_id,c.authored_at,c.received_at,c.message,c.contract_version,c.policy_version,
                   COALESCE(array_agg(p.parent_id ORDER BY p.ordinal) FILTER (WHERE p.parent_id IS NOT NULL),ARRAY[]::bpchar[])
            FROM vc_commits c LEFT JOIN vc_commit_parents p ON p.commit_id=c.id
            WHERE c.owner_id=$1 AND c.library_id=$2 GROUP BY c.id,c.library_id,c.manifest_bytes,c.author_id,c.authored_at,c.received_at,c.message,c.contract_version,c.policy_version
            """, owner, library))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0).Trim();
                commits[id] = new(id, reader.GetString(1), (byte[])reader[2], reader.GetFieldValue<string[]>(9).Select(value => value.Trim()).ToArray(), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), []);
            }
        }

        var normalizedEntries = commits.Keys.ToDictionary(id => id, _ => new List<VersionControlEntry>(), StringComparer.Ordinal);
        await using (var command = Command(connection, """
            SELECT e.commit_id,e.asset_id,e.edit_id,e.change_kind,e.base_content_sha256,e.content_sha256,e.display_path,e.lifecycle_state
            FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
            WHERE c.owner_id=$1 AND c.library_id=$2 ORDER BY e.commit_id,e.ordinal
            """, owner, library))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) normalizedEntries[reader.GetString(0).Trim()].Add(new(reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4).Trim(), reader.IsDBNull(5) ? null : reader.GetString(5).Trim(), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        foreach (var id in commits.Keys.ToArray()) commits[id] = commits[id] with { Entries = normalizedEntries[id] };

        var content = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, "SELECT sha256 FROM content_objects WHERE category='xmp' AND integrity_state='authoritative'"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) content.Add(reader.GetString(0).Trim());
        var result = GraphIntegrity.Verify(library, commits.Values, content);
        var invalidProvenance = await ScalarAsync(connection, """
            SELECT 1 FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
            WHERE c.owner_id=$1 AND c.library_id=$2 AND EXISTS(
                SELECT 1 FROM unnest(ARRAY[e.base_content_sha256,e.content_sha256]) digest
                WHERE digest IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM content_objects o WHERE o.sha256=digest AND o.category='xmp' AND o.integrity_state='authoritative'
                      AND o.storage_key<>'' AND o.storage_version IS NOT NULL
                      AND (EXISTS(SELECT 1 FROM edit_identities x JOIN assets a ON a.id=x.asset_id WHERE x.id=e.edit_id AND x.asset_id=e.asset_id AND a.owner_id=$1 AND x.xmp_sha256=o.sha256)
                           OR EXISTS(SELECT 1 FROM upload_sessions u JOIN assets a ON a.id=u.asset_id WHERE u.owner_id=$1 AND a.owner_id=$1 AND u.asset_id=e.asset_id AND u.edit_id IS NOT DISTINCT FROM e.edit_id AND u.expected_sha256=o.sha256 AND u.category='xmp' AND u.state='completed' AND u.verified_at IS NOT NULL))))
            LIMIT 1
            """, cancellationToken, owner, library) is not null;
        if (invalidProvenance && !result.Codes.Contains("invalid_content_provenance", StringComparer.Ordinal))
            result = result with { Healthy = false, Codes = result.Codes.Append("invalid_content_provenance").Order(StringComparer.Ordinal).ToArray() };
        if (storage is not null)
        {
            var codes = result.Codes.ToHashSet(StringComparer.Ordinal);
            await using var command = Command(connection, """
                SELECT DISTINCT o.sha256,o.byte_length,o.storage_key,o.storage_version
                FROM vc_commit_entries e JOIN vc_commits c ON c.id=e.commit_id
                JOIN content_objects o ON o.sha256=e.base_content_sha256 OR o.sha256=e.content_sha256
                WHERE c.owner_id=$1 AND c.library_id=$2 AND o.category='xmp' AND o.integrity_state='authoritative'
                  AND o.storage_key<>'' AND o.storage_version IS NOT NULL
                """, owner, library);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var address = new ContentAddress(reader.GetString(0).Trim(), reader.GetInt64(1));
                try { await storage.VerifyStoredAsync(reader.GetString(2), reader.GetString(3), address, cancellationToken); }
                catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode is "NoSuchKey" or "NoSuchVersion")
                {
                    codes.Add("missing_object");
                }
                catch (InvalidDataException)
                {
                    codes.Add("altered_object");
                }
                catch (ArgumentException)
                {
                    codes.Add("invalid_content_provenance");
                }
            }
            if (codes.Count != result.Codes.Count) result = result with { Healthy = false, Codes = codes.Order(StringComparer.Ordinal).ToArray() };
        }
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
