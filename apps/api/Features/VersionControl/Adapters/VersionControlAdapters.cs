using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using System.Text.Json;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class VersionControlAdapters
{
    public static async Task ValidateEntriesAsync(NpgsqlConnection connection, string owner, string library,
        IReadOnlyList<VersionControlEntry> entries, S3ObjectStore? storage, CancellationToken cancellationToken)
    {
        await LockAndCheckRecoveryPurgeScopeAsync(connection, owner, library, entries, cancellationToken);
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
                          OR EXISTS(SELECT 1 FROM gallery_web_content g WHERE g.owner_id=$2 AND g.asset_id=d.asset_id AND g.edit_id=d.edit_id AND g.sha256=o.sha256)
                          OR EXISTS(SELECT 1 FROM lineage_merge_content m WHERE m.owner_id=$2 AND m.asset_id=d.asset_id AND m.edit_id=d.edit_id AND m.sha256=o.sha256)),
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

    private static async Task LockAndCheckRecoveryPurgeScopeAsync(NpgsqlConnection connection, string owner,
        string library, IReadOnlyList<VersionControlEntry> entries, CancellationToken cancellationToken)
    {
        var digests = entries.SelectMany(entry => new[] { entry.BaseContentSha256, entry.ContentSha256 })
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var assets = entries.Select(entry => entry.AssetId).Distinct().Order().ToArray();

        // A frozen payload is global by digest, even when a new logical reference belongs to
        // another owner. Lock content before assets everywhere in this path. The exclusive
        // digest lock is intentional: weaker key-share locks do not make an old RR snapshot
        // fail after the freeze's MVCC update.
        await LockAndCheckRecoveryPurgeDigestsAsync(connection, digests, cancellationToken);

        if (assets.Length > 0)
            await using (var lockAssets = Command(connection,
                "SELECT id FROM assets WHERE id=ANY($1) ORDER BY id FOR UPDATE", assets))
            await using (var reader = await lockAssets.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) { }

        if (await ScalarAsync(connection, """
            SELECT 1 FROM recovery_purge_proposals
             WHERE owner_id=$1 AND library_id=$2 AND phase IN ('executing','primary_cleanup')
               AND (asset_id=ANY($3) OR content_sha256=ANY($4::char(64)[]))
             LIMIT 1
            """, cancellationToken, owner, library, (object)assets, (object)digests) is not null)
            throw new VersionControlProblem("recovery_purge_frozen", 409);
    }

    public static async Task LockAndCheckRecoveryPurgeDigestsAsync(NpgsqlConnection connection,
        IEnumerable<string> requestedDigests, CancellationToken cancellationToken)
    {
        if (await Platform.Storage.IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
                connection, requestedDigests, cancellationToken))
            throw new VersionControlProblem("recovery_purge_frozen", 409);
    }

    public static async Task PublishHoldsAsync(NpgsqlConnection connection, string commitId, IEnumerable<Guid> assets, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        var assetIds = assets.Distinct().ToArray();
        if (assetIds.Length == 0) return;
        var digests = new List<string>();
        await using (var affected = Command(connection, """
            SELECT DISTINCT digest FROM (
                SELECT base_content_sha256 AS digest FROM vc_commit_entries WHERE commit_id=$1
                UNION ALL
                SELECT content_sha256 AS digest FROM vc_commit_entries WHERE commit_id=$1
            ) entries WHERE digest IS NOT NULL ORDER BY digest
            """, commitId))
        await using (var reader = await affected.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) digests.Add(reader.GetString(0).Trim());
        if (await Platform.Storage.IngestDb.LockAndCheckRecoveryPurgeDigestsAsync(
                connection, digests, cancellationToken))
            throw new VersionControlProblem("recovery_purge_frozen", 409);

        await using (var lockAssets = Command(connection,
            "SELECT id FROM assets WHERE id=ANY($1) ORDER BY id FOR UPDATE", assetIds))
        await using (var reader = await lockAssets.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) { }
        if (await ScalarAsync(connection, """
            SELECT 1 FROM recovery_purge_proposals
             WHERE asset_id=ANY($1) AND phase IN ('executing','primary_cleanup') LIMIT 1
            """, cancellationToken, assetIds) is not null)
            throw new VersionControlProblem("recovery_purge_frozen", 409);
        var holdIds = assetIds.Select(_ => Guid.NewGuid()).ToArray();
        await ExecuteAsync(connection, """
            DELETE FROM lifecycle_holds WHERE asset_id=ANY($1) AND source_reference=$3;
            INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference,expires_at)
            SELECT h.id,h.asset_id,'version-control-history',$3,$4 FROM unnest($2::uuid[],$1::uuid[]) AS h(id,asset_id)
            """, cancellationToken, assetIds, holdIds, $"vc:{commitId}", expiresAt);
    }

    public static Task AuditAsync(NpgsqlConnection connection, string owner, string operationId, string eventType, Guid resourceId, string outcome, CancellationToken cancellationToken, string? code = null) =>
        Platform.Storage.IngestDb.AuditAsync(connection, owner, operationId, eventType, resourceId, outcome, cancellationToken, code);

    public static Task<long> RecordRecoveryChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string owner, string library, string operationId, string eventType, string entityType, string entityId,
        IReadOnlyCollection<string> contentDigests, CancellationToken cancellationToken) =>
        Platform.Storage.IngestDb.RecordRecoveryChangeAsync(connection, transaction, owner, library, operationId,
            eventType, entityType, entityId, contentDigests, DateTimeOffset.UtcNow, cancellationToken);
}
