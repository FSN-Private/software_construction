ALTER TABLE render_requests ADD COLUMN source_sha256 char(64) REFERENCES content_objects(sha256);
ALTER TABLE render_requests ADD COLUMN xmp_sha256 char(64) REFERENCES content_objects(sha256);

INSERT INTO rendering_compatibility(matrix_version,renderer_release,renderer_artifact_digest,profile,corpus_evidence_id,approved_at,approved_by,state)
VALUES('rendering-v1','5.6.1','sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb','render-v1','darktable-5.6.1-ecr','2026-09-23T17:32:00Z','Fredrik Snårbacka','approved')
ON CONFLICT (matrix_version) DO UPDATE SET renderer_release=EXCLUDED.renderer_release,renderer_artifact_digest=EXCLUDED.renderer_artifact_digest,profile=EXCLUDED.profile,corpus_evidence_id=EXCLUDED.corpus_evidence_id,approved_at=EXCLUDED.approved_at,approved_by=EXCLUDED.approved_by,state=EXCLUDED.state;
