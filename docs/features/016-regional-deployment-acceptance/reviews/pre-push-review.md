# Feature016 adversarial pre-push review

Date2026-10-21. Implementing agent: Codex. Alternate reviewers were explicitly
spawned read-only with model overrides gpt-6-astra (round1) and gpt-5.6-sol
(round2); genuinely different from implementing model. Agent names:
/root/regional_acceptance_review and /root/regional_acceptance_final_review.
Original diagnostic reviewer was not reused without runtime model verification
and performed no Feature016 review.

Remote origin https://github.com/FSN-Private/software_construction.git;
destination refs/heads/feature/016-regional-deployment-acceptance.
Base/default1130d9cf7930cf6c1433930f8cf0129eaeb622e9, verified again with
`git ls-remote origin refs/heads/master`2026-10-21, no divergence behind.
Complete implementation range1130d9c..0c83864 (eight coherent milestones).
Exact proposed push scope: those eight commits plus a final dedicated record
commit containing this reviewed file unchanged. Record-only commit must have
parent0c83864 and change only this file. No push authority yet; no remote write,
merge, provision, protected setting, live acceptance or production operation.

## Scope and evidence provided

Full diff against base; AGENTS/constitution/adversarial instructions;
Feature016 spec/plan/tasks/contracts/research/quickstart and ADR0020; isolated
local synthesis and complete client/release/deployment/test source; coordinated
release plan and local PR draft. Owner approved Singapore nominal-client design,
not external operations. Reviewed every FR001-006, AC001-005, NFR001-007,
SC001-003 and tasks T001-T008 with future external qualification distinguished.

Prompt asked for significant blocking issues across functional/failure/state,
security/privacy/authorization/IAM/provenance, compatibility/performance/reliability,
rollback/observability/CI/tests/documentation and unrelated scope. Findings require
classification,severity,evidence,affected IDs,impact,concrete remedy; explicit
no-blocking statement per review area and PASS/BLOCKED. Read-only review only.

Validation: full `npm run test:release-evidence` PASS after final remediation;
`npm run test:ci-contract` PASS; infrastructure build/check/assert:regional,
validate:vendor,scan:secrets,validate:dependencies,validate:licenses PASS;
main-stack synth/assert PASS M3; new/existing Bash syntax and diff whitespace
PASS. Existing infrastructure vulnerability audit PASS after explicit owner
metadata-export approval; initial auto-review rejection was not bypassed.
Local tests use real child/CLI/Bash seams with fixtures and do not establish
cloud performance. Canonical provisioning input must be exact verified Linux
release zip; local synthesis is review evidence only.

## Round1: gpt-6-astra, code head55731c2

R1-01 **blocking, high**, AC003/FR006, T007. Evidence:
`scripts/deploy-ecs-release.sh` activated then cleared ROLLBACK_REQUIRED/traps;
`.github/workflows/deploy-nonprod.yml` uploaded evidence afterwards without a
failure recovery path. Failed/skipped upload left the candidate active despite
workflow failure and missing retained proof. Remedy: workflow-level recovery
using saved prior task/count fields, with failure-path tests.

Implementer disposition: **valid and fixed** in9c5a6ce. New always() recovery
runs only after successful deployment with non-success evidence upload (including
upload skipped by heartbeat failure). Source/account/current-candidate checks
cover every target before writes; all prior tasks/counts are restored even when
one update fails, then stable desired/running/pending state checked. Errors
remain failures. Existing pre-activation traps/production path retained.
Real Bash fixture tests cover stale/source/account/count rejection, zero and
nonzero restore counts, and one restore failing while remaining services still
attempted. Full release suite/CI contracts/syntax/whitespace PASS.

Reviewer usage limit prevented completed round1; no final PASS claimed from it.
Review continued with explicitly configured different available model gpt-5.6-sol.

## Round2: gpt-5.6-sol, complete range through0c83864

Independent assessment of R1-01: reviewer confirmed valid and substantively fixed,
including failed/skipped upload recovery, guarded targets, all restores, wait and
exact state verification; real Bash fixture covers these paths.

Completed source review: **PASS, no unresolved blocking findings** for exact range1130d9cf7930cf6c1433930f8cf0129eaeb622e9..0c83864a73b21b71ebe990d8de4b3c529ab33d3c. Exact-record agreement is the last local review step.
External push/PR/merge/apply authority and fresh CI/release/live qualification
are separate pending gates under T007/T008, never inferred from code review.

R2-01 **blocking, high**, AC003/FR002/FR006/NFR002, T007. Task-only recovery
left rendering_compatibility approved for the new candidate after activation,
while prior API/renderer tasks required the old digest; ordinary rendering would
fail incompatible_renderer. Original Bash fixture modeled ECS only and missed
persistent state. Disposition **valid and fixed** in0c83864: admission captures
full prior approved tuple as server-authored audit receipt under matrix/intake
locks; operator-only rollback verifies latest receipt/config/candidate/current
state and restores tuple/rejects candidate/closes intake before ECS writes. Prior
running API/renderer task digests must match snapshot. Small JSON acceptance
artifact precedes activation; existing final artifact follows. Failed/skipped
stages invoke guarded recovery, including lost activation response. Actual
PostgreSQL tests cover exact approval fields, replay, unauthorized/wrong digest/
stale/third state no-mutation, real RenderingService compatibility and concurrent
activation/rollback. Full existing PostgreSQL/S3 integration PASS.

R2-02 **blocking, high**, AC003/FR006/NFR002, T007. Initial zero-count recovery
resumed candidate intake before API stop; failed API update could leave an
unqualified incompatible candidate open. Disposition **valid and fixed** in0c83864:
zero-count recovery never resumes candidate intake; stays closed through failed
or successful stop and until next qualified deployment. Real Bash fixture asserts
rollback-before-ECS, no resume for stopped prior API or failed API stop, and resume
only after exact stable running prior service restoration.

R2-03 **blocking, high**, AC003/FR006/NFR002, T007. Abort-required flag followed
admit response; lost committed-admit response skipped abort and left intake paused
when tasks restored. Disposition **valid and fixed** in0c83864: flag set before
idempotent admit request. Actual extracted admission/rollback shell test simulates
committed admission then failed response and proves abort precedes service restore.

R2-N1 **non-blocking, low**, operations docs: retained Version does not preserve
versions after parent Function deletion. Disposition **non-blocking note, fixed**:
release plan states explicit retirement removes function and versions together,
while retention supports ordinary in-place version rollback. No resource-policy
change or permission expansion.

R2-N2 **non-blocking, low**, stale plan wording described only postactivation
upload recovery and unchanged app rollback. Disposition **non-blocking note, fixed**:
plan/contracts/runbook/ADR/release record now describe both evidence stages,
activation ambiguity, server-authored receipts and persistent-state recovery.

Final additional validation: actual migrated PostgreSQL rollback/real compatibility
and five concurrent activation/rollback races PASS; full existing PostgreSQL/S3
migration/ingest/HTTP/rendering/recovery integration PASS using existing local test
services and isolated schema/bucket. Full release-evidence suite, ci-contract,
control bounds/privacy/target/failure checks, actual Bash recovery/order/lost-admit
trap tests, full API format verification and whitespace/syntax checks PASS.
No schema, secret store, dependency, IAM or app allocation change. Earlier convergence
record preceded R2 findings; receipt remediation closes those mapped T007 gaps.

## Final review agreement and gate

gpt-5.6-sol completed the full exact committed range and explicitly reports:
"PASS, no unresolved blocking findings", subject only to exact-record agreement.
It explicitly agrees R1 was valid, the first fix incomplete in persistent state,
and all three R2 blocking dispositions valid and fixed by0c83864.

No blocking issue remains in: functional requirements/state/failures;
source/code/version/result/manifest/Linux ZIP provenance; token privacy/child
credentials/authorization/IAM; native/readiness/activation/evidence/rollback;
production/historical compatibility; infrastructure/cost/supply chain; real
CLI/Bash/PostgreSQL concurrency/compatibility tests; docs/ADR/tasks/traceability
and unrelated scope. Independent reruns passed API build/format, complete
release-evidence, CI contracts, targeted control/deployment/recovery and whitespace.

Exact-record confirmation requested from /root/regional_acceptance_final_review:
this entire pending file accurately records the review/dispositions, no valid
unresolved blocking finding remains, and it can be committed unchanged as the sole
record commit with parent0c83864. **Final gate PASS takes effect only upon that
reviewer's explicit confirmation of these exact bytes.** Commit unchanged after
confirmation; verify clean tree, parent and only-record scope before any push.
External user authority, CI/release and real T008 qualification remain separate.
