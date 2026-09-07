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
        await using var command = dataSource.CreateCommand("UPDATE publication_intents SET state = 'pending-publication', retry_count = retry_count + 1, repair_evidence = jsonb_set(repair_evidence, '{lastRepair}', to_jsonb(now())), updated_at = now() WHERE state IN ('publishing', 'failed') AND updated_at < now() - interval '15 minutes'");
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

    public async Task MarkPromotedAsync(Guid intentId, PromotedContent promoted, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("WITH valid AS (SELECT item.intent_id, item.content_sha256 FROM publication_intent_items item JOIN publication_intents intent ON intent.id = item.intent_id JOIN content_objects content ON content.sha256 = item.content_sha256 JOIN assets asset ON asset.id = item.asset_id JOIN upload_sessions session ON session.id = item.upload_session_id WHERE item.intent_id = $1 AND item.content_sha256 = $2 AND content.storage_key = $3 AND intent.payload ->> 'sha256' = item.content_sha256 AND (intent.payload ->> 'sessionId')::uuid = item.upload_session_id AND (intent.payload ->> 'length')::bigint = content.byte_length AND asset.original_sha256 = item.content_sha256 AND session.expected_sha256 = item.content_sha256 AND session.expected_length = content.byte_length AND session.state = 'verifying'), marked AS (UPDATE publication_intents SET object_state = 'promoted', updated_at = now() WHERE id IN (SELECT intent_id FROM valid) AND state = 'publishing' RETURNING id) UPDATE content_objects SET storage_version = $4 WHERE sha256 IN (SELECT content_sha256 FROM valid) AND EXISTS (SELECT 1 FROM marked)");
        command.Parameters.AddWithValue(intentId); command.Parameters.AddWithValue(promoted.Address.Sha256); command.Parameters.AddWithValue(promoted.Address.AuthoritativeKey); command.Parameters.AddWithValue(promoted.StorageVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("publication_intent_not_claimed");
    }

    public async Task MarkFailedAsync(Guid intentId, string code, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("WITH failed AS (UPDATE publication_intents SET state = 'failed', retry_count = retry_count + 1, repair_evidence = jsonb_set(repair_evidence, '{lastFailure}', to_jsonb($2::text)), updated_at = now() WHERE id = $1 AND state = 'publishing' RETURNING id) INSERT INTO audit_events (id, owner_id, operation_id, event_type, outcome, reason_code) SELECT $3, session.owner_id, 'publication/' || failed.id::text, 'publication.completed', 'failed', $2 FROM failed JOIN publication_intent_items item ON item.intent_id = failed.id JOIN upload_sessions session ON session.id = item.upload_session_id");
        command.Parameters.AddWithValue(intentId); command.Parameters.AddWithValue(code); command.Parameters.AddWithValue(Guid.NewGuid());
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
                SELECT item.intent_id FROM publication_intent_items AS item
                JOIN publication_intents AS intent ON intent.id = item.intent_id
                JOIN content_objects AS content ON content.sha256 = item.content_sha256
                JOIN assets AS asset ON asset.id = item.asset_id
                JOIN upload_sessions AS session ON session.id = item.upload_session_id
                WHERE item.intent_id = $1
                    AND intent.payload ->> 'sha256' = item.content_sha256
                    AND (intent.payload ->> 'sessionId')::uuid = item.upload_session_id
                    AND asset.original_sha256 = item.content_sha256
                    AND (intent.payload ->> 'length')::bigint = content.byte_length
                    AND session.expected_sha256 = item.content_sha256 AND session.expected_length = content.byte_length
                    AND session.state = 'verifying'
                    AND content.storage_key = 'sha256/' || item.content_sha256
                    AND content.storage_version IS NOT NULL
                GROUP BY item.intent_id HAVING count(*) = 1 AND bool_and(content.integrity_state IN ('pending-publication', 'authoritative') AND asset.state = 'prepared')
            ), activated AS (
                UPDATE assets SET state = 'active', lifecycle_version = lifecycle_version + 1
                WHERE state = 'prepared' AND id IN (SELECT asset_id FROM publication_intent_items WHERE intent_id IN (SELECT intent_id FROM ready)) RETURNING id
            ), published AS (
                UPDATE publication_intents SET state = 'published', updated_at = now()
                WHERE id = $1 AND state = 'publishing' AND object_state = 'promoted' AND id IN (SELECT intent_id FROM ready) AND EXISTS (SELECT 1 FROM activated) RETURNING id
            ), authoritative AS (
                UPDATE content_objects SET integrity_state = 'authoritative'
                WHERE sha256 IN (SELECT content_sha256 FROM publication_intent_items WHERE intent_id IN (SELECT id FROM published))
            )
            INSERT INTO audit_events (id, owner_id, operation_id, event_type, outcome)
            SELECT $2, session.owner_id, 'publication/' || published.id::text, 'publication.completed', 'published'
            FROM published JOIN publication_intent_items item ON item.intent_id = published.id JOIN upload_sessions session ON session.id = item.upload_session_id;
            """;
        command.Parameters.AddWithValue(intentId); command.Parameters.AddWithValue(Guid.NewGuid());
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
