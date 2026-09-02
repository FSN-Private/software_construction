# software_construction Constitution

## Core Principles

### I. Specification is the source of truth

Every feature or material behavior change MUST have an approved Spec Kit artifact set under `docs/features/<NNN>-<slug>/` before implementation. `spec.md` defines requirements, acceptance criteria, and non-functional requirements; `plan.md` defines technical design; and `tasks.md` is the sole numbered execution source of truth. Agents MUST set `SPECIFY_FEATURE_DIRECTORY` to the feature directory before invoking feature-scoped `$speckit-*` skills. Chat, issues, and pull requests may summarize but MUST NOT become competing specification sources.

### II. Complete, testable delivery

Each completed task MUST include the code, applicable unit tests, documentation, and stated validation that satisfy its mapped requirements. When unit tests are not applicable, `tasks.md` MUST state the concrete reason and the chosen higher-level verification. Every requirement, acceptance criterion, and non-functional requirement MUST map to an implementation or verification task.

### III. Non-functional quality and supply-chain care

Specifications MUST address security, privacy, accessibility, compatibility, performance, reliability, observability, maintainability, testability, deployment, and operations with measurable targets or an explicit inapplicability rationale. New dependencies, build tools, third-party actions, and release tooling MUST use authoritative sources, pinned versions where practical, and documented validation.

### IV. Coherent milestones and documentation

Each named milestone MUST end in one or more coherent Git commits after its relevant validation passes. Do not mix unrelated work, rewrite history, or include user changes without authorization. Documentation, architecture decisions, migration or release records, and task status MUST change with the implementation they describe.

### V. Independent pre-push review

No local commits may be pushed without explicit user authorization and a passing read-only adversarial review by a genuinely different available LLM model. Critical and high findings MUST be resolved; medium findings require resolution unless the user explicitly accepts and documents the risk. The review record MUST be committed under the applicable feature or `docs/reviews/` path before push.

## Tooling and artifact constraints

This repository uses GitHub Spec Kit `v1.0.3` with the Codex integration and PowerShell scripts. Existing project skills in `.agents/skills/` remain authoritative for their scoped workflows and coexist with `speckit-*` skills. The optional Spec Kit Git extension is not installed. Existing Git branch, milestone, and push policies remain in effect.

## Development workflow

For a production feature, use `$speckit-specify`, `$speckit-clarify`, `$speckit-plan`, `$speckit-checklist`, `$speckit-tasks`, `$speckit-analyze`, `$speckit-implement`, and `$speckit-converge` as applicable. Before implementation, resolve material ambiguity and obtain specification approval. During implementation, update `tasks.md` without renumbering existing task IDs. Before any push, invoke the repository's adversarial pre-push review workflow.

## Governance

This constitution governs generated Spec Kit workflows and supplements `AGENTS.md`; where both address the same repository process, apply the stricter requirement. Amendments require a documented rationale, maintainer approval, and updates to affected skills, templates, specifications, and tasks. Reviewers MUST verify constitution compliance as part of planning, implementation, and pre-push review.

**Version**: 1.0.0 | **Ratified**: 2026-09-02 | **Last Amended**: 2026-09-02
