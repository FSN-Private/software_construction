CREATE TABLE gallery_edit_idempotency (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    fingerprint char(64) NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id,library_id,idempotency_key)
);

CREATE TABLE gallery_preference_idempotency (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    hide_geolocation boolean NOT NULL,
    expected_version bigint NOT NULL,
    result_version bigint NOT NULL,
    PRIMARY KEY (owner_id,library_id,idempotency_key)
);
