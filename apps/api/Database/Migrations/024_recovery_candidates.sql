-- Recovery-only unqualified restore candidates. These rows are written only inside
-- an isolated recovery clone; they do not reference primary health/catalog/session
-- rows and deliberately expose no ready, verified, promoted, or promotion state.
CREATE TABLE IF NOT EXISTS recovery_candidate_sessions (
    candidate_id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    capture_id uuid NOT NULL,
    recovery_point_id uuid NOT NULL,
    source_account_id char(12) NOT NULL CHECK (source_account_id ~ '^[0-9]{12}$'),
    source_region text NOT NULL,
    recovery_account_id char(12) NOT NULL CHECK (recovery_account_id ~ '^[0-9]{12}$'),
    recovery_region text NOT NULL,
    source_manifest_sha256 char(64) NOT NULL CHECK (source_manifest_sha256 ~ '^[0-9a-f]{64}$'),
    full_manifest_sha256 char(64) CHECK (full_manifest_sha256 IS NULL OR full_manifest_sha256 ~ '^[0-9a-f]{64}$'),
    copy_checkpoint_payload_sha256 char(64) NOT NULL CHECK (copy_checkpoint_payload_sha256 ~ '^[0-9a-f]{64}$'),
    copy_checkpoint_envelope_sha256 char(64) NOT NULL CHECK (copy_checkpoint_envelope_sha256 ~ '^[0-9a-f]{64}$'),
    snapshot_cutoff_utc timestamptz NOT NULL,
    snapshot_lsn pg_lsn NOT NULL,
    change_sequence bigint NOT NULL CHECK (change_sequence >= 0),
    latest_changed_at_utc timestamptz,
    audit_genesis_sha256 char(64) NOT NULL CHECK (audit_genesis_sha256 ~ '^[0-9a-f]{64}$'),
    audit_head_sequence bigint NOT NULL CHECK (audit_head_sequence >= 0),
    audit_head_event_sha256 char(64) NOT NULL CHECK (audit_head_event_sha256 ~ '^[0-9a-f]{64}$'),
    audit_proof_payload_sha256 char(64) NOT NULL CHECK (audit_proof_payload_sha256 ~ '^[0-9a-f]{64}$'),
    release_binding_sha256 char(64) NOT NULL CHECK (release_binding_sha256 ~ '^[0-9a-f]{64}$'),
    configuration_digest char(64) NOT NULL CHECK (configuration_digest ~ '^[0-9a-f]{64}$'),
    recovery_worker_build_digest char(64) NOT NULL CHECK (recovery_worker_build_digest ~ '^[0-9a-f]{64}$'),
    target_database_name text NOT NULL,
    target_endpoint_identity text NOT NULL,
    target_root_certificate_sha256 char(64) NOT NULL CHECK (target_root_certificate_sha256 ~ '^[0-9a-f]{64}$'),
    target_storage_bucket text NOT NULL,
    target_application_digest char(64) NOT NULL CHECK (target_application_digest ~ '^[0-9a-f]{64}$'),
    owner_mapping_approval_sha256 char(64) CHECK (owner_mapping_approval_sha256 IS NULL OR owner_mapping_approval_sha256 ~ '^[0-9a-f]{64}$'),
    owner_access_evidence_sha256 char(64) CHECK (owner_access_evidence_sha256 IS NULL OR owner_access_evidence_sha256 ~ '^[0-9a-f]{64}$'),
    state text NOT NULL CHECK (state IN ('restoring','materializing','validating','owner_access_pending','owner_access_verified','failed','expired')),
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    CHECK (candidate_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK (capture_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK (recovery_point_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CHECK (owner_id <> '' AND library_id <> ''),
    CHECK (source_region <> '' AND recovery_region <> ''),
    CHECK (target_database_name <> '' AND target_endpoint_identity <> '' AND target_storage_bucket <> ''),
    CHECK (latest_changed_at_utc IS NULL OR change_sequence > 0),
    CHECK (expires_at_utc > created_at_utc),
    CHECK (completed_at_utc IS NULL OR completed_at_utc >= created_at_utc),
    CHECK (state <> 'owner_access_verified' OR
           (owner_mapping_approval_sha256 IS NOT NULL AND owner_access_evidence_sha256 IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS recovery_candidate_scope_state
    ON recovery_candidate_sessions(owner_id,library_id,state,created_at_utc DESC);
CREATE INDEX IF NOT EXISTS recovery_candidate_point
    ON recovery_candidate_sessions(recovery_point_id,created_at_utc DESC);

CREATE TABLE IF NOT EXISTS recovery_candidate_materialization_receipts (
    candidate_id uuid NOT NULL REFERENCES recovery_candidate_sessions(candidate_id) ON DELETE RESTRICT,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    source_bucket text NOT NULL,
    source_key text NOT NULL,
    source_version_id text NOT NULL CHECK (source_version_id <> '' AND source_version_id <> 'null'),
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    destination_bucket text NOT NULL,
    destination_key text NOT NULL,
    destination_version_id text NOT NULL CHECK (destination_version_id <> '' AND destination_version_id <> 'null'),
    receipt_sha256 char(64) NOT NULL CHECK (receipt_sha256 ~ '^[0-9a-f]{64}$'),
    created_at_utc timestamptz NOT NULL,
    PRIMARY KEY (candidate_id,ordinal),
    UNIQUE (candidate_id,source_bucket,source_key,source_version_id),
    CHECK (source_bucket <> '' AND source_key <> '' AND destination_bucket <> ''),
    CHECK (destination_key = 'sha256/' || rtrim(sha256::text))
);

CREATE TABLE IF NOT EXISTS recovery_candidate_validation_revisions (
    candidate_id uuid NOT NULL REFERENCES recovery_candidate_sessions(candidate_id) ON DELETE RESTRICT,
    revision bigint NOT NULL CHECK (revision > 0),
    source_manifest_sha256 char(64) NOT NULL CHECK (source_manifest_sha256 ~ '^[0-9a-f]{64}$'),
    target_digest char(64) NOT NULL CHECK (target_digest ~ '^[0-9a-f]{64}$'),
    validation_digest char(64) NOT NULL CHECK (validation_digest ~ '^[0-9a-f]{64}$'),
    state text NOT NULL CHECK (state IN ('verified','failed')),
    findings jsonb NOT NULL CHECK (jsonb_typeof(findings) = 'array'),
    validated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (candidate_id,revision),
    UNIQUE (candidate_id,validation_digest),
    CHECK ((state = 'verified') = (findings = '[]'::jsonb))
);

CREATE OR REPLACE FUNCTION recovery_candidate_session_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'recovery_candidate_immutable'; END IF;
    IF (to_jsonb(NEW) - ARRAY['state','completed_at_utc','owner_mapping_approval_sha256','owner_access_evidence_sha256']) IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['state','completed_at_utc','owner_mapping_approval_sha256','owner_access_evidence_sha256']) THEN
        RAISE EXCEPTION 'recovery_candidate_identity_immutable';
    END IF;
    IF NEW.completed_at_utc IS DISTINCT FROM OLD.completed_at_utc AND
       (OLD.completed_at_utc IS NOT NULL OR NEW.completed_at_utc IS NULL OR
        NEW.state NOT IN ('owner_access_verified','failed','expired')) THEN
        RAISE EXCEPTION 'recovery_candidate_completion_immutable';
    END IF;
    IF NEW.owner_mapping_approval_sha256 IS DISTINCT FROM OLD.owner_mapping_approval_sha256 AND
       (OLD.owner_mapping_approval_sha256 IS NOT NULL OR NEW.owner_mapping_approval_sha256 IS NULL OR
        NEW.state NOT IN ('owner_access_pending','owner_access_verified')) THEN
        RAISE EXCEPTION 'recovery_candidate_owner_approval_immutable';
    END IF;
    IF NEW.owner_access_evidence_sha256 IS DISTINCT FROM OLD.owner_access_evidence_sha256 AND
       (OLD.owner_access_evidence_sha256 IS NOT NULL OR NEW.owner_access_evidence_sha256 IS NULL OR
        NEW.state <> 'owner_access_verified' OR NEW.owner_mapping_approval_sha256 IS NULL) THEN
        RAISE EXCEPTION 'recovery_candidate_owner_evidence_immutable';
    END IF;
    IF NEW.state <> OLD.state AND NOT (
        (OLD.state = 'restoring' AND NEW.state IN ('materializing','failed','expired')) OR
        (OLD.state = 'materializing' AND NEW.state IN ('validating','failed','expired')) OR
        (OLD.state = 'validating' AND NEW.state IN ('owner_access_pending','owner_access_verified','failed','expired')) OR
        (OLD.state = 'owner_access_pending' AND NEW.state IN ('owner_access_verified','failed','expired')) OR
        (OLD.state = 'owner_access_verified' AND NEW.state = 'expired') OR
        (OLD.state = NEW.state)
    ) THEN
        RAISE EXCEPTION 'recovery_candidate_state_transition_invalid';
    END IF;
    IF NEW.state = 'owner_access_verified' AND NEW.completed_at_utc IS NULL THEN
        RAISE EXCEPTION 'recovery_candidate_completion_timestamp_required';
    END IF;
    IF NEW.state <> OLD.state AND NEW.state NOT IN ('failed','expired') AND NEW.expires_at_utc <= now() THEN
        RAISE EXCEPTION 'recovery_candidate_expired';
    END IF;
    IF NEW.state IN ('owner_access_pending','owner_access_verified') AND NOT EXISTS (
        SELECT 1 FROM recovery_candidate_validation_revisions v
        WHERE v.candidate_id=NEW.candidate_id AND v.state='verified'
          AND v.source_manifest_sha256=NEW.source_manifest_sha256
    ) THEN
        RAISE EXCEPTION 'recovery_candidate_validation_required';
    END IF;
    RETURN NEW;
END; $$;

DROP TRIGGER IF EXISTS recovery_candidate_session_guard ON recovery_candidate_sessions;
CREATE TRIGGER recovery_candidate_session_guard BEFORE UPDATE OR DELETE ON recovery_candidate_sessions
    FOR EACH ROW EXECUTE FUNCTION recovery_candidate_session_guard();

CREATE OR REPLACE FUNCTION recovery_candidate_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'recovery_candidate_append_only';
END; $$;

DROP TRIGGER IF EXISTS recovery_candidate_materialization_append_only ON recovery_candidate_materialization_receipts;
CREATE TRIGGER recovery_candidate_materialization_append_only BEFORE UPDATE OR DELETE ON recovery_candidate_materialization_receipts
    FOR EACH ROW EXECUTE FUNCTION recovery_candidate_append_only();
DROP TRIGGER IF EXISTS recovery_candidate_validation_append_only ON recovery_candidate_validation_revisions;
CREATE TRIGGER recovery_candidate_validation_append_only BEFORE UPDATE OR DELETE ON recovery_candidate_validation_revisions
    FOR EACH ROW EXECUTE FUNCTION recovery_candidate_append_only();

CREATE OR REPLACE FUNCTION recovery_candidate_materialization_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE candidate_bucket text;
BEGIN
    SELECT target_storage_bucket INTO candidate_bucket
      FROM recovery_candidate_sessions
     WHERE candidate_id = NEW.candidate_id AND state IN ('materializing','validating');
    IF candidate_bucket IS NULL OR NEW.destination_bucket <> candidate_bucket OR
       NEW.destination_key <> 'sha256/' || rtrim(NEW.sha256::text) THEN
        RAISE EXCEPTION 'recovery_candidate_materialization_scope_invalid';
    END IF;
    RETURN NEW;
END; $$;

DROP TRIGGER IF EXISTS recovery_candidate_materialization_guard ON recovery_candidate_materialization_receipts;
CREATE TRIGGER recovery_candidate_materialization_guard BEFORE INSERT ON recovery_candidate_materialization_receipts
    FOR EACH ROW EXECUTE FUNCTION recovery_candidate_materialization_guard();
