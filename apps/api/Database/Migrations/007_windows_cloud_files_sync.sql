CREATE TABLE windows_sync_devices (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    device_id text NOT NULL,
    normalized_ref text NOT NULL,
    cursor text,
    version bigint NOT NULL DEFAULT 1 CHECK(version > 0),
    revoked boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(owner_id, library_id, device_id)
);

CREATE TABLE windows_sync_content_ids (
    content_sha256 char(64) PRIMARY KEY REFERENCES content_objects(sha256),
    content_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
);

CREATE TABLE windows_sync_device_content (
    owner_id text NOT NULL,
    library_id text NOT NULL,
    device_id text NOT NULL,
    content_id uuid NOT NULL REFERENCES windows_sync_content_ids(content_id),
    PRIMARY KEY(owner_id, library_id, device_id, content_id),
    FOREIGN KEY(owner_id, library_id, device_id) REFERENCES windows_sync_devices(owner_id, library_id, device_id) ON DELETE CASCADE
);

CREATE INDEX windows_sync_namespace ON vc_materialized_state(owner_id, library_id, normalized_ref, asset_id, edit_id);
