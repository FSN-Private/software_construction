# Tasks - Feature branch and recursive review policy

**Status:** In progress
**Source:** [spec.md](spec.md) and [plan.md](plan.md)

## Phase 1 - Design

- [x] T001 Create the policy specification at `docs/features/002-branch-and-review-policy/spec.md` (`FR-1` through `FR-9`, `NFR-1` through `NFR-6`).
- [x] T002 Create the implementation plan at `docs/features/002-branch-and-review-policy/plan.md` (`FR-1` through `FR-9`).
- [x] T003 Create ADR `docs/architecture/decisions/0002-feature-branches-and-review-convergence.md` (`FR-1` through `FR-9`).
- [x] T004 Commit the approved design artifacts.
  - Validation: branch name and `git diff --check`.
  - Milestone: Policy design documented.

## Phase 2 - Governance and delivery skills

- [x] T005 Require feature branches in `AGENTS.md`, the constitution, feature specification, planning, and GitHub traceability guidance (`FR-1` through `FR-4`, `AC-1` through `AC-3`).
- [x] T006 Implement validity assessment, disposition recording, recursive reviewer agreement, and no-convergence stop conditions in the adversarial-review skill (`FR-5` through `FR-9`, `AC-4`, `AC-5`).
- [x] T007 Validate policy consistency and record unit-test applicability (`AC-1` through `AC-5`, `NFR-1` through `NFR-6`).
  - Validation: reviewed all affected policy documents, confirmed `feature/002-branch-and-review-policy` is active, confirmed base `master` with no reverse divergence using `git rev-list --left-right --count master...HEAD`, and ran `git diff --check`.
  - Unit tests: not applicable; this feature changes repository documentation and workflow instructions only, with no project-owned executable logic.
- [x] T008 Commit the completed policy feature from this feature branch (`AC-6`).
  - Validation: all checks in `plan.md`; no project-owned executable logic, so unit tests are not applicable.
  - Milestone: Feature-branch and review-convergence policy complete.

## Phase 2.1 - Review remediation

- [x] T010 Resolve round-one reviewer finding R1 by enforcing the feature-branch gate in directly invokable `$speckit-specify` and `$speckit-plan` skills (`FR-1` through `FR-4`, `AC-1`).
- [x] T011 Revalidate the amended policy and commit the review remediation milestone (`AC-1` through `AC-6`).

## Phase 3 - Pre-push gate

- [x] T009 Run the recursive alternate-model review and record its final agreement.

## Traceability

| Requirement | Tasks | Validation |
| --- | --- | --- |
| FR-1 through FR-4 | T005 | Branch and policy inspection |
| FR-5 through FR-9 | T006 | Review-loop policy inspection |
| NFR-1 through NFR-6 | T005-T007 | Documentation and safety checks |
| AC-1 through AC-6 | T004-T008 | Branch, diff, and milestone checks |
