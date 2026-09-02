---
name: feature-specification
description: Define or complete requirements, design, and non-functional specifications for a proposed feature before implementation. Use for feature requests, material behavior changes, or when an implementation plan lacks approved specifications.
---

# Feature Specification

Produce an implementation-ready Spec Kit artifact set under `docs/features/<NNN>-<feature-slug>/`. Before any `$speckit-*` feature command, set `SPECIFY_FEATURE_DIRECTORY` to that directory. Do not begin feature implementation while material product or design decisions remain unresolved, unless the user explicitly authorizes named assumptions.

## Discover before asking

Inspect the repository, existing documentation, conventions, and related behavior first. Reuse facts that can be verified locally. If specifications are missing or incomplete, ask the user one structured set of questions covering every material gap; do not make the user restate discoverable information.

Cover:

- problem, users, goals, non-goals, scope, and terminology;
- functional requirements, scenarios, edge cases, and measurable acceptance criteria;
- user experience, interfaces, data, dependencies, architecture, error handling, rollout, migration, and rollback;
- security, privacy, accessibility, compatibility, performance, reliability, observability, maintainability, localization, operations, and cost;
- constraints, assumptions, risks, open questions, and approval criteria.

For every non-functional category, record a measurable target or `Not applicable` with a reason. Ask only questions that can materially change scope, behavior, design, risk, validation, or delivery.

## Required documents

Read [references/feature-documents.md](references/feature-documents.md) and create or update the canonical documents there:

1. `spec.md`
2. `plan.md`
3. `tasks.md`

`spec.md` contains requirements, acceptance criteria, and non-functional requirements. `plan.md` contains the technical design. The numbered checklist in `tasks.md` is the sole execution plan. Trace every requirement and acceptance criterion to one or more implementation or verification tasks. Include code, tests, documentation, migration, CI, rollout, and review work when applicable.

Use stable IDs such as `FR-1`, `AC-1`, `NFR-1`, and task `T001`, `T002`. Do not renumber tasks after implementation begins. Mark unresolved decisions as blockers, with an owner where known; do not hide them as assumptions.

## Readiness gate

Before handing off for implementation, verify:

- scope and non-goals are explicit;
- acceptance criteria are observable and testable;
- design choices and meaningful alternatives are recorded;
- non-functional requirements are measurable or explicitly inapplicable;
- risks, migration, rollout, and rollback are addressed when relevant;
- every specification item maps to the numbered plan;
- the user has approved the specifications or clearly authorized proceeding with documented assumptions.

Keep the documents concise and proportional to the feature, but never omit a material decision merely because the feature appears small.
