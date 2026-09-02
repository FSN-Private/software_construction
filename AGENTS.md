# Repository agent instructions

These instructions apply to the entire repository.

## Skill routing

- For every new feature or material behavior change, read and follow `.agents/skills/feature-specification/SKILL.md` before implementation. If complete approved specifications already exist, verify them rather than repeating discovery.
- For a new Spec Kit feature, choose the next `NNN` identifier under `docs/features/`, set `SPECIFY_FEATURE_DIRECTORY=docs/features/<NNN>-<slug>`, then use `$speckit-specify`. Never allow Spec Kit to create a parallel top-level `specs/` directory.
- Use `$speckit-clarify`, `$speckit-plan`, `$speckit-tasks`, `$speckit-analyze`, `$speckit-implement`, and `$speckit-converge` when their phase applies. The generated `spec.md`, `plan.md`, and `tasks.md` must comply with this repository's constitution and the skills below.
- For feature implementation, read and follow `.agents/skills/plan-driven-implementation/SKILL.md`. The numbered `tasks.md` is the sole task source of truth.
- For consequential cross-cutting technical decisions, use `.agents/skills/architecture-decisions/SKILL.md`.
- For GitHub Actions workflow design or changes, use `.agents/skills/github-actions-ci/SKILL.md`. For an already failing GitHub Actions PR check, use the official `.agents/skills/gh-fix-ci/SKILL.md` instead.
- For issue, commit, PR, review, and release linkage, use `.agents/skills/github-work-traceability/SKILL.md`. For actionable PR review threads, use the official `.agents/skills/gh-address-comments/SKILL.md`.
- For dependencies, lockfiles, third-party actions, build plugins, and artifact provenance, use `.agents/skills/dependency-supply-chain-security/SKILL.md`.
- For releases, deployments, migrations, rollbacks, or production incident follow-up, use `.agents/skills/release-operations/SKILL.md`.
- Before any local commits are pushed, including documentation, policy, agent-instruction, skill, CI, and configuration changes, read and follow `.agents/skills/adversarial-pre-push-review/SKILL.md`.
- Use these skills automatically when the request matches; the user does not need to name them.

## Repository-wide invariants

- Store feature artifacts under `docs/features/<NNN>-<feature-slug>/`; do not keep specifications only in chat, issues, or agent memory.
- Do not implement a feature until its requirements, design, non-functional considerations, acceptance criteria, and numbered implementation plan are complete and approved, or the user explicitly approves documented assumptions.
- Keep implementation, applicable unit tests, affected documentation, and plan status in the same work slice.
- Make a coherent Git commit at every planned milestone. Preserve unrelated changes and existing history.
- Never push merely because implementation was requested. A push requires explicit user authorization and a passing alternate-model adversarial review.
- The user explicitly authorizes delegation to a separate agent only as needed for the read-only alternate-model pre-push review. Use a genuinely different available model. If none is available, stop before push and report the gate as unmet.

For bug fixes, refactors, maintenance, and documentation-only work, apply documentation and planning proportional to risk. If the change introduces new behavior or materially changes a contract, treat it as a feature and use the full workflow.
