# Adversarial Pre-Push Review

**Status**: BLOCKED on external acceptance evidence only
**Reviewer model**: gpt-5.6-terra (independent alternate model)
**Review date**: 2026-09-24
**Branch and reviewed head**: `feature/008-darktable-rendering` at `25bd8d376f6732687b6d9c407620dac0d8413152`

## Scope

The review covered the unpushed Feature 008 changes, including the renderer runtime,
render-request provenance and cache identity, compatibility enforcement, operation
controls, migration embedding, delivery wiring, and acceptance evidence.

## Findings and dispositions

| Round | Finding | Severity | Disposition |
|---|---|---|---|
| 1 | The renderer opened its output for write-only access before hashing it. | Blocking | Fixed: the file is read/write, flushed, rewound, then hashed. |
| 1 | Cache identity and callback validation omitted source/XMP identity. | Blocking | Fixed: source and XMP SHA-256 values are persisted, participate in the key, and are verified by callbacks and redispatch. |
| 1 | Operations migrations were not all embedded in the migrator. | Blocking | Fixed: migrations 009 through 011 are embedded and migration tests cover them. |
| 1 | A dispatch failure after request creation could leave a pending request stranded. | Blocking | Fixed: pending requests are periodically redispatched; queue duplication is guarded by the existing claim protocol. |
| 1 | The default resource-policy duration exceeded the recovery interval. | Blocking | Fixed: default timeout is 540 seconds. |
| 1 | The compatibility matrix did not provide a fail-closed approved artifact path. | Blocking | Fixed in subsequent rounds: intake requires an exact deployed digest and exact approved row. |
| 1 | Rendering lifecycle/audit events were incomplete. | Blocking | Fixed: rejection, cache use, pause, and cancellation events are recorded. |
| 2 | The approved candidate artifact was not the source-built release artifact. | Blocking | Fixed: deployment injects the actual renderer image digest and compatibility lookup requires exact equality. |
| 3 | Compatibility could fall back when the deployed digest was absent. | Blocking | Fixed at reviewed head: missing or invalid `Rendering:DeployedArtifactDigest` rejects intake. |
| 4 | Pending redispatch can increase queue traffic while an item remains pending. | Non-blocking | Recorded as an operational cost/queue consideration; no correctness or fail-open defect found. |

## Independent conclusion

No unresolved implementation blocker remains at the reviewed head. Foundation validation
was rerun successfully; the PostgreSQL/MinIO integration fixture remains intentionally
skipped unless `INGEST_INTEGRATION=1` is configured.

The push gate is not yet passable because two external acceptance tasks remain:

1. Publish the current source-built renderer, scan and run the corpus against its exact
   ECR digest, and obtain compatibility approval for that digest.
2. Deploy that approved artifact to non-production and capture live HTTP evidence for
   NFR-001 cache-hit p95 and NFR-002 uncached request-start p95 (T025/T026).

NFR-017/SC-006 remains waived by the feature owner as documented; that waiver does not
cover the two requirements above.

## Fresh review — 2026-09-24

**Reviewer model**: gpt-5.6-terra (independent alternate model)  
**Reviewed range**: `master..cc04bf5`  
**Result**: BLOCKED

1. **Blocking, high — T003/T018/T023/T028:** migration 012 approves
   `sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b`,
   but no committed digest-qualified SBOM/SARIF/corpus and compatibility record resolves
   `darktable-5.6.1-hardened-ecr`; retained records still name the older digest.
2. **Blocking, medium — T005/T028:** migration rehearsal stopped at 011 and therefore
   did not test the exact digest update in 012. This is fixed locally by extending
   `MigrationChecks` to apply 012 and assert the approved exact digest; validation and
   a new review are still required.

The required SBOM tooling is unavailable in this environment (`trivy` is not installed
and Docker has no `sbom` plugin), so the first finding remains unresolved and no push
may occur.
