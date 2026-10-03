# Independent adversarial review: feature 012 plan and tasks

**Date**: 2026-10-03 (Asia/Bangkok)
**Reviewer**: gpt-6-astra, separate read-only agent `/root/plan_review`
**Branch**: feature/012-versioned-export-basket
**Base/HEAD**: 2680c6abed7ac463b13dfc36cc8327d2debfa04c (fetched origin/master)
**Remote**: origin, https://github.com/FSN-Private/software_construction.git
**Review scope**: proposed working-tree planning artifacts; no implementation commit range or pending push. No push, merge, deployment or implementation authorized by this review request.
**Result**: PASS for planning readiness after round 3. Rounds 1 and 2 are preserved below. This is not implementation acceptance or pre-push certification; T029 still gates full completion/production enablement on feature 011 evidence.

## Reviewed artifact identities

The design documents are new, untracked files, so the tracked Git diff alone does not represent the scope. The reviewer read spec.md, plan.md, tasks.md, research.md, data-model.md, quickstart.md and contracts/export-api.md in full, together with AGENTS.md, the constitution, ADR-0004 and relevant current source/infrastructure/contracts.

SHA-256 of the principal files at review:

- plan.md: `ac654010c9bc5a258ef14c9dbe95238af5b7d1748cb69950cedf8934d07686ba`
- tasks.md: `b51dc337f95e4d5ab18b4a0f4e8c5bac41b8210d968811f4cef2091f7411f209`
- spec.md: `6607949d6647edb3cccc30497f650a64b6798afe07f351e494dd447895426134`

Requirements are approved; proposed plan/tasks remain unapproved. Feature 011 is unmerged; preceding implementations are present through merged squash/rebase history.

## Prompt scope and available validation

The reviewer was instructed to assess planning correctness, feasibility and completeness against every requirement/acceptance/NFR/task, current source integrations, security/privacy, retention/concurrency integrity, compatibility, operations, validation executability and rollback. It was instructed to identify significant evidenced blockers, distinguish them from non-blocking notes, provide file/line evidence, IDs, impact and remediation, and explicitly state areas with no additional blockers. It was not asked to require implemented code at the planning stage or given a desired conclusion. No edits, commits, pushes or delegation were permitted.

Validation before review:

- Spec Kit check-prerequisites.ps1 with SPECIFY_FEATURE_DIRECTORY set to this feature and -Json -RequireSpec -RequireTasks -IncludeTasks: PASS.
- git diff --check: PASS.
- Static checklist/ID analysis: 26 unique tasks; all 64 FR/AC/NFR/SC identifiers have references. This does not establish semantic feasibility.
- Runtime acceptance: not run; no implementation exists for this feature yet.

## Round 1 findings

### 012-PLAN-001 — Blocking / High: package downloads exceed deployed transport limits

**Reviewer evidence**: plan.md:51 permits 10 GiB packages. plan.md:55 and contracts/export-api.md:17 prescribe authenticated API streaming with ranges. T013 (tasks.md:40) implements the route and T020 (tasks.md:57) provides browser controls. Current packages/infrastructure/src/platform-foundation-stack.ts:255, :260 and :262 use API Gateway HTTP API with service discovery integration. [AWS HTTP API quotas](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-quotas.html) specify a 10 MB payload limit and 30-second maximum integration timeout.

**Impact**: ordinary downloads larger than 10 MB fail through this gateway. Optional range support does not require client/server chunk bounds. A browser chunk implementation also needs a supported disk-writing/assembly strategy for GiB-scale files, grant renewal and resumed-transfer verification without whole-package memory buffering.

**Mapped requirements/tasks**: FR-016, FR-020, FR-023; AC-015; NFR-003, NFR-005, NFR-008, NFR-015; T013, T017, T020, T023, T024.

**Suggested remediation**: choose a transport meeting package size and revocation requirements. Define either bounded authenticated chunks with server enforcement, supported browser disk saving, grant renewal and resume semantics, or an infrastructure path supporting large authenticated streams. Specify CORS request headers and response-header exposure. Validate a >10 MB package through the actual gateway path, interrupted/resumed download, expiry and revocation; localhost streaming is insufficient deployment evidence.

**Independent assessment**: valid, unresolved in this review-only request. The implementing agent read the current HttpApi and HttpServiceDiscoveryIntegration configuration and independently checked the official AWS quota documentation. The proposed plan contains no bounded assembly protocol or alternate large-response route. This is a concrete transport/design gap, not a request to implement the feature prematurely. No artifact correction made; remediation must precede plan approval.

### 012-PLAN-002 — Blocking / High: purge integration boundary does not exist

**Reviewer evidence**: plan.md:37 claims existing feature 005/006 purge/retention contracts while noting 011 is unmerged. T015 (tasks.md:47) names integration at existing lifecycle boundaries in packages/storage/AssetService.cs. That service's ChangeAsync at line 74 accepts only move/delete/restore; deletion produces recoverable-deleted. Searches of current application/storage/contracts find no permanent-purge implementation. Feature 005 spec.md:184 explicitly defers physical purge to 011 and requires retention/recovery-copy checks. Its plan.md:8 and :54 exclude permanent purge.

**Impact**: no defined authoritative event/tombstone identifies permanent purge of original/XMP identity, fences it against export publication/download, or assigns derivative-removal ownership. An absent object can represent integrity drift/outage, not authorized permanent purge. Synthetic row edits test a consumer but cannot prove completed purge integration/compliance.

**Mapped requirements/tasks**: FR-009, FR-025; AC-007, AC-013; NFR-011, NFR-016; T008, T015, T017, T023, T024.

**Suggested remediation**: explicitly decide dependency readiness before approval. Require feature 011's contract/integration as a completion prerequisite, or approve a standalone durable invalidation/tombstone contract for 012 without transferring backup/purge-policy ownership. Specify identity scope, authority, transaction/fencing boundary, derivative cleanup ownership and completion evidence. Distinguish consumer-contract testing from later end-to-end purge evidence, preserving any required 011 completion/rollout gate. Do not silently import/implement the backup subsystem.

**Independent assessment/disposition**: needs user decision. The finding is supported: the implementing agent independently checked AssetService action restrictions, current schema states, source searches and feature 005's explicit deferred-purge boundary. The plan overstates the existing dependency. Choosing a completion prerequisite versus a separately approved cross-feature invalidation contract affects scope/readiness; either must be documented before approval. No source or planning correction made in this review-only request.

### 012-PLAN-003 — Non-blocking / Medium: clarify renderer compatibility mapping

**Reviewer evidence**: plan.md:41–43 introduces private/retained metadata presets and T010 (tasks.md:37) extends contracts/runtime. Existing rendering_compatibility has matrix_version as its sole primary key (apps/api/Database/Migrations/008_rendering.sql:3), storing one profile per matrix. RenderingService.CompatibilityAsync selects configured matrix and requested profile (RenderingService.cs:127–137). Promotion works through that matrix (RenderOperationsService.cs:44–58).

**Impact**: the implementer must distinguish separate profile/matrix admission from a versioned metadata transform layered on an approved profile, including cache identity/provenance/promotion/rollback. Existing redispatch rereads current active source state (RenderingService.cs:168–170), so retained/deleted pins must stay immutable during redispatch too.

**Mapped requirements/tasks**: FR-013, FR-026; AC-014; NFR-008, NFR-013; T010, T024.

**Suggested clarification**: document the preset-to-renderer mapping and required migration/promotion changes in T010; explicitly cover retained/deleted pinned redispatch. The plan already requires exact pinned inputs and preview compatibility, so this is not an additional blocker.

**Independent assessment/disposition**: non-blocking note. Current compatibility selection/promotion is matrix-scoped and the mapping remains unspecified. The pinned-input requirement already covers the behavioral obligation; clarification can be handled during design remediation without elevating it into another gate.

## Reviewer areas with no additional blocking findings

- Traceability: all stories and major acceptance/NFR areas have implementation and verification tasks; ID coverage does not resolve the two semantic blockers.
- Identity/snapshot/concurrency: exact pins, immutable snapshots, fingerprints, lease generations and publication fencing are appropriate at planning depth.
- Authorization/privacy: owner scope, operator restrictions, audit exclusions and metadata consent have planned controls/tests; future implementation is not certified.
- Cancellation/expiry/recovery: revocation before cleanup, bounded lease recovery and shared-render protection are covered; purge remains subject to 002.
- Accessibility/localization/validation: browser/manual accessibility, locale tests, real-render evidence and fault injection are planned. Lack of runtime results is appropriate before implementation.
- Migration/rollout/rollback: additive schema, capability gating, preserved evidence and cleanup are planned; transport/purge blockers prevent full readiness.
- Dependencies/scope/process: no new dependency, correct artifact location, approval gate preserved; no implementation or push claimed.

## Final reviewer statement

“Final result: BLOCKED pending disposition of 012-PLAN-001 and 012-PLAN-002.”

No fixes were requested in this review-only turn. Plan/tasks/source are unchanged; all tasks remain unchecked. No commits or remote writes were made. The next design revision must address both blockers and receive a follow-up independent review; this record preserves round 1 and does not claim PASS.

## Record verification

The same independent reviewer inspected this saved record and confirmed: “the record faithfully captures all three findings, their evidence and classifications, the independent assessments, and the BLOCKED plan-readiness result. No factual transcription errors found.” It explicitly confirmed that 001 and 002 remain unresolved and that record verification is not plan approval or pre-push certification.

## Authorized remediation and round 2

The user subsequently requested “please fix.” Planning corrections were authorized; runtime implementation, feature 011 merge/purge, infrastructure deployment and push were not. All original task IDs were preserved. Revisions add proposed ADR-0014, contracts/purge-invalidation.md and T027-T029, synchronizing plan/data-model/research/API/quickstart.

- **001 — valid and design-fixed**: dedicated HTTPS CloudFront/internal-ALB path to existing API tasks, no HTTP API Gateway media response; native one-use bootstrap POST installs request-scoped owner-session cookie for browser disk downloads, bounded buffers/ranges/checksums, admission revocation/session expiry, deployed multi-GiB/browser/CORS evidence and budget/security/rollback tasks. No new runtime service/dependency; changed infrastructure remains proposed.
- **002 — valid and planning-fixed**: explicitly distinguish 005 soft deletion from 011 permanent purge; durable scoped tombstone/outbox consumer and shared lifecycle lock/cleanup acknowledgment, no public purge publisher, labeled synthetic consumer fixtures. T029 remains BLOCKED on actual authoritative 011 publisher/integration evidence; full completion/convergence and production enablement cannot be claimed until that gate passes.
- **003 — clarified/non-blocking note addressed**: existing renderer profile/matrix retained; separately versioned export metadata transform with input/output hashes, treatment-aware cache/provenance and pinned redispatch. Changed worker artifacts use existing admission/promotion.

The reviewer independently reread the revised artifacts and explicitly accepted all three dispositions at planning depth. It confirmed that actual browser/transport/runtime evidence remains required and T029 is a separate feature-completion dependency. Its round-2 result was BLOCKED on the following new issue.

### 012-PLAN-004 — Blocking / High: cleanup acknowledgment can precede a late object write

**Reviewer evidence**: the revised purge-invalidation.md:9–11 and T028 define transaction fencing and cleanup of recorded exact versions but do not wait for storage writes admitted before tombstone whose object version is not yet recorded. Existing RenderExecution.cs:149–154 publishes the object before the completion callback; RenderRuntime.cs:57–67 learns the version only after PutObjectAsync completes.

**Impact**: upload starts, tombstone fences publication, cleanup deletes known versions and acknowledges, then the upload completes and creates an untracked media version despite correct database callback rejection. Callback loss/worker death can leave media after purge completion.

**Mapped IDs**: FR-009, FR-025; AC-013; NFR-004, NFR-016; T012, T014, T015, T028, T029.

**Suggested remediation**: durable pre-upload intents/attempt destinations, close admission on invalidation, settle or safely fence all admitted writes, reconcile unknown/crashed outcomes and remove resulting versions/multipart uploads before acknowledging. Preserve shared-renderer ownership. Fault-test a paused upload across tombstone/cleanup followed by late success, failed callback and worker loss.

**Independent assessment**: valid and design-fixed for round 3. The object-before-callback ordering is real, and DB fencing alone cannot prove derivative removal. The plan now requires durable intents before every export-owned storage write, intent-owned unique destinations including unknown multipart-initiation outcomes, closed admission, verifiable storage-writer settlement and post-settlement version/multipart reconciliation. Lease expiry/timeouts are explicitly insufficient; unknown outcomes remain draining and feature 011 cannot complete deletion without acknowledgment. T030 adds the implementation/validation obligations and exact late-write/lost-callback/worker-death fault tests. Plan, data model, quickstart and ADR-0014 mirror the contract. This records a design fix, not runtime validation.

Round 2 reported no additional blocking issue in transport feasibility, owner/session authorization, metadata privacy, identity/idempotency, renderer mapping, traceability, accessibility/localization or rollout/rollback. No runtime acceptance or pre-push certification was issued.

## Round 3 scope and validation (remediation through 2026-10-04)

The same alternate reviewer is assessing the complete revised artifacts and every prior disposition. Principal revised SHA-256:

- plan.md: `6b0c6662da61540f2a83214aa2eca0cf44c233557a10cf571fabef9f53f74a66`
- tasks.md (final corrected checklist): `e4a0d1a02ac5b2cb7906e75b1ea4da54a17411e01e247e8f803ce0a70fbd9663`

Validation: Spec Kit prerequisites pass; original T001-T026 preserved, T027-T030 appended with explicit dependency ordering; all 64 specification IDs still referenced; relative planning-document links resolve; git diff --check passes. No runtime tests apply to these documentation-only fixes; their required future runtime evidence remains in tasks. All 30 tasks remain unchecked; T029 explicitly blocked on feature 011 evidence. Design/ADR approval remains pending; no code, commits, push, deployment or feature 011 changes made.

## Round 3 final agreement

The reviewer identified a mechanical issue during this round: T030 was concatenated onto the T029 line and the blocker marker had an encoding artifact. These were corrected, the line-based validator passed for all 30 unique checklist entries, and the reviewer independently verified the corrected task hash above. No task ID was renumbered or removed.

The reviewer inspected the pending record and confirmed it faithfully preserves previous findings/dispositions, then explicitly reported:

> Round 3 plan-readiness: PASS. No valid unresolved planning blocker remains.

It accepted 001's dedicated transport design, 002's explicit authoritative-purge dependency, 003's compatibility clarification and 004's pre-write-intent/quiescence/reconciliation design with T030 race tests. It found no new blocker in transport, authorization/privacy, concurrency/lifecycle integrity, compatibility, traceability, validation design or rollout/rollback.

Final gate: **PASS — planning readiness only**. Maintainer design/ADR approval remains pending. **T029 remains BLOCKED for full feature completion and production enablement** until actual feature 011 publisher/integration evidence exists. Dedicated-transport/native-browser and lifecycle acceptance tests are future implementation obligations, not passed runtime checks. No implementation acceptance, deployment/purge/merge/push authorization or pre-push certification is granted.
