UPDATE rendering_compatibility
SET renderer_release='5.6.1',
    renderer_artifact_digest='sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b',
    profile='render-v1',
    corpus_evidence_id='darktable-5.6.1-hardened-ecr',
    approved_at=NULL,
    approved_by=NULL,
    state='pending'
WHERE matrix_version='rendering-v1';
