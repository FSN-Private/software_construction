-- Expand-only: old readers and immutable content identities remain valid.
ALTER TABLE upload_sessions ALTER COLUMN state SET DEFAULT 'uploading';
ALTER TABLE upload_sessions ADD COLUMN logical_path text;
ALTER TABLE upload_sessions ADD COLUMN multipart_id text;
ALTER TABLE upload_sessions ADD COLUMN staging_version text;
ALTER TABLE upload_sessions ADD COLUMN category text;
ALTER TABLE upload_sessions ADD COLUMN outcome_code text;
ALTER TABLE upload_sessions ADD COLUMN asset_id uuid REFERENCES assets(id);
ALTER TABLE upload_sessions ADD COLUMN edit_id uuid REFERENCES edit_identities(id);
ALTER TABLE upload_sessions ADD COLUMN verified_at timestamptz;
ALTER TABLE upload_sessions ADD COLUMN quarantine_until timestamptz;
ALTER TABLE upload_sessions ADD COLUMN operation_id text NOT NULL DEFAULT 'op_legacy';
ALTER TABLE edit_identities ADD COLUMN state text NOT NULL DEFAULT 'active' CHECK (state IN ('active','recoverable-deleted','restored','integrity-error'));
ALTER TABLE edit_identities ADD COLUMN lifecycle_version bigint NOT NULL DEFAULT 1;
ALTER TABLE edit_identities ADD COLUMN deleted_until timestamptz;
ALTER TABLE edit_identities ADD COLUMN handoff_id uuid NOT NULL DEFAULT gen_random_uuid();
ALTER TABLE assets ADD COLUMN format text;
ALTER TABLE assets ADD COLUMN handoff_id uuid NOT NULL DEFAULT gen_random_uuid();
ALTER TABLE audit_events ADD COLUMN resource_id uuid;
ALTER TABLE reconstruction_manifests ADD COLUMN owner_id text;
CREATE INDEX ingest_pending_sessions ON upload_sessions(batch_id, state);
CREATE INDEX owner_assets ON assets(owner_id, state, id);
CREATE INDEX owner_audits ON audit_events(owner_id, resource_id);
CREATE INDEX asset_path_history ON asset_paths(asset_id, effective_from);
CREATE INDEX active_holds ON lifecycle_holds(asset_id);
CREATE INDEX edit_content ON edit_identities(xmp_sha256);
CREATE TABLE ingest_publications (
    batch_id uuid PRIMARY KEY REFERENCES import_batches(id),
    attempts integer NOT NULL DEFAULT 0,
    last_error text,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE asset_mutations (owner_id text NOT NULL, idempotency_key text NOT NULL, fingerprint text NOT NULL, result jsonb NOT NULL, PRIMARY KEY(owner_id,idempotency_key));
