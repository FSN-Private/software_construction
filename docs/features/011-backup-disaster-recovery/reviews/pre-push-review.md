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


## Implementation round 2 — 2026-10-08

Reviewer: **gpt-6-astra**, read-only alternate-model follow-up against `8b2a121..3fc994e`, including the pending review record. **R1-001 disposition accepted: valid and fixed.** The reviewer inspected the actual JWT/middleware/probe/independent-attestation test and confirmed the authenticated foreign denial without owner authority. No new blocker was found in the inspected authorization, immutable validation replay or scoped retention extension. Tests were inspected, not independently rerun; no cloud evidence was certified.

The whole-feature gate remains **BLOCKED** on T029/T034/T035/T036 and external qualification. The reviewer reported one non-blocking documentation note: the paragraph labeled current checkpoint still described approvals as pending. It is now explicitly historical and records that both scoped approvals arrived. Earlier results remain preserved.

## Implementation round 3 — 2026-10-12

Reviewer: **gpt-6-astra**, read-only follow-up against `8b2a121..b762494`. No new supported blocking finding in the inspected private FULL_VERIFY implementation: canonical signatures bind exact point/capture/owner/library/checkpoint/build/time; replay, unknown outcomes and concurrency preserve original read time; ordinary metadata/retention/access checks and forced drill reads remain. No IAM, publication, hold-release, purge or promotion authority was added. Tests were inspected, not independently rerun.

**R1-001 remains valid and fixed.** The reviewer confirmed that the round 2 record accurately describes its agreement and limitations and that the historical approval-state documentation note is resolved. The whole-feature gate remains **BLOCKED** on the tracked restore, purge, hold-release/convergence and external qualification work. This round does not review subsequent retained-restore changes.

## Implementation round 4 — 2026-10-12

Reviewer: **gpt-6-astra**, read-only against `8b2a121..21b51d4`. Tests were inspected, not independently rerun. R1-001 remains valid and fixed; the round 3 record and its scope limitations were accepted. The reviewer returned **BLOCKED** with two supported new findings:

- **R4-001 — Blocking, High; T029, FR-009/010, AC-005/006.** The retained executor passed the source capture manifest digest to validation, while the imported catalog binds the final manifest including owner-access evidence. Every otherwise successful execution would fail with `restore_validation_session_or_manifest_mismatch`. **Valid and fixed; independent follow-up pending.** The executor now persists the authenticated catalog's final manifest digest and retains source-bound closure validation. The real PostgreSQL regression constructs distinct source/final manifests, signs/imports the final catalog, proves the source digest is rejected, and invokes the actual executor's persistence helper twice to establish a successful immutable revision and exact replay. This exercises the failing database boundary; it is not a complete native ExecuteAsync orchestration test, which remains in T029.
- **R4-002 — Blocking, Medium; T029, FR-009/010, NFR-002/009.** Attempts shared scratch files by session; a losing advisory-lock attempt could delete the winning attempt's TLS certificate or dump in its finally block. **Valid and fixed; independent follow-up pending.** Each invocation now has a unique attempt directory under the deterministic session directory and deletes only its own directory. A synchronized filesystem regression demonstrates loser cleanup preserves winner certificate bytes. Real PostgreSQL checks establish same-session lease exclusion and independent-session concurrency. The administration connection is unpooled so disposal releases the session lease.

Validation after both fixes: `dotnet run --project tests/recovery/Platform.Recovery.Tests.csproj -c Release --no-restore` PASS; `dotnet build apps/recovery-worker/Platform.Recovery.Worker.csproj -c Release --no-restore` PASS, zero warnings/errors; `node tests/integration/recovery/run.mjs` PASS. Initial regression attempts found a missing worker namespace, a fixture data-source password redaction issue and an incorrect expected exception type; these test issues were corrected before the passing run. No AWS action occurred. Whole-feature tracked workflow and qualification gaps remain open.

## Implementation round 5 — 2026-10-12

Reviewer: **gpt-6-astra**, read-only against `8b2a121..a36db4e`, including the review record. **R4-001 and R4-002 accepted as valid and fixed; R1-001 remains valid and fixed.** No new supported significant blocking finding in the revised paths. The reviewer independently confirmed clean HEAD and range-wide `git diff --check`; other validation was inspected, not rerun. It explicitly accepted the record's limitation that these database/filesystem regressions do not constitute full native ExecuteAsync orchestration coverage. The whole-feature gate remains **BLOCKED** for tracked unfinished work and qualification. Subsequent native-boundary tests and purge input changes are outside this round's reviewed range.

## Implementation round 6 — 2026-10-12

Reviewer: **gpt-6-astra**, read-only against `8b2a121..a57335c`. Prior blocking dispositions R1-001/R4-001/R4-002 remain accepted. No new supported significant blocking defect in bounded purge inputs, attempt scratch isolation or native retained-clone database boundaries. The reviewer accepted marking T036 locally complete while retaining full-volume/RPO qualification in T024/T026. The round 5 record accurately represents its agreement and limitations. It independently confirmed clean HEAD and range-wide `git diff --check`; reported tests were inspected, not rerun. The whole-feature gate remains **BLOCKED** on unfinished workflow and external qualification.

The reviewer found no supported material contradiction in the proposed ADR-0018 snapshot preservation, permanent exclusions, exact-target journals, explicit renewal or bound hold release. At round 6 review, IAM was unattached and approval was pending. One **non-blocking design clarification**, T034/T035: identify the snapshot-evidence producer and hold-proof handoff because the proposed purge role has neither database-secret retrieval nor VerificationTable reads. The proposal now specifies existing-verifier production/export to bounded private operator files, with purge-side pinned signature/exact-history verification and missing-proof denial. The reviewer inspected the pending record/proposal, accepted that clarification and confirmed no new supported text blocker. The owner subsequently specifically approved local protocol/IAM implementation; this round does not certify the subsequent implementation.

## Implementation round 7 — 2026-10-13

Reviewer: **gpt-6-astra**, read-only against `8b2a121..e4a3877` and the three-file working fix atop that HEAD. **R7-001 — Blocking, High; T034, FR-013/017, AC-009, NFR-002/016:** HEAD returns generic errors without the structured `NoSuchVersion` body required by the deletion journal and purge-point verifier. The method-override fake manufactured that error, masking failed successful-delete/lost-response reconciliation. AWS documents this behavior in [HeadObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html).

Implementer assessment: **valid and fixed; independent disposition accepted**. Both paths share an exact-version GET helper. Nonempty objects request one byte and validate exact version, total length, response length and actual bounded bytes; empty objects require zero bytes. Only structured `404 NoSuchVersion` establishes absence. Denial and generic errors remain pending. Actual AWS SDK request marshalling and response/error unmarshalling checks cover present/empty/absent versions, denial, generic 404, altered version/length and truncation. `dotnet run --project tests/recovery/Platform.Recovery.Tests.csproj -c Release --no-restore` passes. The first HTTP fixture used a non-S3 content type; correcting it to `application/xml` exercises the actual structured S3 error decoder.

The reviewer accepted the fix and tests by inspection, not independent execution; no other supported significant blocker was found in the journal, cross-point receipt matching, historical-access checks or scoped IAM changes. R1-001/R4-001/R4-002 remain accepted. Range-wide diff checks pass. The whole-feature gate remains **BLOCKED** on tracked unfinished workflow and external qualification; this is not completion certification.

## Archived specification-only review (2026-09-03)

The following pre-existing review is retained unchanged. Its PASS applies only to the September specification and does not override the current implementation BLOCKED gate.

# Adversarial Specification Review

**Status**: Round 1 findings fixed; awaiting independent Round 2
**Reviewer model**: gpt-5.5
**Review date**: 2026-09-03
**Base**: `feature/003-darktable-version-control`
**Scope**: Feature 011 specification and ADR-0003; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| Purge did not cover recovery copies or make recovery unavailable afterward. | High | Parent FR-028, AC-013; 011 FR-017, AC-009 | **Valid and fixed.** FR-017 and AC-009 define eligibility, primary/recovery purge, recovery denial, and audit evidence. |
| Restore validation omitted owner identity, permission policy, and authorization boundaries. | Medium | Parent AC-007; 011 AC-010 | **Valid and fixed.** AC-010 requires these checks before promotion. |
| Failure alert target was one hour, not 15 minutes. | Medium | Parent NFR-009; 011 FR-018, AC-011, NFR-007 | **Valid and fixed.** The binding 15-minute alert requirement is explicit. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

Round 2 corrected the parent traceability citations above.

## Final agreement

The alternate reviewer confirmed purge lifecycle, permission validation, 15-minute alerting, and corrected traceability. No valid unresolved finding remains.

**Final gate result: PASS**

## Implementation round 8 � 2026-10-13

Reviewer: **gpt-6-astra**, read-only against `8b2a121..07890d6` plus pending snapshot-preservation changes, concentrating on changes since `dc54dc0`. No push is authorized. The reviewer inspected hold approval/proof custody, native schema/COPY validation, private preservation evidence, tests and pending documentation. It independently ran `git diff --check dc54dc0` (PASS); reported executable/native tests were inspected, not rerun. This was not an exhaustive re-review of the complete feature. A preliminary scoped PASS was withdrawn after further inspection established R8-001; it is not final agreement.

**R8-001 � Blocking, High; T034, ADR-0018, FR-012/017, AC-009, NFR-016:** a known migration/schema inventory and honest snapshot coverage do not prove existing binary evidence values are media-free. Migration 022 allows arbitrary nonempty `recovery_points.manifest_bytes`; native validation authenticates selected-scope commits and global audit but does not inspect every private recovery binary field. Payload bytes present before capture can therefore survive exact dump/hash/schema/COPY/coverage checks and receive a false preservation proof. Other affected values include catalog/observation, identity and purge evidence blobs and foreign-scope commit bytes. The active deletion guard prevents immediate deletion but does not make that proof correct.

**Disposition: valid and fixed in `ac6e8e5`.** Preservation now enumerates binary columns and refuses every non-null value without a completed typed validator. Global audit authentication remains in the native pipeline; canonical graph bytes are validated across every retained owner/library scope. Known migration reads are bounded. The native before-capture regression inserts payload bytes into the existing recovery-point field, recaptures genuine coverage, restores from pinned schema, demonstrates successful ordinary validation, then proves preservation refusal. Further native regressions reject unknown migrations, added payload columns, large objects and foreign-scope commit corruption. Populated additional binary evidence remains conservatively unqualified until its validator is implemented under T034; this limitation and the unchanged deletion guard are documented.

Validation after remediation: `dotnet run --project tests/recovery/Platform.Recovery.Tests.csproj -c Release --no-restore` PASS; recovery-worker Release build PASS with zero warnings/errors; scoped worker whitespace verification PASS; `npm run test:backup-native` and final `node tests/integration/recovery-native/run.mjs` PASS. The earlier hold slice additionally passed real PostgreSQL/S3/API integration. No external qualification or AWS operation occurred. Follow-up review of the complete revised range and this record is pending; whole-feature gate remains **BLOCKED** on tracked implementation and qualification work.

## Implementation round 9 � 2026-10-13

Reviewer: **gpt-6-astra**, read-only against the complete revised range `8b2a121..ac6e8e5`, concentrating on the resumed slices `dc54dc0..ac6e8e5`, plus the pending round-8 review record. **R8-001 is accepted as valid and fixed in `ac6e8e5`; scoped gate: PASS.** Populated binary values without completed validators are refused, global audit authentication remains intact, and canonical commits are validated for every retained owner/library. Native regressions cover payload present before capture with honest coverage and corrupted foreign-scope commits. No new supported significant blocking finding was identified. Unsupported binary evidence remains protected and explicitly tracked in T034.

The reviewer independently verified committed-range and pending-record whitespace checks; tests were inspected, not rerun. It accepted the record's finding, remediation, validation evidence and limitations. **Whole-feature gate remains BLOCKED** on tracked unfinished restore/promotion, purge/proof consumption/exclusion/completion, lifecycle-hold custody, convergence and external qualification. No push or merge is authorized. This agreement supersedes round 8's pending follow-up status without removing its history. The reviewer will inspect this final pending record before it is committed unchanged as the dedicated review checkpoint.
