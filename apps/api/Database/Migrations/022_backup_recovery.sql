-- Feature 011. This migration is additive, and is safe to execute repeatedly
-- when exercised directly by the recovery integration harness.
CREATE TABLE IF NOT EXISTS recovery_points (
    point_id uuid PRIMARY KEY,
    capture_id uuid NOT NULL UNIQUE,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    source_region text NOT NULL,
    captured_at_utc timestamptz NOT NULL,
    snapshot_id text NOT NULL,
    snapshot_lsn pg_lsn NOT NULL,
    snapshot_cutoff_utc timestamptz NOT NULL,
    change_sequence bigint NOT NULL CHECK (change_sequence >= 0),
    latest_changed_at_utc timestamptz,
    migration_digest char(64) NOT NULL CHECK (migration_digest ~ '^[0-9a-f]{64}$'),
    manifest_schema_version integer NOT NULL CHECK (manifest_schema_version > 0),
    manifest_sha256 char(64) NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    manifest_bytes bytea NOT NULL CHECK (octet_length(manifest_bytes) > 0),
    manifest jsonb NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    state text NOT NULL CHECK (state IN ('capturing','copying','verifying','verified','failed')),
    verified_at_utc timestamptz,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (owner_id, library_id, point_id),
    CHECK (snapshot_cutoff_utc <= captured_at_utc),
    CHECK ((state = 'verified') = (verified_at_utc IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS recovery_points_owner_latest
    ON recovery_points(owner_id, library_id, captured_at_utc DESC, point_id DESC);

-- This recovery-signed canonical envelope binds the database catalog row to
-- the independently verified manifest and exact-copy receipt set.
CREATE TABLE IF NOT EXISTS recovery_point_attestations (
    point_id uuid PRIMARY KEY REFERENCES recovery_points(point_id),
    worker_id text NOT NULL,
    signing_key_id text NOT NULL,
    signing_public_key_sha256 char(64) NOT NULL CHECK (signing_public_key_sha256 ~ '^[0-9a-f]{64}$'),
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_payload bytea NOT NULL CHECK (octet_length(canonical_payload) > 0),
    signature bytea NOT NULL CHECK (octet_length(signature) > 0),
    imported_at_utc timestamptz NOT NULL DEFAULT now()
);

-- Primary status storage keeps only the bounded, signed catalog summary here.
-- The payload contains seven aggregate class totals and build/checkpoint
-- digests, never object names, exact versions, paths, receipts or locators.
COMMENT ON COLUMN recovery_points.manifest_bytes IS
    'Feature 011 primary mirror: canonical signed compact catalog payload only; full object manifest stays in recovery-owned storage.';
COMMENT ON COLUMN recovery_points.manifest IS
    'Feature 011 primary mirror: parsed compact signed catalog summary only; must not contain per-object identities or locators.';

CREATE TABLE IF NOT EXISTS recovery_cycle_statuses (
    event_id uuid PRIMARY KEY,
    worker_id text NOT NULL,
    signing_key_id text NOT NULL,
    signing_public_key_sha256 char(64) NOT NULL CHECK (signing_public_key_sha256 ~ '^[0-9a-f]{64}$'),
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_payload bytea NOT NULL CHECK (octet_length(canonical_payload) > 0),
    signature bytea NOT NULL CHECK (octet_length(signature) > 0),
    capture_id uuid,
    recovery_point_id uuid,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    stage text NOT NULL CHECK (stage IN ('capture','copy','verification','publication')),
    failed_data_class text CHECK (failed_data_class IS NULL OR failed_data_class IN ('originals','xmp','version_graph','metadata','audit','configuration','recovery_records')),
    outcome text NOT NULL CHECK (outcome IN ('missing','incomplete','corrupt','inaccessible','pending_retrieval','failed')),
    safe_code text NOT NULL CHECK (safe_code IN ('capture_overdue','class_missing','manifest_mismatch','bytes_mismatch','graph_mismatch','audit_chain_mismatch','access_policy_failed','kms_unavailable','cold_retrieval_pending','copy_corrupt','alert_delivery_failed','observation_rejected','recovery_point_incomplete','recovery_point_integrity_failed','recovery_point_unavailable','recovery_copy_pending','future_timestamp','catalog_missing','catalog_invalid','capture_failed','copy_failed','verification_failed','publication_failed','snapshot_failed','coverage_incomplete')),
    recovery_worker_build_digest char(64) NOT NULL CHECK (recovery_worker_build_digest ~ '^[0-9a-f]{64}$'),
    imported_at_utc timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS recovery_cycle_statuses_owner_latest
    ON recovery_cycle_statuses(owner_id,library_id,observed_at_utc DESC,event_id DESC);

-- Non-object artifacts are explicit components too: the complete database
-- dump, migration identity, configuration, release and access evidence.
CREATE TABLE IF NOT EXISTS recovery_point_components (
    point_id uuid NOT NULL REFERENCES recovery_points(point_id),
    component_id uuid NOT NULL,
    data_class text NOT NULL CHECK (data_class IN ('audit','configuration','recovery_records')),
    component_kind text NOT NULL CHECK (component_kind IN ('database_dump','schema_migrations','configuration','release_artifact','access_policy','audit_checkpoint','object_inventory','other')),
    source_identity text NOT NULL,
    destination_identity text NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    destination_account text NOT NULL,
    destination_region text NOT NULL,
    destination_version_id text NOT NULL,
    verified_at_utc timestamptz NOT NULL,
    verifier_build text NOT NULL,
    PRIMARY KEY (point_id, component_id),
    UNIQUE (point_id, component_kind, source_identity)
);

-- Exact physical versions; logical identities are retained to ensure a
-- deduplicated object is purged only after every eligible reference is gone.
CREATE TABLE IF NOT EXISTS recovery_point_objects (
    point_id uuid NOT NULL REFERENCES recovery_points(point_id),
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    data_class text NOT NULL CHECK (data_class IN ('originals','xmp')),
    owner_id text NOT NULL,
    library_id text NOT NULL,
    asset_id uuid,
    edit_id uuid,
    commit_id text,
    source_bucket text NOT NULL,
    source_key text NOT NULL,
    source_version_id text NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    destination_account text NOT NULL,
    destination_region text NOT NULL,
    destination_bucket text NOT NULL,
    destination_key text NOT NULL,
    destination_version_id text,
    verified_at_utc timestamptz,
    verifier_build text,
    verification_status text NOT NULL CHECK (verification_status IN ('pending','verified','unavailable','corrupt','retrieval_pending')),
    PRIMARY KEY (point_id, ordinal),
    UNIQUE (point_id, source_bucket, source_key, source_version_id),
    UNIQUE (point_id, destination_bucket, destination_key, destination_version_id),
    CHECK (verification_status <> 'verified' OR (destination_version_id IS NOT NULL AND verified_at_utc IS NOT NULL AND verifier_build IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS recovery_point_objects_logical_scope
    ON recovery_point_objects(owner_id, library_id, asset_id, edit_id, data_class, sha256);

CREATE TABLE IF NOT EXISTS recovery_observations (
    observation_id uuid PRIMARY KEY,
    worker_id text NOT NULL,
    signing_key_id text NOT NULL,
    signing_public_key_sha256 char(64) NOT NULL CHECK (signing_public_key_sha256 ~ '^[0-9a-f]{64}$'),
    observed_at_utc timestamptz NOT NULL,
    recovery_point_id uuid,
    owner_id text,
    library_id text,
    manifest_sha256 char(64) CHECK (manifest_sha256 IS NULL OR manifest_sha256 ~ '^[0-9a-f]{64}$'),
    data_class text CHECK (data_class IS NULL OR data_class IN ('originals','xmp','version_graph','metadata','audit','configuration','recovery_records')),
    outcome text CHECK (outcome IS NULL OR outcome IN ('healthy','missing','incomplete','corrupt','inaccessible','pending_retrieval','failed')),
    expected_item_count bigint CHECK (expected_item_count IS NULL OR expected_item_count >= 0),
    expected_byte_length bigint CHECK (expected_byte_length IS NULL OR expected_byte_length >= 0),
    item_count bigint CHECK (item_count IS NULL OR item_count >= 0),
    byte_length bigint CHECK (byte_length IS NULL OR byte_length >= 0),
    safe_codes jsonb CHECK (safe_codes IS NULL OR jsonb_typeof(safe_codes) = 'array'),
    alert_delivery jsonb CHECK (alert_delivery IS NULL OR jsonb_typeof(alert_delivery) = 'object'),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    canonical_payload bytea NOT NULL,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    signature bytea NOT NULL CHECK (octet_length(signature) > 0),
    imported_at_utc timestamptz NOT NULL DEFAULT now(),
    import_state text NOT NULL CHECK (import_state IN ('verified','rejected')),
    safe_result_code text NOT NULL,
    CHECK (import_state <> 'verified' OR (recovery_point_id IS NOT NULL AND owner_id IS NOT NULL AND library_id IS NOT NULL AND manifest_sha256 IS NOT NULL AND data_class IS NOT NULL AND outcome IS NOT NULL AND expected_item_count IS NOT NULL AND expected_byte_length IS NOT NULL AND item_count IS NOT NULL AND byte_length IS NOT NULL AND safe_codes IS NOT NULL)),
    UNIQUE (worker_id, observation_id)
);
ALTER TABLE recovery_observations ADD COLUMN IF NOT EXISTS signing_public_key_sha256 char(64);
CREATE INDEX IF NOT EXISTS recovery_observations_owner_latest
    ON recovery_observations(owner_id,library_id,data_class,observed_at_utc DESC) WHERE import_state='verified';
CREATE INDEX IF NOT EXISTS recovery_observations_point_latest
    ON recovery_observations(recovery_point_id,owner_id,library_id,manifest_sha256,data_class,observed_at_utc DESC,observation_id DESC)
    WHERE import_state='verified';
CREATE INDEX IF NOT EXISTS recovery_observations_point_alert
    ON recovery_observations(recovery_point_id,owner_id,library_id,manifest_sha256,observed_at_utc DESC)
    WHERE import_state='verified' AND alert_delivery IS NOT NULL;

-- Permanent malformed/untrusted queue deliveries are acknowledged only after
-- their body digest and allow-listed disposition have been durably recorded.
CREATE TABLE IF NOT EXISTS recovery_observation_rejections (
    rejection_id uuid PRIMARY KEY,
    body_sha256 char(64) NOT NULL CHECK (body_sha256 ~ '^[0-9a-f]{64}$'),
    safe_code text NOT NULL CHECK (safe_code IN ('invalid_envelope','untrusted_signer','invalid_signature','digest_mismatch','invalid_payload','observation_scope_mismatch','manifest_mismatch','replay_conflict')),
    rejected_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (body_sha256,safe_code)
);
CREATE INDEX IF NOT EXISTS recovery_observation_rejections_recent ON recovery_observation_rejections(rejected_at_utc DESC);

-- A producer must admit every recovery write before calling storage. Unknown
-- outcomes remain fenced until provider evidence proves settlement/quiescence.
CREATE TABLE IF NOT EXISTS recovery_write_intents (
    intent_id uuid PRIMARY KEY,
    point_id uuid REFERENCES recovery_points(point_id),
    session_id uuid,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    data_class text NOT NULL,
    asset_id uuid,
    edit_id uuid,
    source_bucket text NOT NULL,
    source_key text NOT NULL,
    source_version_id text NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    destination_bucket text NOT NULL,
    destination_key text NOT NULL UNIQUE,
    writer_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('admitted','storage_pending','unknown','settled','cleaned')),
    provider_version_id text,
    provider_receipt text,
    storage_pending boolean NOT NULL DEFAULT false,
    writer_stopped boolean NOT NULL DEFAULT false,
    admitted_at_utc timestamptz NOT NULL DEFAULT now(),
    settled_at_utc timestamptz,
    CHECK (state <> 'settled' OR settled_at_utc IS NOT NULL),
    CHECK (state <> 'unknown' OR settled_at_utc IS NULL)
);
CREATE INDEX IF NOT EXISTS recovery_write_intents_scope
    ON recovery_write_intents(owner_id, library_id, asset_id, edit_id, state);

-- The watermark row is locked and incremented inside each source mutation's
-- transaction. This gives recovery captures a commit-ordered acknowledgement
-- sequence rather than a non-transactional sequence with commit gaps.
CREATE TABLE IF NOT EXISTS recovery_change_watermarks (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
    latest_changed_at_utc timestamptz,
    PRIMARY KEY (owner_id, library_id)
);
CREATE TABLE IF NOT EXISTS recovery_change_ledger (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    operation_id text NOT NULL,
    event_type text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    object_references jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(object_references) = 'array'),
    changed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (owner_id, library_id, sequence),
    UNIQUE (owner_id, library_id, operation_id, event_type, entity_type, entity_id)
);

CREATE TABLE IF NOT EXISTS recovery_sessions (
    session_id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    recovery_point_id uuid NOT NULL REFERENCES recovery_points(point_id),
    idempotency_key text NOT NULL,
    request_digest char(64) NOT NULL CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    scope jsonb NOT NULL CHECK (jsonb_typeof(scope) = 'object'),
    isolated_namespace text NOT NULL UNIQUE,
    state text NOT NULL CHECK (state IN ('requested','restoring','validating','ready','promoting','promoted','aborted','failed','expired')),
    validation_digest char(64) CHECK (validation_digest IS NULL OR validation_digest ~ '^[0-9a-f]{64}$'),
    expected_live_generation bigint CHECK (expected_live_generation IS NULL OR expected_live_generation >= 0),
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    expires_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    UNIQUE (owner_id, library_id, idempotency_key),
    UNIQUE (session_id, owner_id, library_id)
);
CREATE INDEX IF NOT EXISTS recovery_sessions_pending ON recovery_sessions(state, expires_at_utc);

CREATE TABLE IF NOT EXISTS recovery_validation_revisions (
    session_id uuid NOT NULL REFERENCES recovery_sessions(session_id),
    revision bigint NOT NULL CHECK (revision > 0),
    source_manifest_sha256 char(64) NOT NULL CHECK (source_manifest_sha256 ~ '^[0-9a-f]{64}$'),
    validation_digest char(64) NOT NULL CHECK (validation_digest ~ '^[0-9a-f]{64}$'),
    state text NOT NULL CHECK (state IN ('verified','failed')),
    findings jsonb NOT NULL CHECK (jsonb_typeof(findings) = 'array'),
    validated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (session_id, revision),
    UNIQUE (session_id, validation_digest)
);

CREATE TABLE IF NOT EXISTS recovery_holds (
    hold_id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    recovery_point_id uuid REFERENCES recovery_points(point_id),
    session_id uuid REFERENCES recovery_sessions(session_id),
    asset_id uuid,
    edit_id uuid,
    reason_code text NOT NULL,
    source_reference text NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    expires_at_utc timestamptz,
    released_at_utc timestamptz,
    CHECK (recovery_point_id IS NOT NULL OR session_id IS NOT NULL OR asset_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS recovery_holds_scope ON recovery_holds(owner_id,library_id,asset_id,edit_id,expires_at_utc) WHERE released_at_utc IS NULL;

-- This durable tombstone is the admission fence shared by source reads,
-- renders, exports, restore and backup writers. It intentionally retains
-- identity and digest evidence after media bytes are physically deleted.
CREATE TABLE IF NOT EXISTS media_purge_tombstones (
    event_id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    asset_id uuid NOT NULL,
    edit_id uuid,
    category text NOT NULL CHECK (category IN ('original','xmp')),
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    lifecycle_generation bigint NOT NULL CHECK (lifecycle_generation > 0),
    evidence_id uuid NOT NULL,
    plan_digest char(64) NOT NULL CHECK (plan_digest ~ '^[0-9a-f]{64}$'),
    proposed_at_utc timestamptz NOT NULL DEFAULT now(),
    recovery_acknowledged_at_utc timestamptz,
    completed_at_utc timestamptz,
    CHECK (category = 'original' OR edit_id IS NOT NULL),
    UNIQUE (owner_id,library_id,asset_id,edit_id,category,lifecycle_generation)
);
CREATE INDEX IF NOT EXISTS media_purge_tombstone_scope
    ON media_purge_tombstones(owner_id,library_id,asset_id,edit_id,category,lifecycle_generation);
CREATE OR REPLACE FUNCTION media_purge_tombstone_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'immutable_media_purge_tombstone'; END IF;
    IF (to_jsonb(NEW) - ARRAY['recovery_acknowledged_at_utc','completed_at_utc']) IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['recovery_acknowledged_at_utc','completed_at_utc']) OR
       (OLD.recovery_acknowledged_at_utc IS NOT NULL AND NEW.recovery_acknowledged_at_utc IS DISTINCT FROM OLD.recovery_acknowledged_at_utc) OR
       (OLD.completed_at_utc IS NOT NULL AND NEW.completed_at_utc IS DISTINCT FROM OLD.completed_at_utc) THEN
        RAISE EXCEPTION 'immutable_media_purge_tombstone';
    END IF;
    RETURN NEW;
END; $$;
DROP TRIGGER IF EXISTS media_purge_tombstone_immutable ON media_purge_tombstones;
CREATE TRIGGER media_purge_tombstone_immutable BEFORE UPDATE OR DELETE ON media_purge_tombstones
    FOR EACH ROW EXECUTE FUNCTION media_purge_tombstone_immutable();

CREATE TABLE IF NOT EXISTS recovery_purge_journals (
    operation_id uuid PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE REFERENCES media_purge_tombstones(event_id),
    owner_id text NOT NULL,
    library_id text NOT NULL,
    asset_id uuid NOT NULL,
    edit_id uuid,
    category text NOT NULL CHECK (category IN ('original','xmp')),
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    expected_lifecycle_version bigint NOT NULL CHECK (expected_lifecycle_version > 0),
    lifecycle_generation bigint NOT NULL CHECK (lifecycle_generation > 0),
    eligible_at_utc timestamptz NOT NULL,
    plan_digest char(64) NOT NULL CHECK (plan_digest ~ '^[0-9a-f]{64}$'),
    plan jsonb NOT NULL CHECK (jsonb_typeof(plan) = 'object'),
    primary_actor text NOT NULL,
    reason_code text NOT NULL,
    phase text NOT NULL CHECK (phase IN ('proposal','awaiting_independent_approval','approved','tombstoned','export_pending','writers_pending','deleting','verifying','completed','failed')),
    independent_signer_id text,
    independent_key_id text,
    independent_receipt bytea,
    independently_approved_at_utc timestamptz,
    failure_code text,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    completed_at_utc timestamptz,
    UNIQUE (owner_id,library_id,asset_id,edit_id,category,lifecycle_generation),
    CHECK ((independent_receipt IS NULL) = (independent_key_id IS NULL)),
    CHECK (phase NOT IN ('proposal','awaiting_independent_approval') OR independent_receipt IS NULL),
    CHECK (phase NOT IN ('approved','tombstoned','export_pending','writers_pending','deleting','verifying','completed') OR independent_receipt IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS recovery_purge_pending ON recovery_purge_journals(phase,updated_at_utc) WHERE phase NOT IN ('completed','failed');

CREATE TABLE IF NOT EXISTS recovery_purge_objects (
    operation_id uuid NOT NULL REFERENCES recovery_purge_journals(operation_id),
    object_id uuid NOT NULL,
    failure_domain text NOT NULL CHECK (failure_domain IN ('primary','recovery')),
    category text NOT NULL,
    bucket text NOT NULL,
    object_key text NOT NULL,
    version_id text NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    delete_state text NOT NULL CHECK (delete_state IN ('pending','deleted','verified_absent','outcome_unknown','retained_shared')),
    provider_receipt text,
    verified_at_utc timestamptz,
    PRIMARY KEY (operation_id,object_id),
    UNIQUE (operation_id,failure_domain,bucket,object_key,version_id)
);

-- Signed, independently MFA-approved identity bindings. A recovery runtime
-- resolves verified issuer/audience/sub claims to the preserved owner ID;
-- clients never submit the mapped owner ID.
CREATE TABLE IF NOT EXISTS recovery_identity_mappings (
    mapping_id uuid PRIMARY KEY,
    issuer text NOT NULL,
    audience text NOT NULL,
    subject text NOT NULL,
    owner_id text NOT NULL,
    approved_by text NOT NULL,
    approval_key_id text NOT NULL,
    approval_receipt bytea NOT NULL CHECK (octet_length(approval_receipt) > 0),
    approved_at_utc timestamptz NOT NULL,
    revoked_at_utc timestamptz,
    UNIQUE (issuer,audience,subject,mapping_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS recovery_identity_active_subject
    ON recovery_identity_mappings(issuer,audience,subject) WHERE revoked_at_utc IS NULL;

-- The chain writer serializes on its singleton state row. Trust starts only
-- when a signed recovery-owned checkpoint matches an offline owner pin.
CREATE TABLE IF NOT EXISTS recovery_audit_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
    last_event_sha256 char(64) NOT NULL DEFAULT repeat('0',64) CHECK (last_event_sha256 ~ '^[0-9a-f]{64}$')
);
INSERT INTO recovery_audit_state(singleton) VALUES (true) ON CONFLICT (singleton) DO NOTHING;
CREATE TABLE IF NOT EXISTS recovery_audit_events (
    sequence bigint PRIMARY KEY CHECK (sequence > 0),
    event_id uuid NOT NULL UNIQUE,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    event_type text NOT NULL,
    outcome text NOT NULL,
    safe_resource_id text,
    occurred_at_utc timestamptz NOT NULL,
    previous_sha256 char(64) NOT NULL CHECK (previous_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_event bytea NOT NULL,
    event_sha256 char(64) NOT NULL CHECK (event_sha256 ~ '^[0-9a-f]{64}$'),
    signing_key_id text NOT NULL,
    signature bytea NOT NULL CHECK (octet_length(signature) > 0),
    UNIQUE (previous_sha256,event_sha256)
);
CREATE TABLE IF NOT EXISTS recovery_audit_checkpoints (
    checkpoint_id uuid PRIMARY KEY,
    sequence bigint NOT NULL,
    event_sha256 char(64) NOT NULL CHECK (event_sha256 ~ '^[0-9a-f]{64}$'),
    signer_key_id text NOT NULL,
    signature bytea NOT NULL CHECK (octet_length(signature) > 0),
    recovery_account text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    offline_pin_id text NOT NULL
);
CREATE OR REPLACE FUNCTION recovery_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'immutable_recovery_evidence'; END; $$;
DROP TRIGGER IF EXISTS recovery_change_ledger_immutable ON recovery_change_ledger;
CREATE TRIGGER recovery_change_ledger_immutable BEFORE UPDATE OR DELETE ON recovery_change_ledger FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_audit_events_immutable ON recovery_audit_events;
CREATE TRIGGER recovery_audit_events_immutable BEFORE UPDATE OR DELETE ON recovery_audit_events FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_audit_checkpoints_immutable ON recovery_audit_checkpoints;
CREATE TRIGGER recovery_audit_checkpoints_immutable BEFORE UPDATE OR DELETE ON recovery_audit_checkpoints FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_validation_revisions_immutable ON recovery_validation_revisions;
CREATE TRIGGER recovery_validation_revisions_immutable BEFORE UPDATE OR DELETE ON recovery_validation_revisions FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_observations_immutable ON recovery_observations;
CREATE TRIGGER recovery_observations_immutable BEFORE UPDATE OR DELETE ON recovery_observations FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_point_attestations_immutable ON recovery_point_attestations;
CREATE TRIGGER recovery_point_attestations_immutable BEFORE UPDATE OR DELETE ON recovery_point_attestations FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_cycle_statuses_immutable ON recovery_cycle_statuses;
CREATE TRIGGER recovery_cycle_statuses_immutable BEFORE UPDATE OR DELETE ON recovery_cycle_statuses FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_observation_rejections_immutable ON recovery_observation_rejections;
CREATE TRIGGER recovery_observation_rejections_immutable BEFORE UPDATE OR DELETE ON recovery_observation_rejections FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_point_components_immutable ON recovery_point_components;
CREATE TRIGGER recovery_point_components_immutable BEFORE UPDATE OR DELETE ON recovery_point_components FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
DROP TRIGGER IF EXISTS recovery_point_objects_immutable ON recovery_point_objects;
CREATE TRIGGER recovery_point_objects_immutable BEFORE UPDATE OR DELETE ON recovery_point_objects FOR EACH ROW EXECUTE FUNCTION recovery_append_only();
