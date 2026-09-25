using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using System.Text.Json;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class VersionControlAdapters
{
    public static async Task ValidateEntriesAsync(NpgsqlConnection connection, string owner, IReadOnlyList<VersionControlEntry> entries, S3ObjectStore? storage, CancellationToken cancellationToken)
    {
        var requested = JsonSerializer.Serialize(entries.Select(entry => new
        {
            asset_id = entry.AssetId,
            edit_id = entry.EditId,
            base_digest = entry.BaseContentSha256,
            content_digest = entry.ContentSha256,
        }));
        if (await ScalarAsync(connection, """
            WITH requested AS (
                SELECT * FROM jsonb_to_recordset($1::jsonb) AS r(asset_id uuid,edit_id uuid,base_digest text,content_digest text))
            SELECT 1 FROM requested r WHERE NOT EXISTS(
                SELECT 1 FROM assets a LEFT JOIN edit_identities e ON e.id=r.edit_id AND e.asset_id=a.id
                WHERE a.id=r.asset_id AND a.owner_id=$2 AND a.state IN ('active','restored')
                  AND (r.edit_id IS NULL OR e.id IS NOT NULL AND e.state IN ('active','restored'))) LIMIT 1
            """, cancellationToken, requested, owner) is not null) throw new VersionControlProblem("cross_library_reference", 403);

        var storedObjects = new Dictionary<string, (string Key, string Version, ContentAddress Address)>(StringComparer.Ordinal);
        await using (var content = Command(connection, """
            WITH requested AS (
                SELECT * FROM jsonb_to_recordset($1::jsonb) AS r(asset_id uuid,edit_id uuid,base_digest text,content_digest text)),
            digests AS (
                SELECT DISTINCT r.asset_id,r.edit_id,d.digest FROM requested r
                CROSS JOIN LATERAL (VALUES(r.base_digest),(r.content_digest)) d(digest) WHERE d.digest IS NOT NULL)
            SELECT d.digest,
                   o.sha256 IS NOT NULL AND o.category='xmp' AND o.integrity_state='authoritative' AND o.storage_key<>'' AND o.storage_version IS NOT NULL
                     AND (EXISTS(SELECT 1 FROM edit_identities e JOIN assets a ON a.id=e.asset_id WHERE e.id=d.edit_id AND e.asset_id=d.asset_id AND a.owner_id=$2 AND e.xmp_sha256=o.sha256)
                          OR EXISTS(SELECT 1 FROM upload_sessions u JOIN assets a ON a.id=u.asset_id WHERE u.owner_id=$2 AND a.owner_id=$2 AND u.asset_id=d.asset_id AND u.edit_id IS NOT DISTINCT FROM d.edit_id AND u.expected_sha256=o.sha256 AND u.category='xmp' AND u.state='completed' AND u.verified_at IS NOT NULL)
                          OR EXISTS(SELECT 1 FROM gallery_web_content g WHERE g.owner_id=$2 AND g.asset_id=d.asset_id AND g.edit_id=d.edit_id AND g.sha256=o.sha256)),
                   o.byte_length,o.storage_key,o.storage_version
            FROM digests d LEFT JOIN content_objects o ON o.sha256=d.digest
            """, requested, owner))
        await using (var reader = await content.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(1)) throw new VersionControlProblem("content_not_authoritative");
                var digest = reader.GetString(0);
                if (storage is not null) storedObjects.TryAdd(digest, (reader.GetString(3), reader.GetString(4), new(digest, reader.GetInt64(2))));
            }
        }
        if (storage is not null)
            try
            {
                await Parallel.ForEachAsync(storedObjects.Values, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken }, async (item, token) =>
                    await storage.VerifyStoredAsync(item.Key, item.Version, item.Address, token));
            }
            catch (Exception error) when (error is Amazon.S3.AmazonS3Exception or InvalidDataException or ArgumentException) { throw new VersionControlProblem("content_not_authoritative"); }
    }

    public static async Task PublishHoldsAsync(NpgsqlConnection connection, string commitId, IEnumerable<Guid> assets, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        var assetIds = assets.Distinct().ToArray();
        if (assetIds.Length == 0) return;
        var holdIds = assetIds.Select(_ => Guid.NewGuid()).ToArray();
        await ExecuteAsync(connection, """
            DELETE FROM lifecycle_holds WHERE asset_id=ANY($1) AND source_reference=$3;
            INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference,expires_at)
            SELECT h.id,h.asset_id,'version-control-history',$3,$4 FROM unnest($2::uuid[],$1::uuid[]) AS h(id,asset_id)
            """, cancellationToken, assetIds, holdIds, $"vc:{commitId}", expiresAt);
    }

    public static Task AuditAsync(NpgsqlConnection connection, string owner, string operationId, string eventType, Guid resourceId, string outcome, CancellationToken cancellationToken, string? code = null) =>
        Platform.Storage.IngestDb.AuditAsync(connection, owner, operationId, eventType, resourceId, outcome, cancellationToken, code);
}
