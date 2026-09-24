# Adversarial Pre-Push Review

**Status**: PASS — historical blocked rounds and post-merge T026 acceptance remain recorded below.
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

## Remediation review convergence — 2026-09-25

**Alternate reviewer model**: `gpt-6-sol`  
**Remote / destination**: `origin` / `master`  
**Base and final reviewed range**: `cd1fe828c7d56f89f5acc8503aaff6372daf8f9c..389ed07a15327fa61898bfccac5591e185c94598`  
**Pending push scope**: `origin/master..HEAD` at the final clean-tree/push check: the reviewed implementation through `389ed07`, this passing review record (`672c7ac`), and this record-only audit correction. No implementation changes occur after `389ed07`.

The reviewer examined the full Feature 008 specification/tasks, migration 013,
renderer/API digest binding, candidate admission and activation, ECS cutover/rollback,
native corpus, live HTTP fixture, and validation evidence. The following blocking
findings were independently assessed as valid and fixed.

| Round | Finding / affected work | Disposition and evidence |
|---|---|---|
| 1 | Any owner could promote; activation preceded HTTP acceptance; rollback could leave the prior API incompatible (T024–T026, T028). | Valid and fixed in `1b7f86c`: configured Cognito operator admission, owner-scoped paused candidate session, activation after HTTP evidence, and abort before rollback. |
| 1 | Corpus checked JPEG metadata/distinctness, not approved decoded pixels or all renditions (FR-015, AC-008/009). | Valid and fixed in `1b7f86c` and `f626b38`: four effective XMP states compare PFM pixels at 512px and 2048px against committed exact-digest references. |
| 2 | Post-activation smoke/evidence failures could trigger ECS rollback after the compatibility row changed (NFR-013/014). | Valid and fixed in `4675943`: all fallible evidence steps precede activation; activation is the terminal release action. |
| 2 | The documented rendition value did not bind to the C# enum (T026). | Valid and fixed in `f626b38`: endpoint accepts documented `thumbnail-512` / `preview-2048` strings and returns textual state values. |
| 2 | Old-digest work could cross renderer cutover; a same-owner asset could be requested under another library (FR-005/015, NFR-005). | Valid and fixed in `f626b38`: pre-cutover drain, worker exact-digest guard, and source resolution bound to materialized/working library state; integration exercises same-owner cross-library refusal. |
| 3 | HTTP p95 acceptance was not a real uncached p95; sequential polling could exceed a per-request 30-second deadline (T025/T026, NFR-002). | Valid and fixed in `381fd39` and `de157b7`: at least five distinct uncached requests, per-submission 30-second deadlines, concurrent polling, nearest-rank p95 start, and 20 concurrent cache samples. |
| 3 | Activation, drain, retry, and stale queue delivery had ambiguous-response or paused-intake races (T024/T029, NFR-013/014). | Valid and fixed in `381fd39` and `389ed07`: exact activation is idempotent, retries enforce paused/exact-compatibility gates, mismatched worker envelopes claim before safe failure/delete, bootstrap skips unavailable old routes when prior renderer count is zero, and drain recovery is armed before the request. |
| 3 | Migration 013 had not run against PostgreSQL. | Valid and fixed/verified: `node tests/integration/ingest/run.mjs` passed on local PostgreSQL/MinIO, including migration rehearsal and candidate admission/activation assertions. |

### Validation rerun

- `dotnet build --no-restore apps/api/Platform.Api.csproj` — PASS
- `dotnet build --no-restore apps/render-worker/Platform.Render.Worker.csproj` — PASS
- `dotnet run --no-restore --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` — PASS
- `node tests/integration/ingest/run.mjs` — PASS (PostgreSQL/MinIO, including migration 013)
- `node tests/infrastructure/release/release-workflows.mjs` — PASS
- `bash -n scripts/deploy-ecs-release.sh`, `bash -n tests/rendering/corpus/run-native.sh`, and `node --check scripts/smoke-rendering.mjs` — PASS

### Independent conclusion

The reviewer explicitly reported: **“PASS … All prior blocking findings are resolved; I found no valid unresolved blocking issue.”** Non-production live evidence remains intentionally post-merge T026 work and is fail-closed on the protected fixture configuration. Production remains categorically blocked by T021.

**Final gate result: PASS.**
