---
name: release-operations
description: Plan and verify releases, deployments, data migrations, rollbacks, and production incident follow-up. Use when a change will be published, deployed, migrate persistent state, or alter operational recovery.
---

# Release Operations

Turn approved feature and non-functional requirements into a reversible release procedure. Planning and local edits do not authorize publishing, deployment, environment changes, migrations, or incident communications; obtain explicit approval immediately before each external mutation.

## Release plan

Create `docs/releases/<version-or-date>/release-plan.md` for a release that needs coordination. Link included feature documents and record:

- version, owner, intended commit/tag, included changes, artifacts, and environments;
- prerequisites, configuration and secret changes, compatibility constraints, and dependency state;
- database or state migration steps, backups, rehearsal evidence, duration, locking, and backward compatibility;
- staged rollout order, approvals, feature flags, monitoring, success criteria, and observation windows;
- explicit abort thresholds, rollback decision owner, rollback steps, and data-reconciliation limits;
- smoke tests, post-release validation, user/support communication, and cleanup tasks.

Every step must have an observable result. Replace vague instructions such as “monitor closely” with named signals, thresholds, duration, and ownership. Never describe a rollback as available if an irreversible schema or data transformation makes it incomplete.

## Execute safely

Verify that the intended commit passed required CI and, when it is merged through a pull request, the alternate-model pre-merge review. Ensure artifacts are reproducible and attributable, required backups exist, and the rollback has been rehearsed proportionally to risk. Use least-privilege credentials and avoid exposing secrets in logs.

Pause on failed gates or unexpected state. Do not improvise destructive recovery. Record actual timestamps, operators, evidence, deviations, and final outcome in the release plan.

## Incidents

For a production incident, prioritize containment and recovery within the user's authorized scope. Preserve evidence and create `docs/incidents/<date>-<slug>.md` for material incidents, covering impact, detection, timeline, contributing conditions, response, recovery, and follow-up tasks. Use blameless, evidence-based language. Separate confirmed facts from hypotheses, and link corrective actions to numbered plans and owners.
