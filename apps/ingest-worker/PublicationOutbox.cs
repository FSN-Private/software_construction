using Npgsql;

namespace Platform.Ingest.Worker;

public sealed class PublicationOutbox(NpgsqlDataSource dataSource)
{
    public async Task<Guid> EnqueueAsync(string payload, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("INSERT INTO publication_intents (id, state, object_state, payload) VALUES ($1, 'pending-publication', 'unverified', $2::jsonb)");
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(payload);
        await command.ExecuteNonQueryAsync(cancellationToken); return id;
    }
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("UPDATE publication_intents SET state = 'pending-publication', retry_count = retry_count + 1, repair_evidence = jsonb_set(repair_evidence, '{lastRepair}', to_jsonb(now())), updated_at = now() WHERE state = 'publishing' AND updated_at < now() - interval '15 minutes'");
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PublicationIntent?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            WITH next AS (
                SELECT id FROM publication_intents WHERE state = 'pending-publication'
                ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE publication_intents AS intent SET state = 'publishing', updated_at = now()
            FROM next WHERE intent.id = next.id
            RETURNING intent.id, intent.state, intent.retry_count, intent.payload::text;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PublicationIntent(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    public async Task MarkPromotedAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("UPDATE publication_intents SET object_state = 'promoted', updated_at = now() WHERE id = $1 AND state = 'publishing'");
        command.Parameters.AddWithValue(intentId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("publication_intent_not_claimed");
    }

    public async Task<bool> MarkPublishedAsync(Guid intentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ready AS (
                SELECT intent_id FROM publication_intent_items JOIN content_objects ON sha256 = content_sha256
                WHERE intent_id = $1 GROUP BY intent_id HAVING bool_and(integrity_state = 'pending-publication')
            ), published AS (
                UPDATE publication_intents SET state = 'published', updated_at = now()
                WHERE id = $1 AND state = 'publishing' AND object_state = 'promoted' AND id IN (SELECT intent_id FROM ready) RETURNING id
            ), authoritative AS (
                UPDATE content_objects SET integrity_state = 'authoritative'
                WHERE sha256 IN (SELECT content_sha256 FROM publication_intent_items WHERE intent_id IN (SELECT id FROM published))
            )
            UPDATE assets SET state = 'active', lifecycle_version = lifecycle_version + 1
            WHERE state = 'prepared' AND id IN (SELECT asset_id FROM publication_intent_items WHERE intent_id IN (SELECT id FROM published));
            """;
        command.Parameters.AddWithValue(intentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = "SELECT state = 'published' FROM publication_intents WHERE id = $1";
        state.Parameters.AddWithValue(intentId);
        var published = await state.ExecuteScalarAsync(cancellationToken) is true;
        if (published) await transaction.CommitAsync(cancellationToken); else await transaction.RollbackAsync(cancellationToken);
        return published;
    }
}
