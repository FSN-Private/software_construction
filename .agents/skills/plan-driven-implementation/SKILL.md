---
name: plan-driven-implementation
description: Implement approved feature specifications through their numbered plan, keeping code, tests, documentation, plan status, and milestone commits synchronized. Use for feature development or material behavior changes after specification.
---

# Plan-driven Implementation

Use `docs/features/<NNN>-<feature-slug>/tasks.md` as the sole execution source of truth. Read the linked `spec.md` and `plan.md` before editing code. If they are absent, incomplete, contradictory, or unapproved, stop implementation and use the `feature-specification` skill.

## Execute the plan

1. Confirm the working tree is clean before selecting a task. If unrelated or unapproved changes are present, stop before feature edits and resolve scope with the user. Require the matching `feature/<NNN>-<feature-slug>` branch before selecting a task; feature implementation on the default branch is forbidden. Verify and report divergence from the default branch, but never silently merge, rebase, or rewrite history to reconcile it. Never include unrelated user work in a task or commit.
2. Select the next unblocked `T###` task. Mark it in progress before or with the first implementation change.
3. Implement the smallest coherent slice that satisfies the mapped specification IDs.
4. Add or update unit tests in the same slice whenever logic can be tested in isolation. If unit tests are not applicable, record the concrete reason in the plan and add the most appropriate higher-level verification.
5. Update all affected documentation in the same slice. Consider user guidance, interfaces, architecture, operations, migration, examples, and changelog material; update only what the feature affects.
6. Run task-level validation, then mark the task complete and update traceability. A task is incomplete if code, tests, documentation, or validation required by it remains outstanding.

Do not silently depart from the specifications. Record a discovered conflict or scope change as a blocked plan item, update the relevant specification, and obtain user approval when the change is material.

## Milestone commits

Treat each milestone named in the plan as a required Git checkpoint. A milestone may contain multiple atomic commits, but it must have at least one coherent commit and must not be combined with an unrelated milestone merely for convenience.

Before each milestone commit:

- run the milestone's relevant tests, lint, type checks, and build;
- update specifications, feature documentation, plan state, and traceability;
- inspect the diff and staged files for unrelated changes, secrets, generated noise, and debugging artifacts;
- use a concise commit subject that states the outcome; Conventional Commit style is preferred when the repository has no stronger convention.

Do not create empty commits. If Git is unavailable, identity is not configured, required validation fails, or a safe commit cannot be isolated, report the blocker and do not pretend the milestone was committed. Never amend, squash, rebase, force-push, or rewrite user history without explicit authorization.

## Completion gate

A feature is complete only when every planned item is complete or explicitly waived by the user, acceptance criteria and non-functional checks pass, documentation matches behavior, CI-relevant checks pass locally where possible, milestones are committed, and the plan's traceability has no gaps.

Before any push, use the `adversarial-pre-push-review` skill. A request to implement a feature does not itself authorize pushing; push only when the user explicitly requests it and the review gate passes.
