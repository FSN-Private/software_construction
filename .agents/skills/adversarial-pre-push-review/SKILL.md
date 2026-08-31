---
name: adversarial-pre-push-review
description: Gate any proposed push through an independent alternate-model review of its governing specifications or intent, diff, tests, documentation, and operational risks. Use whenever local commits are ready to be pushed.
---

# Adversarial Pre-push Review

Do not push changes until this gate passes. This includes feature, maintenance, documentation, policy, agent-instruction, skill, CI, and configuration changes. The reviewer must use a genuinely different available LLM model from the implementing model. A fresh prompt, context, or agent using the same underlying model does not qualify unless the user explicitly accepts that limitation.

The review is read-only. It does not authorize a push, remote mutation, history rewrite, or unrelated change. If an alternate model is unavailable, stop before push and ask the user how to proceed; never self-certify the gate.

## Prepare an independent review

Give the reviewer only the evidence needed to assess the work:

- approved files under `docs/features/<feature-slug>/`, or the governing request and repository policy for non-feature work;
- the complete intended commit range and diff against its base;
- relevant source, tests, configuration, migrations, and generated artifacts;
- exact validation commands and their latest results;
- repository conventions and runtime constraints.

Do not prime the reviewer with desired conclusions or suspected findings. Ask it to attempt to disprove completeness and correctness by checking:

- every requirement, acceptance criterion, NFR, and numbered task against the diff;
- functional bugs, boundary cases, failure paths, concurrency, state, and data integrity;
- security, privacy, authorization, secrets, dependency, and supply-chain risks;
- compatibility, accessibility, performance, reliability, observability, rollout, migration, and rollback;
- weak, missing, misleading, or non-deterministic tests;
- stale or inaccurate documentation and CI gaps;
- unrelated changes, accidental files, and commit-history clarity.

Require findings to include severity, evidence, affected specification/task IDs, impact, and a concrete remediation or verification suggestion. Require an explicit statement when no issue is found in a review area.

## Resolve and record

Save a feature review to `docs/features/<feature-slug>/reviews/pre-push-review.md`. For a push without a feature slug, save it to `docs/reviews/<date>-<change-slug>.md`. Record the alternate model identifier, remote and destination ref, reviewed commit range, review date, validation evidence, each finding, its resolution or user-approved waiver, and the final gate result.

Resolve all critical and high findings. Resolve medium findings unless the user explicitly accepts and documents the risk. Record low findings as fixed, planned with a task ID, or accepted with rationale. Update specifications and the numbered plan if a finding exposes a scope or design gap.

After substantive fixes, rerun relevant validation and ask the alternate model to review the revised diff. Commit review fixes and the final review record as a dedicated milestone. Before push, verify the working tree is clean, the recorded commit range matches what will be pushed, required checks pass, and the final review says `PASS` with no unresolved blocking findings.

Push only with explicit user authorization.
