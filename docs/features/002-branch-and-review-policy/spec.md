# Feature branch and recursive review policy

**Status:** Approved
**Feature:** 002-branch-and-review-policy
**Owner:** Project maintainer
**Decision date:** 2026-09-02

## Problem and goals

The repository requires specification-driven delivery and an alternate-model pre-push review, but it does not yet require isolated feature branches or a disciplined method for deciding whether reviewer feedback is correct. Add both controls so feature work is isolated and review findings are resolved based on evidence rather than accepted blindly.

Goals:

- require every feature and material behavior change to use a dedicated branch;
- make branch identity traceable to the feature artifact directory;
- require recursive alternate-model review rounds until the reviewer agrees that every finding is resolved, invalid with evidence, or explicitly accepted by the user when a waiver is allowed;
- require the implementing agent to validate each reviewer finding before changing work;
- preserve the existing explicit-user-authorization requirement for pushes.

Non-goals:

- install Spec Kit's optional Git extension or automate branch creation through a hook;
- require a GitHub issue or pull request for every feature;
- change branch-protection rules, remote settings, or push this branch;
- waive critical or high review findings.

## Functional requirements

- **FR-1:** Before creating feature artifacts or editing feature implementation, the agent MUST start from a clean working tree and create or switch to `feature/<NNN>-<slug>`, where `<NNN>-<slug>` matches `docs/features/<NNN>-<slug>/`.
- **FR-2:** Feature work MUST NOT be created, implemented, committed, or completed on the default branch. If the checked-out branch is the default branch, the agent MUST stop before feature edits and create or request an appropriately named feature branch.
- **FR-3:** The agent MUST verify the feature branch's base and report any divergence from the default branch before implementation. It MUST not silently merge, rebase, or rewrite history to reconcile divergence.
- **FR-4:** The implementation plan and relevant GitHub/traceability guidance MUST record the feature branch as part of the feature's delivery chain.
- **FR-5:** For every reviewer finding, the implementing agent MUST independently assess validity against specifications, code, tests, diff, repository policy, and reproducible evidence before applying a change or declining the finding.
- **FR-6:** Each finding disposition MUST be recorded as `valid and fixed`, `valid and user-waived`, `invalid with evidence`, or `needs user decision`. A finding cannot be treated as closed merely because the agent disagrees with it.
- **FR-7:** After any valid fix, waiver, or invalidity evidence, the alternate reviewer MUST review the revised diff and prior-finding dispositions. Repeat this loop until the reviewer explicitly agrees that no valid unresolved finding remains.
- **FR-8:** The final review record MUST include every round, the reviewer feedback, the agent's validity assessment and evidence, changes or waivers, validation results, the reviewer’s explicit final agreement, and the exact pending push scope.
- **FR-9:** A push remains blocked unless the final reviewer result is `PASS`, all required findings are resolved, the review record is committed unchanged after final confirmation, the working tree is clean, and the user explicitly authorizes the push.

## Acceptance criteria

- **AC-1:** `AGENTS.md`, the constitution, and feature-planning skill all require `feature/<NNN>-<slug>` before feature work and reject default-branch feature work.
- **AC-2:** The implementation workflow verifies clean status, branch identity, and base divergence before selecting a task.
- **AC-3:** The traceability guidance connects feature docs, branch, commits, issue/PR where present, review, and release.
- **AC-4:** The adversarial-review skill defines validity assessment, finding dispositions, recursive review, reviewer agreement, and a safe no-convergence stop condition.
- **AC-5:** A review report template or required fields capture all review rounds and the final reviewer agreement.
- **AC-6:** The policy update is implemented and committed from `feature/002-branch-and-review-policy`; no direct feature commit is added to `master`.

## Edge cases and assumptions

- Documentation-only or emergency maintenance is not a feature unless it introduces new behavior or materially changes a contract; existing risk-proportional guidance remains applicable.
- A reviewer can be wrong. The agent must provide evidence and seek reviewer agreement rather than making a superficial change solely to silence feedback.
- If a reviewer repeatedly disagrees with evidence or a finding needs product authority, the loop stops without a push and the user decides the next action.
- A user can waive medium or low risk only when policy allows and the waiver is explicit and documented. Critical and high findings remain blocking.

## Non-functional requirements

- **NFR-1 Auditability:** Branch, base, finding, disposition, evidence, review round, model identifier, validation command, and final agreement must be recorded in repository documentation.
- **NFR-2 Reliability:** Never push while the review loop lacks explicit reviewer agreement or has unresolved valid findings.
- **NFR-3 Safety:** Never auto-merge, rebase, force-push, or rewrite history to satisfy the branch rule.
- **NFR-4 Maintainability:** Use one consistent branch convention and one review-record structure across future features.
- **NFR-5 Security and privacy:** Review evidence must not expose secrets, credentials, or sensitive vulnerability details.
- **NFR-6 Compatibility, accessibility, performance, localization, deployment, operations, and cost:** Not applicable - this is repository process policy with no user-facing runtime or deployment change.
