-- A candidate is admitted only for a paused, named owner acceptance session. The
-- active compatibility row remains unchanged until the HTTP fixture succeeds, so
-- restoring the prior API task definition also restores a usable compatibility row.
ALTER TABLE render_intake_control ADD COLUMN acceptance_owner text;

CREATE TABLE rendering_compatibility_candidates (
    matrix_version text NOT NULL REFERENCES rendering_compatibility(matrix_version),
    renderer_artifact_digest text NOT NULL CHECK (renderer_artifact_digest ~ '^sha256:[0-9a-f]{64}$'),
    renderer_release text NOT NULL,
    profile text NOT NULL,
    corpus_evidence_id text NOT NULL,
    admitted_at timestamptz NOT NULL DEFAULT now(),
    admitted_by text NOT NULL,
    state text NOT NULL CHECK (state IN ('accepted', 'activated', 'rejected')),
    PRIMARY KEY(matrix_version, renderer_artifact_digest),
    CHECK ((state <> 'rejected') OR admitted_at IS NOT NULL)
);

ALTER TABLE render_events ALTER COLUMN request_id DROP NOT NULL;
ALTER TABLE render_events DROP CONSTRAINT render_events_event_type_check;
ALTER TABLE render_events ADD CONSTRAINT render_events_event_type_check CHECK (event_type IN ('requested', 'cache_used', 'queued', 'completed', 'failed', 'retried', 'compatibility_rejected', 'intake_paused', 'intake_resumed', 'cancelled', 'compatibility_admitted', 'compatibility_activated'));
