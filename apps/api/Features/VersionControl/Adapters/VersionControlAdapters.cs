using Npgsql;
using Platform.Contracts.VersionControl;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.VersionControl.Adapters;

public static class VersionControlAdapters
{
    public static async Task ValidateEntriesAsync(NpgsqlConnection connection, string owner, IReadOnlyList<VersionControlEntry> entries, CancellationToken cancellationToken)
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
                await using var content = Command(connection, "SELECT 1 FROM content_objects WHERE sha256=$1 AND integrity_state='authoritative'", digest);
                if (await content.ExecuteScalarAsync(cancellationToken) is null) throw new VersionControlProblem("content_not_authoritative");
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
