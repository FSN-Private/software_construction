-- Expand-only Feature 006 schema. Immutable commit evidence is authoritative;
-- working state, refs, visibility, reflogs, and projections are mutable views.
CREATE TABLE vc_working_changes (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    client_id text NOT NULL,
    branch_name text NOT NULL,
    asset_id uuid NOT NULL REFERENCES assets(id),
    edit_id uuid REFERENCES edit_identities(id),
    base_commit_id char(64),
    base_content_sha256 char(64),
    current_content_sha256 char(64),
    change_kind text NOT NULL CHECK (change_kind IN ('added','modified','deleted','conflicted')),
    display_path text,
    lifecycle_state text,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (base_commit_id IS NULL OR base_commit_id ~ '^[0-9a-f]{64}$'),
    CHECK (base_content_sha256 IS NULL OR base_content_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (current_content_sha256 IS NULL OR current_content_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (change_kind = 'deleted' OR current_content_sha256 IS NOT NULL),
    UNIQUE NULLS NOT DISTINCT (owner_id, library_id, client_id, branch_name, asset_id, edit_id)
);

CREATE TABLE vc_staging_sets (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    client_id text NOT NULL,
    branch_name text NOT NULL,
    expected_head char(64),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    status text NOT NULL CHECK (status IN ('open','published','abandoned')),
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (expected_head IS NULL OR expected_head ~ '^[0-9a-f]{64}$')
);

CREATE TABLE vc_staged_entries (
    staging_set_id uuid NOT NULL REFERENCES vc_staging_sets(id) ON DELETE CASCADE,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    working_change_id uuid NOT NULL REFERENCES vc_working_changes(id),
    working_version bigint NOT NULL CHECK (working_version > 0),
    PRIMARY KEY (staging_set_id, ordinal),
    UNIQUE (staging_set_id, working_change_id)
);

CREATE TABLE vc_commits (
    id char(64) PRIMARY KEY CHECK (id ~ '^[0-9a-f]{64}$'),
    owner_id text NOT NULL,
    library_id text NOT NULL,
    author_id text NOT NULL,
    authored_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    message text NOT NULL CHECK (length(message) BETWEEN 1 AND 4096),
    contract_version integer NOT NULL CHECK (contract_version > 0),
    policy_version integer NOT NULL CHECK (policy_version > 0),
    manifest_bytes bytea NOT NULL,
    integrity_state text NOT NULL DEFAULT 'verified' CHECK (integrity_state IN ('verified','quarantined')),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE vc_commit_parents (
    commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    ordinal integer NOT NULL CHECK (ordinal BETWEEN 0 AND 1),
    parent_id char(64) NOT NULL REFERENCES vc_commits(id),
    PRIMARY KEY (commit_id, ordinal),
    UNIQUE (commit_id, parent_id),
    CHECK (commit_id <> parent_id)
);

CREATE TABLE vc_commit_entries (
    commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    asset_id uuid NOT NULL REFERENCES assets(id),
    edit_id uuid REFERENCES edit_identities(id),
    change_kind text NOT NULL CHECK (change_kind IN ('added','modified','deleted','conflicted')),
    base_content_sha256 char(64),
    content_sha256 char(64),
    display_path text,
    lifecycle_state text,
    PRIMARY KEY (commit_id, ordinal),
    CHECK (base_content_sha256 IS NULL OR base_content_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (content_sha256 IS NULL OR content_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (change_kind = 'deleted' OR content_sha256 IS NOT NULL),
    UNIQUE NULLS NOT DISTINCT (commit_id, asset_id, edit_id)
);

CREATE TABLE vc_refs (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_name text NOT NULL,
    display_name text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('branch','tag','recovery')),
    head_commit_id char(64) REFERENCES vc_commits(id),
    protected boolean NOT NULL DEFAULT false,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, library_id, normalized_name),
    CHECK (head_commit_id IS NULL OR head_commit_id ~ '^[0-9a-f]{64}$'),
    CHECK (kind <> 'tag' OR head_commit_id IS NOT NULL)
);

CREATE TABLE vc_reflog (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_name text NOT NULL,
    old_head char(64),
    new_head char(64),
    actor_id text NOT NULL,
    operation text NOT NULL,
    reason text NOT NULL,
    occurred_at timestamptz NOT NULL,
    recovery_expires_at timestamptz,
    CHECK (old_head IS NULL OR old_head ~ '^[0-9a-f]{64}$'),
    CHECK (new_head IS NULL OR new_head ~ '^[0-9a-f]{64}$')
);

CREATE TABLE vc_visibility (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    hidden boolean NOT NULL,
    changed_at timestamptz NOT NULL,
    changed_by text NOT NULL,
    PRIMARY KEY (owner_id, library_id, commit_id)
);

CREATE TABLE vc_materialized_state (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_ref text NOT NULL,
    source_commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    asset_id uuid NOT NULL REFERENCES assets(id),
    edit_id uuid REFERENCES edit_identities(id),
    content_sha256 char(64),
    display_path text,
    lifecycle_state text,
    rebuilt_at timestamptz NOT NULL,
    CHECK (content_sha256 IS NULL OR content_sha256 ~ '^[0-9a-f]{64}$'),
    UNIQUE NULLS NOT DISTINCT (owner_id, library_id, normalized_ref, asset_id, edit_id)
);

CREATE TABLE vc_divergence (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_ref text NOT NULL,
    base_commit_id char(64),
    ours_commit_id char(64) NOT NULL,
    theirs_commit_id char(64) NOT NULL,
    resolved_commit_id char(64) REFERENCES vc_commits(id),
    state text NOT NULL CHECK (state IN ('unresolved','resolved')),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE vc_idempotency (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    idempotency_key text NOT NULL,
    fingerprint char(64) NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    commit_id char(64) REFERENCES vc_commits(id),
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, library_id, idempotency_key)
);

CREATE TABLE vc_ref_quarantine (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    normalized_ref text NOT NULL,
    reason_code text NOT NULL,
    detected_at timestamptz NOT NULL,
    PRIMARY KEY (owner_id, library_id, normalized_ref)
);

CREATE TABLE vc_checkouts (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    client_id text NOT NULL,
    normalized_ref text NOT NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (owner_id, library_id, client_id)
);

CREATE TABLE vc_stashes (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    client_id text NOT NULL,
    source_ref text NOT NULL,
    working_snapshot jsonb NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE vc_recovery_expirations (
    reflog_id uuid PRIMARY KEY REFERENCES vc_reflog(id),
    expired_at timestamptz NOT NULL
);

ALTER TABLE vc_working_changes ADD CONSTRAINT vc_working_base_commit FOREIGN KEY(base_commit_id) REFERENCES vc_commits(id);
ALTER TABLE vc_staging_sets ADD CONSTRAINT vc_staging_expected_head FOREIGN KEY(expected_head) REFERENCES vc_commits(id);
ALTER TABLE vc_divergence ADD CONSTRAINT vc_divergence_base FOREIGN KEY(base_commit_id) REFERENCES vc_commits(id);
ALTER TABLE vc_divergence ADD CONSTRAINT vc_divergence_ours FOREIGN KEY(ours_commit_id) REFERENCES vc_commits(id);
ALTER TABLE vc_divergence ADD CONSTRAINT vc_divergence_theirs FOREIGN KEY(theirs_commit_id) REFERENCES vc_commits(id);

CREATE INDEX vc_working_scope ON vc_working_changes(owner_id, library_id, client_id, branch_name);
CREATE INDEX vc_commit_library_time ON vc_commits(owner_id, library_id, received_at DESC, id);
CREATE INDEX vc_entry_asset ON vc_commit_entries(asset_id, edit_id, commit_id);
CREATE INDEX vc_parent_ancestry ON vc_commit_parents(parent_id, commit_id);
CREATE INDEX vc_reflog_recovery ON vc_reflog(owner_id, library_id, recovery_expires_at) WHERE recovery_expires_at IS NOT NULL;
CREATE INDEX vc_projection_ref ON vc_materialized_state(owner_id, library_id, normalized_ref, asset_id);
CREATE INDEX vc_divergence_open ON vc_divergence(owner_id, library_id, normalized_ref) WHERE state='unresolved';

CREATE FUNCTION vc_reject_immutable_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'immutable version-control evidence cannot be changed' USING ERRCODE='55000';
END $$;
CREATE TRIGGER vc_commits_immutable BEFORE UPDATE OR DELETE ON vc_commits FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
CREATE TRIGGER vc_commit_parents_immutable BEFORE UPDATE OR DELETE ON vc_commit_parents FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
CREATE TRIGGER vc_commit_entries_immutable BEFORE UPDATE OR DELETE ON vc_commit_entries FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
CREATE TRIGGER vc_reflog_immutable BEFORE UPDATE OR DELETE ON vc_reflog FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
CREATE TRIGGER vc_divergence_immutable BEFORE UPDATE OR DELETE ON vc_divergence FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
CREATE TRIGGER vc_recovery_expirations_immutable BEFORE UPDATE OR DELETE ON vc_recovery_expirations FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
