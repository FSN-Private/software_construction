# Implementation Plan: Darktable Rendering and Preview Provenance

**Branch**: `feature/008-darktable-rendering` | **Date**: 2026-09-21 | **Spec**: [spec.md](spec.md)

## Summary

Add a private rendering boundary that accepts only an immutable asset/XMP pair, derives a versioned rendition key, coalesces duplicate work, and atomically publishes a derivative with provenance. `darktable-cli` runs only in a resource-bounded, per-library worker workspace. The current edit resolves through feature 005; historical requests are fail-closed until feature 006 implements its selected-version contract.

## Technical Context

| Area | Decision |
|---|---|
| Language/runtime | C# / .NET 10 web API and worker |
| Storage | PostgreSQL request/attempt/provenance records; private encrypted S3 derivative objects |
| Rendering | `darktable-cli` 5.6.1, invoked with explicit input, XMP, output, dimensions, `--library :memory:`, and a versioned profile |
| Worker boundary | Dedicated Linux ECS/Fargate task role with derivative-only write access, no API/database credentials, a private writable workspace, and SQS/DLQ delivery |
| API | Owner-authorized `/v1/renditions` request/status/retry endpoints; versioned JSON contract in [contracts/rendering-v1.md](contracts/rendering-v1.md) |
| Testing | .NET model/unit tests; PostgreSQL/MinIO integration tests; native darktable corpus and performance tests on an approved Linux runner |
| Targets | cached p95 ≤500 ms; uncached start p95 ≤2 s and finish p95 ≤30 s; 99% supported thumbnails within five minutes |

## Research and design artifacts

- [Research decisions](research.md) establish the CLI invocation and pinned-artifact policy.
- [Data model](data-model.md) defines immutable keys, attempt transitions, and atomic publication.
- [API contract](contracts/rendering-v1.md) specifies safe owner-visible states and provenance.
- [Quickstart](quickstart.md) separates model validation from the trusted native-renderer gate.
- [ADR-0007](../../architecture/decisions/0007-isolated-darktable-rendering-provenance.md) records the enduring containment boundary.
- [ADR-0009](../../architecture/decisions/0009-production-renderer-vulnerability-gate.md) limits the accepted renderer digest to non-production until every accepted lower-severity finding is remediated.
- [ADR-0010](../../architecture/decisions/0010-renderer-iam-callback-authentication.md) authorizes the renderer task role, rather than a user or shared secret, to invoke dedicated SigV4-protected callback routes.

## Constitution Check

| Gate | Result | Evidence |
|---|---|---|
| Matching branch and feature directory | PASS | `feature/008-darktable-rendering` and `docs/features/008-darktable-rendering/` |
| Specification source of truth | PASS | `spec.md`, this plan, and `tasks.md` are the sole feature artifacts |
| Testable and secure design | PASS with release gates | Model tests run locally; native corpus, image digest, and compatibility approval block release use |
| Dependencies and supply chain | PASS for non-production with production gate | darktable artifact source, digest, license, SBOM, and vulnerability scan are required before a worker image is accepted; the accepted 48 medium and 18 low findings prohibit production promotion until remediated |
| Feature dependency | BLOCKED for US2 | Feature 006 has no implementation on this baseline; selected historical versions must return unavailable, never substitute current state |

## Delivery slices and milestones

1. **Render model and containment** — persistence schema, safe key/provenance model, source abstraction, resource policy, Docker/ECS manifests, and unit tests. This slice can be built without feature 006 or a local renderer but cannot claim a native render.
2. **Current-preview MVP** — current edit resolution through feature 005, queue/coalescing, worker execution boundary, owner-visible current rendition states, and integration tests. Native acceptance remains gated on a trusted Linux runner with an approved darktable artifact.
3. **Historical previews** — blocked until feature 006 delivers exact selected-version resolution. Its adapter must reject unavailable/mismatched versions without branch-head fallback.
4. **Failure, compatibility, and operations** — retry semantics, corpus, compatibility approval, metrics, drain/cancel/restart recovery, retention, and runbooks.

Each milestone has its own coherent commit after its listed validation passes. No deploy, image publication, AWS mutation, renderer-release approval, or push is authorized by this plan.

### Delivery CI contract

`Build release evidence` runs only from successful `master` CI and emits six retained release artifacts: API, ingest worker, renderer worker, web, infrastructure, and Windows API. `Deploy non-production` and production promotion require the complete artifact set, use the existing protected environments and OIDC roles, and deploy the API, ingest worker, and renderer task definitions as one rollback unit. The existing workflow names and job names remain stable; local evidence and infrastructure contract tests are required, while protected-environment deployment is remote evidence.

## Interfaces and behavior

1. The API canonicalizes the owner, library, source asset, selected edit/version, rendition, profile, and renderer compatibility version into a SHA-256 request key.
2. It returns an existing completed rendition only if every key/provenance field matches. It otherwise creates or joins one pending request.
3. The worker resolves an immutable source pair. A current request uses the active feature-005 edit; a historical request requires feature 006. Failure to resolve means `unavailable`, not a fallback.
4. The worker writes temporary input/output only below a unique workspace, validates the output byte stream, writes a private derivative, and commits the rendition and provenance in one database transaction.
5. Any failure records a category, retryability, attempt identity, and safe localized summary. Logs contain IDs and category only.

## Security, privacy, and operations

- The worker role can read exactly the authorized source object versions and write only its library-scoped derivative prefix. It cannot connect to PostgreSQL, mutate source objects, access credentials, or write version-control state.
- A request is tenant/library owner scoped before lookup, queueing, status, retry, or derivative access. Derivative URLs are private and short-lived.
- The worker limits CPU, memory, ephemeral storage, process time, concurrency, and queue visibility. On restart, active attempts become retryable or terminal within ten minutes.
- Intake pause/drain/cancel operates on pending work only. Completed derivative/provenance pairs are immutable and remain readable according to retention policy.
- The release plan must pin the darktable image by digest, retain SBOM/provenance and corpus evidence, define alert thresholds, and rehearse queue-drain rollback before deployment.
- Compatibility approval is environment-scoped: the 2026-09-23 approval permits the documented digest only for non-production. Production must use a replacement digest whose vulnerability evidence contains none of the 66 accepted medium/low findings; no waiver can bypass this gate.
- The API signs no renderer work on behalf of a user. The renderer consumes only immutable queue envelopes and invokes only the dedicated IAM-authorized callback routes with its ECS task role.

## Alternatives considered

| Alternative | Decision |
|---|---|
| Render in the API process | Rejected: native parsing/rendering would share production credentials and availability fate. |
| Use mutable source/XMP paths | Rejected: cannot prove the displayed rendition matches a selected revision. |
| Fall back to the current/branch-head XMP | Rejected: violates historical provenance and is misleading. |
| Publish a derivative before provenance | Rejected: violates atomic visibility and cache correctness. |
| Treat an unverified container tag as a renderer release | Rejected: release/profile changes require explicit approval and evidence. |

## Project Structure

```text
apps/api/Features/Rendering/        # owner API, keying, status, provenance
apps/api/Database/Migrations/       # rendering persistence
apps/render-worker/                 # queue consumer and constrained CLI executor
packages/contracts/                 # stable request/response records
packages/infrastructure/            # task, queue, IAM, metrics, retention
tests/unit/Platform.Api.Foundation.Tests/
tests/rendering/                    # integration, corpus, native and performance gates
docs/runbooks/                      # failures, compatibility, drain, retention
docs/features/008-darktable-rendering/
```

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
|---|---|---|
| Separate native worker | Isolates untrusted media and renderer from production data/control-plane credentials. | In-process execution violates FR-009 and FR-017. |
| Durable request and attempt records | Provides cache correctness, duplicate coalescing, retries, provenance, and restart recovery. | A fire-and-forget queue cannot satisfy observable attempt history. |
