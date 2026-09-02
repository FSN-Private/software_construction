# Implementation plan - Feature branch and recursive review policy

**Status:** Approved for execution
**Specification:** [spec.md](spec.md)
**ADR:** [../../architecture/decisions/0002-feature-branches-and-review-convergence.md](../../architecture/decisions/0002-feature-branches-and-review-convergence.md)
**Feature branch:** `feature/002-branch-and-review-policy`

## Technical approach

Keep Spec Kit's optional Git extension disabled. Enforce the branch convention in repository-owned `AGENTS.md`, the Spec Kit constitution, the feature-specification skill, the plan-driven implementation skill, and the GitHub traceability skill. This makes the policy visible to both generated `$speckit-*` workflows and existing project skills without adding branch-mutating automation.

Extend the adversarial review skill with a formal loop. The implementing agent triages every reviewer finding with independent evidence, records the disposition, makes only justified changes, reruns validation, and sends the revised diff plus the dispositions back to the alternate reviewer. The loop continues until the reviewer explicitly agrees each prior finding is resolved, invalid with evidence, or validly user-waived. If reviewer and agent cannot converge, or a finding needs product authority, stop without pushing and ask the user.

## Validation strategy

- Verify the policy appears consistently in `AGENTS.md`, the constitution, and all affected skills.
- Verify branch name and current working-tree state for this policy feature.
- Validate every changed `SKILL.md` frontmatter block and `git diff --check`.
- Confirm review requirements include both agent validity assessment and reviewer agreement.
- Unit tests are not applicable unless project-owned executable logic is added; this change is policy and documentation only.

## Milestones

1. Document the branch and review-convergence design, task list, and ADR on the required feature branch.
2. Update repository governance and delivery skills.
3. Validate, update task status, and commit the completed policy feature.
