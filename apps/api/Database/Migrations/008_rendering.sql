-- Rendering is derivative-only. None of these tables can change source content, edits, or history.
CREATE TABLE rendering_compatibility (
    matrix_version text PRIMARY KEY,
    renderer_release text NOT NULL,
    renderer_artifact_digest text NOT NULL CHECK (renderer_artifact_digest = 'unapproved' OR renderer_artifact_digest ~ '^sha256:[0-9a-f]{64}$'),
    profile text NOT NULL,
    corpus_evidence_id text,
    approved_at timestamptz,
    approved_by text,
    state text NOT NULL CHECK (state IN ('pending', 'approved', 'rejected')),
    CHECK ((state = 'approved') = (corpus_evidence_id IS NOT NULL AND approved_at IS NOT NULL AND approved_by IS NOT NULL))
);

CREATE TABLE render_requests (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    request_key char(64) NOT NULL CHECK (request_key ~ '^[0-9a-f]{64}$'),
    asset_id uuid NOT NULL REFERENCES assets(id),
    edit_id uuid NOT NULL REFERENCES edit_identities(id),
    selected_version text NOT NULL,
    rendition text NOT NULL CHECK (rendition IN ('thumbnail-512', 'preview-2048')),
    profile text NOT NULL,
    renderer_release text NOT NULL,
    renderer_artifact_digest text NOT NULL CHECK (renderer_artifact_digest = 'unapproved' OR renderer_artifact_digest ~ '^sha256:[0-9a-f]{64}$'),
    matrix_version text NOT NULL REFERENCES rendering_compatibility(matrix_version),
    state text NOT NULL CHECK (state IN ('pending', 'running', 'completed', 'unavailable', 'failed', 'retryable', 'cancelled')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(owner_id, request_key)
);

CREATE TABLE render_attempts (
    id uuid PRIMARY KEY,
    request_id uuid NOT NULL REFERENCES render_requests(id),
    attempt_number integer NOT NULL CHECK (attempt_number > 0),
    state text NOT NULL CHECK (state IN ('pending', 'running', 'completed', 'unavailable', 'failed', 'retryable', 'cancelled')),
    failure_code text,
    worker_policy_version text NOT NULL,
    queued_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz,
    completed_at timestamptz,
    CHECK (failure_code IS NULL OR failure_code IN ('source_unavailable', 'selected_version_unavailable', 'version_resolution_unavailable', 'incompatible_renderer', 'malformed_xmp', 'unsupported_source', 'resource_limit', 'renderer_timeout', 'output_invalid', 'storage_unavailable')),
    CHECK ((state IN ('failed', 'retryable', 'unavailable')) = (failure_code IS NOT NULL)),
    UNIQUE(request_id, attempt_number)
);

CREATE TABLE renditions (
    id uuid PRIMARY KEY,
    request_id uuid NOT NULL UNIQUE REFERENCES render_requests(id),
    object_key text NOT NULL UNIQUE,
    object_version text NOT NULL,
    output_sha256 char(64) NOT NULL CHECK (output_sha256 ~ '^[0-9a-f]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length > 0),
    media_type text NOT NULL CHECK (media_type IN ('image/jpeg', 'image/png')),
    completed_at timestamptz NOT NULL,
    retention_until timestamptz
);

CREATE TABLE render_provenance (
    rendition_id uuid PRIMARY KEY REFERENCES renditions(id),
    source_sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    xmp_sha256 char(64) NOT NULL REFERENCES content_objects(sha256),
    selected_version text NOT NULL,
    renderer_release text NOT NULL,
    renderer_artifact_digest text NOT NULL CHECK (renderer_artifact_digest = 'unapproved' OR renderer_artifact_digest ~ '^sha256:[0-9a-f]{64}$'),
    profile text NOT NULL,
    matrix_version text NOT NULL REFERENCES rendering_compatibility(matrix_version),
    completed_at timestamptz NOT NULL
);

CREATE TABLE render_events (
    id uuid PRIMARY KEY,
    owner_id text NOT NULL,
    request_id uuid NOT NULL REFERENCES render_requests(id),
    attempt_id uuid REFERENCES render_attempts(id),
    event_type text NOT NULL CHECK (event_type IN ('requested', 'cache_used', 'queued', 'completed', 'failed', 'retried', 'compatibility_rejected', 'intake_paused', 'cancelled')),
    reason_code text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX render_request_work ON render_requests(state, created_at);
CREATE INDEX render_attempt_recovery ON render_attempts(state, started_at);
CREATE INDEX render_event_owner ON render_events(owner_id, request_id, created_at);
