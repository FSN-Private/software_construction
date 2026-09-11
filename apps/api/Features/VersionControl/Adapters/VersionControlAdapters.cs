using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class VersionControlAdapters
{
    public static async Task ValidateEntriesAsync(NpgsqlConnection connection, string owner, IReadOnlyList<VersionControlEntry> entries, S3ObjectStore? storage, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            await using var identity = Command(connection, """
                SELECT 1 FROM assets a
                LEFT JOIN edit_identities e ON e.id=$2 AND e.asset_id=a.id
                WHERE a.id=$1 AND a.owner_id=$3 AND a.state IN ('active','restored')
                  AND ($2::uuid IS NULL OR e.id IS NOT NULL AND e.state IN ('active','restored'))
                """, entry.AssetId, entry.EditId, owner);
            if (await identity.ExecuteScalarAsync(cancellationToken) is null) throw new VersionControlProblem("cross_library_reference", 403);

            foreach (var digest in new[] { entry.BaseContentSha256, entry.ContentSha256 }.Where(value => value is not null).Distinct(StringComparer.Ordinal))
            {
                await using var content = Command(connection, """
                    SELECT o.byte_length,o.storage_key,o.storage_version FROM content_objects o
                    WHERE o.sha256=$1 AND o.category='xmp' AND o.integrity_state='authoritative'
                      AND o.storage_key<>'' AND o.storage_version IS NOT NULL
                      AND (EXISTS(
                            SELECT 1 FROM edit_identities e JOIN assets a ON a.id=e.asset_id
                            WHERE e.id=$2 AND e.asset_id=$3 AND a.owner_id=$4 AND e.xmp_sha256=o.sha256)
                           OR EXISTS(
                            SELECT 1 FROM upload_sessions u JOIN assets a ON a.id=u.asset_id
                            WHERE u.owner_id=$4 AND a.owner_id=$4 AND u.asset_id=$3
                              AND u.edit_id IS NOT DISTINCT FROM $2 AND u.expected_sha256=o.sha256
                              AND u.category='xmp' AND u.state='completed' AND u.verified_at IS NOT NULL))
                    """, digest, entry.EditId, entry.AssetId, owner);
                await using var reader = await content.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new VersionControlProblem("content_not_authoritative");
                if (storage is not null)
                {
                    try { await storage.VerifyStoredAsync(reader.GetString(1), reader.GetString(2), new(digest!, reader.GetInt64(0)), cancellationToken); }
                    catch (Exception error) when (error is Amazon.S3.AmazonS3Exception or InvalidDataException or ArgumentException) { throw new VersionControlProblem("content_not_authoritative"); }
                }
            }
        }
    }

    public static async Task PublishHoldsAsync(NpgsqlConnection connection, string commitId, IEnumerable<Guid> assets, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        foreach (var asset in assets.Distinct())
        {
            await ExecuteAsync(connection, "DELETE FROM lifecycle_holds WHERE asset_id=$1 AND source_reference=$2; INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference,expires_at) VALUES($3,$1,'version-control-history',$2,$4)", cancellationToken, asset, $"vc:{commitId}", Guid.NewGuid(), expiresAt);
        }
    }

    public static Task AuditAsync(NpgsqlConnection connection, string owner, string operationId, string eventType, Guid resourceId, string outcome, CancellationToken cancellationToken, string? code = null) =>
        Platform.Storage.IngestDb.AuditAsync(connection, owner, operationId, eventType, resourceId, outcome, cancellationToken, code);
}
