# Tasks: Non-production Cost Control

**Input**: [spec.md](spec.md) and [plan.md](plan.md)

**Tests**: One mocked AWS lifecycle test covers non-trivial state logic; existing release and infrastructure contract suites cover workflow and CDK integration.

## Phase 1: Lifecycle Contract

- [x] T001 Implement the idempotent, ordered, bounded nonprod lifecycle script and mocked AWS tests in `scripts/manage-nonprod-lifecycle.sh` and `tests/infrastructure/release/nonprod-lifecycle.mjs` (FR-002/003/004/005/008, AC-002/003/004, NFR-001/004/007/009/010; SC-001). `node tests/infrastructure/release/nonprod-lifecycle.mjs` passes.

## Phase 2: Protected Automation and Infrastructure

- [x] T002 Add the protected manual and scheduled lifecycle workflow and deployment database-start integration in `.github/workflows/nonprod-lifecycle.yml` and `.github/workflows/deploy-nonprod.yml` (FR-001/002/006/008, AC-001/005/006, NFR-002/003/006/008/012/014; SC-001/003). Workflow contract validation is included in T003.
- [x] T003 Export the database identifier, grant instance-scoped RDS lifecycle permissions, propagate GitHub configuration, and extend CDK/workflow contract validation (FR-007, AC-006, NFR-003/006/010; SC-002). Full infrastructure validation, release workflow contracts, and reliability contracts pass.

## Phase 3: Operations and Completion

- [x] T004 Update nonprod bootstrap and recovery guidance with lifecycle, schedule, cost envelope, failure recovery, and initial-deployment constraints (FR-006/008, AC-007, NFR-002/008/013/014; SC-003).
- [x] T005 Run infrastructure and release validation, inspect the complete diff, converge against this specification, and record results (all requirements and criteria). Full infrastructure, security, release, reliability, CI-contract, Bash syntax, workflow YAML, diff, prerequisite, and 33-ID traceability checks pass after the second review remediation; no actionable convergence gap remains.
- [x] T006 Complete an independent alternate-model adversarial pre-push review, remediate valid findings, and create the milestone commit; pushing remains separately authorized (NFR-003/006/007/009). Round 3 passed with no unresolved blocking findings after credential-session and failure-reporting remediation.

## Traceability

| Requirement / criterion | Tasks |
|---|---|
| FR-001 | T002, T005 |
| FR-002 | T001, T002, T005 |
| FR-003, FR-004, FR-005 | T001, T005 |
| FR-006 | T002, T004, T005 |
| FR-007 | T003, T005 |
| FR-008 | T001, T002, T004, T005 |
| AC-001 | T002, T005 |
| AC-002, AC-003, AC-004 | T001, T005 |
| AC-005, AC-006 | T002, T003, T005 |
| AC-007 | T004, T005 |
| NFR-001, NFR-004, NFR-007, NFR-009 | T001, T005 |
| NFR-002, NFR-008, NFR-012, NFR-014 | T002, T004, T005 |
| NFR-003, NFR-006, NFR-010 | T001, T002, T003, T005, T006 |
| NFR-005 | T002, T005 |
| NFR-011, NFR-015 | T005 |
| NFR-013 | T004, T005 |
| SC-001 | T001, T002, T005 |
| SC-002 | T003, T005 |
| SC-003 | T002, T004, T005 |
