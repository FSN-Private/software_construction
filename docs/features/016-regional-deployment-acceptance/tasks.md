# Tasks: Regional deployment acceptance

Authoritative ledger. Branch `feature/016-regional-deployment-acceptance` starts
clean at1130d9c,0divergence from master; diagnostic branch/history preserved.
Approved user design2026-10-21. External publication/provisioning still gated.

## Setup and foundation (M0)

- [x] T001 Specify approved scope, stories, ACs/NFRs and checklist in spec.md;
  clarify material questions against owner decision. Commit ed1d175.
- [x] T002 Plan/research/contracts/data model/quickstart, ADR0020 and traceability;
  analyze consistency/constitution before implementation. No unresolved material
  questions, no top-level specs/ directory, no new dependency or delegation.

## User Story 1/2: regional client and trust (M1-M2)

- [x] T003 [US1] Implement scripts/regional-acceptance/handler.mjs and
  scripts/package-regional-acceptance.ps1; reuse both smoke scripts and PNG.
  Test real child order/success/failure/timeout/bounds/environment/token privacy,
  fixed target/region/version/input rejection and exact zip contents/checksum.
  Map FR001/004/005/006, AC001/003/005, NFR001-004/007, SC001/003.
- [x] T004 [US2] Add source-bound artifact/provenance support in release generator/
  verifier and scripts/invoke-regional-acceptance.mjs. Test real CLI seam:
  tampering/wrong config/version/code/source/result/region/counts/NaN, FunctionError,
  transport ambiguity/no retry/temp cleanup and zero secret disclosure. Recompute
  all samples/thresholds. Map FR003/005/006, AC003-005, NFR002-005, SC001/003.

## User Story 1: infrastructure and delivery (M3-M4)

- [x] T005 [US1] Add isolated regional-acceptance-app.ts/stack.ts, published code
  version, role/log/timeout/concurrency/region/production guards; test synthesized
  exact zip/code/IAM scope, no function URL/events/VPC/NAT/main stack changes and
  bounded logs/compute. Map FR003/004, AC004/005, NFR001-006, SC001/003.
- [ ] T006 [US1] Integrate bundle build/CI and nonprod invocation into build-release,
  deploy-nonprod and deploy-ecs-release.sh. Preserve production/direct path and
  native/readiness/bootstrap/admit/activate/error trap/evidence ordering. Test real
  extracted workflow/script failure paths and update spec008 interpretation,
  Feature011 task linkage, runbook and release plan. Map FR001-006, AC001-005,
  NFR001-007, SC001-003. No new action/package or token permission.

## Cross-cutting verification and delivery (M5)

- [ ] T007 Run complete relevant release-evidence, infrastructure/security,
  syntax/whitespace checks; analyze/converge spec/plan/tasks against implementation,
  remediate gaps, record alternate-model pre-push PASS and milestone commits.
  Prepare concrete PR/template/IAM/cost/rollback review package and obtain final
  publication/integration/provisioning authority. Map every FR/AC/NFR/SC.
- [ ] T008 After explicit authority: source-bound PR/master CI/release/security,
  exact zip/source/version/template verification, priced scoped client apply,
  protected numeric version config, bootstrap real regional rendering/ingest/
  activation/evidence upload and only then qualified fixture IDs. Record actual
  results and clean superseded redundant artifacts proportionally. Failure is
  not completion; remediate safe in-scope defects and repeat review as required.
  Map all FR/AC/NFR/SC; cloud work requires available credentials/authority.

## Dependencies, validation and strategy

T001 -> T002 -> T003 -> T004 -> T005 -> T006 -> T007 -> T008. Stories share a
single trusted client, so shipping US1 without US2 would be unsafe; complete
locally before publication. No agent delegation except required read-only
alternate-model pre-push review. Unit tests are not applicable to docs/ADR;
cross-artifact analysis and actual code tests validate their claims instead.
Package/handler tests and infra synthesis can run independently once client code
exists; use ordinary batched local commands, not extra agents.

Traceability: FR001/AC001/NFR001 -> T003/T006/T008;
FR002/AC002 -> T006/T008; FR003/AC004/NFR005 -> T004/T005/T006/T008;
FR004/NFR003/006 -> T003/T005/T007/T008; FR005/AC005 -> T003/T004/T005/T007;
FR006/AC003/NFR002 -> T003/T004/T006/T008; NFR004/007 -> T003/T006/T007;
SC001 -> T003-T008; SC002 -> T007/T008; SC003 -> T003-T008.

M0 analysis: requirements/plan/tasks consistent, every ID mapped, approved
geography versus remote-user limitation explicit, no constitution conflict,
unmapped requirement, unresolved clarification or hidden failed-run acceptance.

M1 validation2026-10-21: `node tests/infrastructure/release/regional-client.mjs`
PASS. Standard ZIP packaging produces repeated identical local bytes, exact
canonical source entries and unchanged PNG. Real fixture child processes verify
order, minimal environment/token isolation, timeout/output bounds and sanitized
failure diagnostics; malformed inputs/counts/nonfinite/over-limit values reject.
These are fixture child programs, not real cloud performance qualification.
Node syntax and whitespace pass. Windows uses existing Windows PowerShell5.1;
Ubuntu runner has pwsh. No tool installation or package dependency added.

M2 validation2026-10-21: regional-invoker and regional-release-evidence checks
PASS, along with existing generate-release-evidence and complete release-workflow
suite. Real CLI fixture processes cover code/config/version/source/result drift,
FunctionError, transport/no retry, malformed/nonfinite/over-limit data, payload
cleanup and secret suppression. Restricted response file is precreated too.
Regional zip is a checksummed SBOM/provenance subject with/without recovery;
tampered bytes/subjects and unknown artifact sets fail. Historical release formats
still verify. No actual Lambda invocation or healthy cloud result is claimed.

M3 validation2026-10-21: TypeScript build/check, isolated regional synthesis and
negative contexts, staged ZIP byte equality, code-bound retained version, exact
512MiB/300s/concurrency1/7day controls and scoped log/invoke IAM tests PASS.
Full infrastructure validation also passes existing main-stack assertions,
vendor integrity, secret scan, dependency-tree and license checks. Registry
vulnerability audit remains pending under T007: restricted network failed, then
automatic approval review rejected exporting dependency metadata without specific
authorization. Owner audit approval requested; no bypass or PASS claim. No new
dependency, cloud resource/apply, app stack or data mutation.
