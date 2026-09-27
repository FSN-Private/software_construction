-- Immutable origin evidence for server-generated XMP. Publication still validates the
-- authoritative object bytes and the asset/edit identity in the same transaction.
CREATE TABLE lineage_merge_content (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    asset_id uuid NOT NULL,
    edit_id uuid NOT NULL REFERENCES edit_identities(id),
    sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    base_sha256 char(64),
    target_sha256 char(64),
    source_sha256 char(64),
    target_commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    source_commit_id char(64) NOT NULL REFERENCES vc_commits(id),
    policy_version text NOT NULL CHECK (policy_version = 'xmp-merge-v1'),
    operation_id text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id,library_id,asset_id,edit_id,sha256,target_commit_id,source_commit_id),
    FOREIGN KEY (owner_id,asset_id) REFERENCES assets(owner_id,id),
    CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (base_sha256 IS NULL OR base_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (target_sha256 IS NULL OR target_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (source_sha256 IS NULL OR source_sha256 ~ '^[0-9a-f]{64}$')
);
CREATE INDEX lineage_merge_content_digest ON lineage_merge_content(owner_id,asset_id,edit_id,sha256);
CREATE TRIGGER lineage_merge_content_immutable BEFORE UPDATE OR DELETE ON lineage_merge_content
    FOR EACH ROW EXECUTE FUNCTION vc_reject_immutable_mutation();
