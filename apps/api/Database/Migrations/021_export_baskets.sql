-- Feature 012 is additive. Pins/tombstones never acquire source retention holds.
CREATE TABLE export_baskets (
 id uuid PRIMARY KEY, owner_id text NOT NULL, library_id text NOT NULL, name text NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
 is_default boolean NOT NULL DEFAULT false, revision bigint NOT NULL DEFAULT 1 CHECK(revision>0), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(id,owner_id,library_id)
);
CREATE UNIQUE INDEX export_default_basket ON export_baskets(owner_id,library_id) WHERE is_default;
CREATE TABLE export_basket_items (
 id uuid PRIMARY KEY, basket_id uuid NOT NULL REFERENCES export_baskets(id) ON DELETE CASCADE, pin jsonb NOT NULL,
 CHECK(jsonb_typeof(pin)='object')
);
CREATE TABLE export_requests (
 id uuid PRIMARY KEY, owner_id text NOT NULL, library_id text NOT NULL, basket_id uuid NOT NULL,
 idempotency_key text NOT NULL CHECK(length(idempotency_key) BETWEEN 1 AND 200), fingerprint char(64) NOT NULL,
 snapshot jsonb NOT NULL, state text NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','preparing','completed','partial','failed','retryable','cancelling','cancelled','expired','unavailable')),
 generation bigint NOT NULL DEFAULT 1, lease_id uuid, lease_until timestamptz, created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz, expires_at timestamptz, reason text,
 package_key text, package_version text, package_sha256 char(64), package_bytes bigint, manifest jsonb,
 UNIQUE(owner_id,library_id,idempotency_key)
);
CREATE INDEX export_pending_requests ON export_requests(state,lease_until);
CREATE TABLE export_results (
 request_id uuid NOT NULL REFERENCES export_requests(id), item_id uuid NOT NULL, outcome jsonb NOT NULL,
 render_request_id uuid, PRIMARY KEY(request_id,item_id)
);
CREATE TABLE export_purge_tombstones (
 event_id uuid PRIMARY KEY, owner_id text NOT NULL, library_id text NOT NULL, asset_id uuid NOT NULL, edit_id uuid,
 category text NOT NULL CHECK(category IN ('original','xmp')), content_sha256 char(64) NOT NULL CHECK(content_sha256 ~ '^[0-9a-f]{64}$'),
 generation bigint NOT NULL CHECK(generation>0), evidence_id uuid NOT NULL, received_at timestamptz NOT NULL DEFAULT now(), acknowledged_at timestamptz,
 CHECK(category='original' OR edit_id IS NOT NULL)
);
CREATE INDEX export_tombstone_scope ON export_purge_tombstones(owner_id,library_id,asset_id,content_sha256);
CREATE TABLE export_write_intents (
 id uuid PRIMARY KEY, request_id uuid NOT NULL REFERENCES export_requests(id), generation bigint NOT NULL,
 writer_id uuid NOT NULL, object_key text NOT NULL UNIQUE, state text NOT NULL CHECK(state IN ('admitted','settled','unknown','cleaned')),
 object_version text, multipart_id text, created_at timestamptz NOT NULL DEFAULT now(), settled_at timestamptz
);
CREATE TABLE export_download_sessions (
 id uuid PRIMARY KEY, owner_id text NOT NULL, session_hash char(64) NOT NULL, expires_at timestamptz NOT NULL, revoked_at timestamptz, UNIQUE(owner_id,session_hash)
);
CREATE TABLE export_download_grants (
 token_hash char(64) PRIMARY KEY, request_id uuid NOT NULL REFERENCES export_requests(id), session_id uuid NOT NULL REFERENCES export_download_sessions(id),
 kind text NOT NULL CHECK(kind IN ('bootstrap','cookie')), expires_at timestamptz NOT NULL, consumed_at timestamptz, revoked_at timestamptz
);
CREATE TABLE export_operations (singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton), paused boolean NOT NULL DEFAULT false, draining boolean NOT NULL DEFAULT false);
INSERT INTO export_operations(singleton) VALUES(true);
-- Immutable selection/snapshot evidence, independent of source lifecycle and mutable baskets.
CREATE FUNCTION export_snapshot_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.snapshot IS DISTINCT FROM OLD.snapshot OR NEW.owner_id<>OLD.owner_id OR NEW.library_id<>OLD.library_id OR NEW.fingerprint<>OLD.fingerprint OR NEW.idempotency_key<>OLD.idempotency_key OR NEW.basket_id<>OLD.basket_id THEN RAISE EXCEPTION 'immutable_export_snapshot'; END IF;
 RETURN NEW;
END; $$;
CREATE TRIGGER export_snapshot_immutable BEFORE UPDATE ON export_requests FOR EACH ROW EXECUTE FUNCTION export_snapshot_immutable();
