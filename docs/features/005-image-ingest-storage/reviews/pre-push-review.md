# Adversarial Specification Review

**Status**: Round 1 findings fixed; awaiting independent Round 2
**Reviewer model**: gpt-5.5
**Review date**: 2026-09-03
**Base**: `feature/003-darktable-version-control`
**Scope**: Feature 005 specification; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| Accepted unassociated XMP could be consumed as active data. | Medium | Parent FR-002; 005 FR-005, AC-009 | **Valid and fixed.** It is quarantine-only and non-authoritative until one deterministic association exists. |
| Recovery manifests omitted key lifecycle and provenance evidence. | Medium | Parent recovery requirements; 005 FR-016, AC-010 | **Valid and fixed.** Manifest and recovery validation now include path, holds, audit, provenance, and handoff identifiers. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

## Final agreement

The alternate reviewer confirmed quarantine-only unassociated XMP and complete recovery-manifest traceability. No valid unresolved finding remains.

**Final gate result: PASS**

## Pre-push review — 2026-09-07

**Status**: blocked | **Reviewer model**: gpt-5.6-terra | **Base**: `93ff52a` | **Reviewed range**: `93ff52a..16c7f46` | **Pending push scope**: `origin/feature/005-image-ingest-storage..HEAD`

### Round 1

The independent reviewer found no unrelated changes, secrets, deployment mutation, fixture checksum mismatch, or defect in the basic owner/path primitives. The reviewer reproduced the focused unit executable and whitespace check. It found the following unresolved findings:

| Finding | Severity | Affected IDs | Disposition |
|---|---|---|---|
| Authoritative writes do not verify the supplied stream or prevent overwrite; canonical digest casing also differs from the database constraint. | High | T006; FR-002/003/008; AC-001/002; NFR-015 | **Valid.** No fix yet; T006 returned to in progress. |
| The worker does not register or execute the outbox; stale publication cannot retry and activation is not gated on verified storage. | High | T008; FR-007/009; AC-002/007; NFR-002/015 | **Valid.** No fix yet; T008 returned to in progress. |
| Presigned URLs lack persisted owner/session, expiry, length, and checksum constraints. | High | T006; FR-002/008; NFR-003 | **Valid.** No fix yet; T006 returned to in progress. |
| Idempotency/lifecycle headers are only presence checks; replay, key validation, and optimistic concurrency are absent. | Medium | T004; FR-015; NFR-003/015 | **Valid.** No fix yet; T004 returned to in progress. |
| Metrics are declared but not emitted; audit writer, health, and alert path are absent. | Medium | T004/T009; FR-014/015; NFR-004/007/012 | **Valid.** No fix yet; T009 returned to in progress. |
| Migration lacks received-part/target binding, content version, and publication object/repair evidence fields. | Medium | T005; FR-004/016; NFR-008/011/016 | **Valid.** Add before migration rehearsal. |
| Integration container tags are mutable. | Medium | T002; NFR-003/014 | **Valid.** Pin reviewed digests before running integration environments. |
| The focused tests do not cover S3, PostgreSQL, worker registration, or fault paths; one helper accepted any exception. | Medium | T006/T008/T009; AC-002/007; NFR-009/015 | **Valid.** Add exact unit and container-backed tests with the implementations. |
| Fixture README called all materials public-domain despite MIT corpus fixtures. | Low | T003; NFR-014 | **Valid and fixed** in the follow-up review-record commit. |

**Validation evidence**: locked restores and all three project builds passed; the focused unit executable and `npm run test:contract` passed; `git diff --check` passed. The initial locked restore required normal network access only to retrieve NuGet vulnerability metadata. The reviewer confirmed those commands were consistent with the range.

**Final gate result: BLOCKED.** High findings remain unresolved. No push is authorized.

## Pre-push review — 2026-09-03

**Status**: Follow-up required | **Reviewer**: gpt-5.4 | **Destination**: `origin/feature/005-image-ingest-storage` | **Range**: `origin/feature/005-image-ingest-storage..HEAD`

Initial independent review found stale/missing exact-range review evidence and incorrect master-divergence wording. Both are **valid and fixed**: this record defines the pending range and [plan.md](../plan.md) reports divergence without a reversed count. `git diff --check origin/feature/005-image-ingest-storage..HEAD` passed. Awaiting final alternate-model agreement.

## Final agreement for current pending range

The alternate reviewer rechecked the pending push range `origin/feature/005-image-ingest-storage..HEAD` and confirmed the previously identified missing/stale exact-range evidence and reversed divergence wording are fixed. The review record now documents the exact pending range, the plan uses stable non-directional divergence language, and the added ADR/contract/data-model/plan/tasks artifacts remain consistent with the approved review dispositions. No valid unresolved finding remains on the provided evidence.

**Final gate result: PASS**
