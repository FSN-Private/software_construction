# Repository agent instructions

These instructions apply to the entire repository.

## Skill routing

- For every new feature or material behavior change, read and follow `.agents/skills/feature-specification/SKILL.md` before implementation. If complete approved specifications already exist, verify them rather than repeating discovery.
- For feature implementation, read and follow `.agents/skills/plan-driven-implementation/SKILL.md`. The numbered `implementation-plan.md` is the sole task source of truth.
- Before any feature branch or feature commits are pushed, read and follow `.agents/skills/adversarial-pre-push-review/SKILL.md`.
- Use these skills automatically when the request matches; the user does not need to name them.

## Repository-wide invariants

- Store feature artifacts under `docs/features/<feature-slug>/`; do not keep specifications only in chat, issues, or agent memory.
- Do not implement a feature until its requirements, design, non-functional considerations, acceptance criteria, and numbered implementation plan are complete and approved, or the user explicitly approves documented assumptions.
- Keep implementation, applicable unit tests, affected documentation, and plan status in the same work slice.
- Make a coherent Git commit at every planned milestone. Preserve unrelated changes and existing history.
- Never push merely because implementation was requested. A push requires explicit user authorization and a passing alternate-model adversarial review.
- The user explicitly authorizes delegation to a separate agent only as needed for the read-only alternate-model pre-push review. Use a genuinely different available model. If none is available, stop before push and report the gate as unmet.

For bug fixes, refactors, maintenance, and documentation-only work, apply documentation and planning proportional to risk. If the change introduces new behavior or materially changes a contract, treat it as a feature and use the full workflow.
