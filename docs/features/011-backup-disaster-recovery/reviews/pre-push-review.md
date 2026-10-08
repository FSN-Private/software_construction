# Feature 011 independent adversarial review

**Date**: 2026-10-08
**Alternate model**: `gpt-6-astra` (implementer `gpt-6.1-sol`)
**Remote/destination**: `origin`, `feature/011-backup-disaster-recovery-fresh`
**Base**: `8b2a121` (`origin/master`); initial reviewed range `8b2a121..b6942fe`
**Push scope**: None authorized. This is an interim read-only review while local approvals are pending, not a completed-feature certification.
**Gate**: BLOCKED; required work and qualification remain unfinished.

## Round 1

Prompt: inspect the complete change inventory and approved specifications/plan/tasks, prioritizing significant correctness, authorization, integrity and release-boundary defects. Include the recent offline attestation, historical access separation, readiness and retention changes. Distinguish new defects from already tracked unbuilt work. Do not modify files or external state. Supplied validation: recovery executable checks and worker Release build pass; actual PostgreSQL/S3/API regression passes; prior Docker/native TLS and infrastructure checks are recorded locally, with no AWS qualification.

The reviewer inspected the complete change inventory and task mappings, with detailed review concentrated on access/attestation, historical-proof separation, readiness, trusted dump restore, materialization and release qualification. It did not rerun tests or certify all approximately 30,900 added lines.

### R1-001 — Blocking, High: foreign access probe cannot pass real middleware

Evidence: `RecoveryOwnerClaimsTransformation` changed an authenticated but unmapped principal into an unauthenticated principal. `/v1/recovery/health` requires authorization and standard middleware therefore challenges with 401 before the handler. `RecoveryOwnerAccessProbe` and offline preparation require 403 for a valid authenticated foreign principal. Separate transformation/fake-probe tests missed the composed pipeline.

Impact: independent owner probing and attestation cannot complete. Mapping: T030; FR-008/010/016; AC-003/010. Suggested remediation: preserve a safe authenticated-but-unmapped denial without owner authority; do not accept arbitrary 401 as foreign authentication evidence. Require actual JWT/middleware owner=200, foreign=403, invalid=401 and pass real responses through probe/preparation.

Implementer assessment: **valid and fixed, follow-up agreement pending**. Successful JWT authentication with no verified owner mapping now yields an authenticated principal with **zero claims**: no subject, owner group, role or forged binding survives. Mapping lookup failures still fail closed. A real Kestrel/JWT/claims/authorization/endpoint test includes the production result handler and worker probe; it verifies owner 200, signed foreign 403, invalid and absent JWT 401, then prepares and verifies the separate owner-key attestation using the actual HTTP transcript. Only the test transport redirects private-network requests to local Kestrel. `node tests/integration/recovery/run.mjs` passes; the referenced worker project and its already locked dependencies are reused, with no package version/source change. This remains subject to alternate-model follow-up.

### Already tracked blockers

T029 retained restore/promotion; T034 sanitized replacement/purge journals/cleanup; T035 independent holds/releases/provider reconciliation; T036 persistent periodic verification proof/cadence qualification; independent AWS acceptance, owner workload/budget approval and RPO/RTO evidence. These remain incomplete requirements. Specific local approvals for ADR-0017 retention extension and the full-media proof proposal arrived after this review and their implementation continues.

No additional supported blocker found in the inspected offline signature/binding checks, historical-proof separation, readiness/latest-validation guard, full-read retention checks, trusted schema/COPY restore, exact-version materialization or release qualification bindings. No non-blocking notes were reported. The reviewer explicitly returned **BLOCKED** for the entire feature because the tracked requirements remain open.
