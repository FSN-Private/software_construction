---
name: feature-specification
description: Define or complete requirements, design, and non-functional specifications for a proposed feature before implementation. Use for feature requests, material behavior changes, or when an implementation plan lacks approved specifications.
---

# Feature Specification

Produce an implementation-ready specification set under `docs/features/<feature-slug>/`. Do not begin feature implementation while material product or design decisions remain unresolved, unless the user explicitly authorizes named assumptions.

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

Read [references/feature-documents.md](references/feature-documents.md) and create or update all four documents there:

1. `requirements.md`
2. `design.md`
3. `non-functional.md`
4. `implementation-plan.md`

The numbered task list in `implementation-plan.md` is the sole execution plan. Trace every requirement and acceptance criterion to one or more implementation or verification tasks. Include code, tests, documentation, migration, CI, rollout, and review work when applicable.

Use stable IDs such as `FR-1`, `AC-1`, `NFR-1`, and task `1`, `1.1`, `2`. Do not renumber tasks after implementation begins. Mark unresolved decisions as blockers, with an owner where known; do not hide them as assumptions.

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
