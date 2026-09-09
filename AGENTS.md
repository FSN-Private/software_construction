# Repository agent instructions

These instructions apply to the entire repository.

## Skill routing

- For every new feature or material behavior change, read and follow `.agents/skills/feature-specification/SKILL.md` before implementation. If complete approved specifications already exist, verify them rather than repeating discovery.
- Before creating feature artifacts or editing feature code, require a clean working tree and create or switch to `feature/<NNN>-<slug>`, matching `docs/features/<NNN>-<slug>/`. Feature work on the default branch is forbidden. Verify and report divergence from the default branch; do not silently merge, rebase, or rewrite history to reconcile it.
- For a new Spec Kit feature, choose the next `NNN` identifier under `docs/features/`, set `SPECIFY_FEATURE_DIRECTORY=docs/features/<NNN>-<slug>`, then use `$speckit-specify`. Never allow Spec Kit to create a parallel top-level `specs/` directory.
- Use `$speckit-clarify`, `$speckit-plan`, `$speckit-tasks`, `$speckit-analyze`, `$speckit-implement`, and `$speckit-converge` when their phase applies. The generated `spec.md`, `plan.md`, and `tasks.md` must comply with this repository's constitution and the skills below.
- For feature implementation, read and follow `.agents/skills/plan-driven-implementation/SKILL.md`. The numbered `tasks.md` is the sole task source of truth.
- For consequential cross-cutting technical decisions, use `.agents/skills/architecture-decisions/SKILL.md`.
- For GitHub Actions workflow design or changes, use `.agents/skills/github-actions-ci/SKILL.md`. For an already failing GitHub Actions PR check, use the official `.agents/skills/gh-fix-ci/SKILL.md` instead.
- For issue, commit, PR, review, and release linkage, use `.agents/skills/github-work-traceability/SKILL.md`. For actionable PR review threads, use the official `.agents/skills/gh-address-comments/SKILL.md`.
- For dependencies, lockfiles, third-party actions, build plugins, and artifact provenance, use `.agents/skills/dependency-supply-chain-security/SKILL.md`.
- For releases, deployments, migrations, rollbacks, or production incident follow-up, use `.agents/skills/release-operations/SKILL.md`.
- Before pushing any local commits, read and follow `.agents/skills/adversarial-pre-push-review/SKILL.md`.
- Use these skills automatically when the request matches; the user does not need to name them.

## Repository-wide invariants

- Store feature artifacts under `docs/features/<NNN>-<feature-slug>/`; do not keep specifications only in chat, issues, or agent memory.
- Develop and commit every feature and material behavior change on its matching `feature/<NNN>-<feature-slug>` branch; never on the default branch.
- Do not implement a feature until its requirements, design, non-functional considerations, acceptance criteria, and numbered implementation plan are complete and approved, or the user explicitly approves documented assumptions.
- Keep implementation, applicable unit tests, affected documentation, and plan status in the same work slice.
- Make a coherent Git commit at every planned milestone. Preserve unrelated changes and existing history.
- Never push merely because implementation was requested. A push requires explicit user authorization and a passing alternate-model adversarial review with no unresolved blocking findings. Merging additionally requires explicit user authorization.
- The user explicitly authorizes delegation to a separate agent only as needed for the read-only alternate-model pre-push review. Use a genuinely different available model. If none is available, stop before pushing and report the gate as unmet.

## Execution persistence

- A request to complete an approved feature authorizes execution of all of its in-scope numbered tasks, validation, documentation, milestone commits, convergence work, and remediation of valid blocking review findings. Do not treat a progress update, a failed first attempt, a milestone, or a `BLOCKED` review gate as a handoff or completion point.
- Keep working through the next unblocked task and through review-remediation cycles in the same request. Record progress in the plan and use commentary for short updates, but give a final response only when the requested outcome is complete or a true blocker prevents further safe progress.
- A true blocker is limited to a material product or design decision, an explicit user waiver or risk acceptance, missing authority or credentials, an unavailable required external dependency, an unsafe/unrelated worktree conflict, or a validation failure that remains unreproducible after reasonable in-scope diagnosis. State the exact blocker, evidence, and the smallest decision or access needed. Do not ask the user to decide routine implementation details or to authorize a retry, a test, or an in-scope fix.
- When adversarial review finds valid, in-scope blocking implementation work while the active request is to complete the feature or prepare it for push, map it to the numbered plan, remediate it, validate it, and obtain another independent review. `BLOCKED` is the status of the current push gate, not permission to abandon the feature. Record non-blocking findings without delaying the push. For a review-only request, record findings without changing implementation unless the user asks for fixes.

For bug fixes, refactors, maintenance, and documentation-only work, apply documentation and planning proportional to risk. If the change introduces new behavior or materially changes a contract, treat it as a feature and use the full workflow.
