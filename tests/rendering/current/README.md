# Current-preview integration gate

`RenderingIntegrationChecks` runs against the PostgreSQL and MinIO integration stack. It proves a current active asset/XMP pair creates one pending attempt, coalesces a duplicate request, publishes a private JPEG, atomically records exact provenance, reuses the completed cache entry, and leaves source/XMP identities unchanged.

Native current-XMP rendering is additionally covered by the trusted-runner corpus's `current.xmp` state. The ECR-digest run and its renderer identity are retained in [`../corpus/ecr-evidence.md`](../corpus/ecr-evidence.md).
