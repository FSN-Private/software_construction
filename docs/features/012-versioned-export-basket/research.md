# Research: Versioned Export Basket

Proposed decisions, 2026-10-03.

| Decision | Evidence/rationale | Alternative |
|---|---|---|
| Fresh branch from fetched master | Old 012 branch is 198 commits behind with only spec/reviews. Merged source contains preceding features including 015. | Rebase stale planning history: unnecessary rewrite. |
| Pinned render path | RenderingService.RequestAsync reads current asset original; VersionControlSelectedVersionResolver resolves committed XMP. Both identities must be pinned for exports. | Unchanged public current rendition request violates exactness. |
| Committed-only working selection | FR-005 explicitly permits this; avoids extra durable snapshots. | Working snapshots add unneeded state. |
| Mandatory manifest/external package hash | FR-017 requires all package manifests. ZIP hash cannot be embedded recursively. | Optional manifest violates FR-017. |
| Originals and 2048 JPEG catalog | Existing RenditionKind supports Preview2048; RenderRuntime invokes darktable-cli JPEG with width/height bounds. | New full-resolution/TIFF presets expand compatibility scope. |
| Separate versioned export transforms | Keep approved renderer profile/matrix; transform verified output and record input/output hashes plus transform version in manifest/cache identity. Redispatch remains pinned. | Competing profiles violate current single-profile matrix shape. |
| Database hosted worker and standard ZIP | Existing recovery/refresh hosted workers and installed platform libraries cover durable coordination/packaging. | New service/dependency unnecessary. |
| Dedicated authenticated native download origin | CloudFront/private ALB bypasses HTTP API Gateway 10 MB/30-second limits. One-use form exchange installs owner-session cookie for native Chrome/Edge/Firefox saving without JS whole-file buffers. | Presigned URLs lack request-time revocation; browser chunk assemblers add compatibility complexity. |
| Explicit purge dependency and consumer contract | 005 has only soft-delete/restore; implement scoped durable tombstone consumer and export cleanup acknowledgment. T029 requires authoritative 011 integration before full completion/rollout. | Assuming an existing purge producer or accepting fixture-only evidence is incorrect; importing 011 exceeds scope. |

No dependency added. Direct local investigation respects repository restriction reserving delegation for read-only alternate-model review. Approval of design decisions is pending.
