-- Reversible owner-authorized freeze receipt. The API writes one row per proposal
-- in the same transaction as phase='executing'; post-freeze recovery snapshots
-- include the canonical acknowledgment and digest before a deletion plan can be made.
CREATE TABLE IF NOT EXISTS recovery_purge_freezes (
    operation_id uuid PRIMARY KEY REFERENCES recovery_purge_proposals(operation_id),
    event_id uuid NOT NULL UNIQUE,
    owner_id text NOT NULL,
    library_id text NOT NULL,
    asset_id uuid NOT NULL,
    edit_id uuid,
    category text NOT NULL CHECK (category IN ('original','xmp')),
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    expected_lifecycle_version bigint NOT NULL CHECK (expected_lifecycle_version > 0),
    lifecycle_generation bigint NOT NULL CHECK (lifecycle_generation > 0),
    proposal_digest char(64) NOT NULL CHECK (proposal_digest ~ '^[0-9a-f]{64}$'),
    freeze_ack_sha256 char(64) NOT NULL CHECK (freeze_ack_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_freeze_ack bytea NOT NULL CHECK (octet_length(canonical_freeze_ack) > 0),
    frozen_by text NOT NULL,
    auth_time_utc timestamptz NOT NULL,
    frozen_at_utc timestamptz NOT NULL,
    CHECK (category <> 'xmp' OR edit_id IS NOT NULL),
    CHECK (auth_time_utc <= frozen_at_utc AND frozen_at_utc - auth_time_utc <= interval '5 minutes')
);

CREATE INDEX IF NOT EXISTS recovery_purge_freeze_scope
    ON recovery_purge_freezes(owner_id,library_id,asset_id,edit_id,lifecycle_generation);
