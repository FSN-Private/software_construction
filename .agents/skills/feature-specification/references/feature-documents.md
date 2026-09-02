# Feature document contract

Store one feature's artifacts in `docs/features/<NNN>-<feature-slug>/`. Use a sequential three-digit ID and a lowercase hyphenated slug. Before using a Spec Kit feature command, set `SPECIFY_FEATURE_DIRECTORY` to that exact directory. Link related documents with relative links.

## `spec.md`

Use the Spec Kit specification template. Include status and owner, problem statement, users and scenarios, goals, non-goals, scope, glossary, constraints, assumptions, dependencies, functional requirements with `FR-*` IDs, acceptance criteria with `AC-*` IDs, edge cases, and open questions. Also include non-functional requirements with `NFR-*` IDs covering performance, availability, security, privacy, accessibility, compatibility, observability, maintainability, testability, localization, deployment, operations, cost, and compliance. Give targets and verification methods, or write `Not applicable — <reason>`.

## `plan.md`

Use the Spec Kit planning template. Include technical context, the constitution check, proposed approach, affected components, interfaces or APIs, data model and lifecycle, user experience, validation and error behavior, security and privacy design, compatibility, migration, rollout and rollback, alternatives, risks, and unresolved decisions. Add diagrams only when they materially clarify relationships.

## `tasks.md`

This is the sole execution source of truth. Use the Spec Kit checklist format with immutable `T001`, `T002`, and later IDs. Organize implementation by user story when applicable, and include requirement IDs, acceptance criteria, expected code, tests, documentation, validation, milestones, CI, migration, rollout, rollback, and adversarial review where relevant. Add traceability from every `FR-*`, `AC-*`, and `NFR-*` ID to a task and test or validation check.

Allowed task states are unchecked, in progress (append `— IN PROGRESS`), checked, or blocked (append `— BLOCKED: <reason>`). Update tasks in the same change as the work. Never mark a task complete until its tests, documentation, and stated validation are complete or explicitly recorded as not applicable with a reason.
