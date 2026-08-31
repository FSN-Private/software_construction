# Feature document contract

Store one feature's artifacts in `docs/features/<feature-slug>/`. Use lowercase hyphenated slugs. Link related documents with relative links.

## `requirements.md`

Include status and owner, problem statement, users and scenarios, goals, non-goals, scope, glossary, constraints, assumptions, dependencies, functional requirements with `FR-*` IDs, acceptance criteria with `AC-*` IDs, edge cases, and open questions. Acceptance criteria must describe observable outcomes rather than implementation details.

## `design.md`

Include context, proposed approach, affected components, interfaces or APIs, data model and lifecycle, user experience, validation and error behavior, security and privacy design, compatibility, migration, rollout and rollback, alternatives considered, risks, and unresolved design decisions. Add diagrams only when they materially clarify relationships.

## `non-functional.md`

Address performance and capacity, availability and recovery, security, privacy and data retention, accessibility, browser/platform compatibility, observability, maintainability, testability, localization, deployment and operations, resource or cost limits, and compliance. Give targets and verification methods. Use `Not applicable — <reason>` where appropriate.

## `implementation-plan.md`

This is the sole execution source of truth. Include:

- links to the three specification documents;
- overall status and any blocking decisions;
- a checkbox task list with immutable numeric IDs;
- requirement and acceptance-criterion IDs covered by each task;
- expected code, tests, documentation, and validation for each task;
- milestone boundaries and intended commit outcome;
- CI, migration, rollout, rollback, and adversarial-review tasks where applicable;
- a traceability table mapping every `FR-*`, `AC-*`, and `NFR-*` ID to task and test IDs.

Use task entries such as:

```markdown
- [ ] 2. Implement validation (`FR-2`, `AC-3`)
  - [ ] 2.1 Add unit tests for valid, boundary, and invalid inputs
  - [ ] 2.2 Update API and user documentation
  - Validation: `<command or observable check>`
  - Milestone: Validation behavior complete
```

Allowed task states are unchecked, in progress (append `— IN PROGRESS`), checked, or blocked (append `— BLOCKED: <reason>`). Update the plan in the same change as the work. Never mark a task complete until its tests, documentation, and stated validation are complete or explicitly recorded as not applicable with a reason.
