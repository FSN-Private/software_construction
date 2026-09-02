---
name: adversarial-pre-push-review
description: Gate any proposed push through a convergent independent alternate-model review of its governing specifications or intent, diff, tests, documentation, and operational risks. Use whenever local commits are ready to be pushed.
---

# Adversarial Pre-push Review

Do not push changes until this gate converges with a final `PASS`. This includes feature, maintenance, documentation, policy, agent-instruction, skill, CI, and configuration changes. The reviewer must use a genuinely different available LLM model from the implementing model. A fresh prompt, context, or agent using the same underlying model does not qualify unless the user explicitly accepts that limitation.

The review is read-only. It does not authorize a push, remote mutation, history rewrite, or unrelated change. If an alternate model is unavailable, stop before push and ask the user how to proceed; never self-certify the gate.

## Prepare an independent review

Give the reviewer only the evidence needed to assess the work:

- approved files under `docs/features/<NNN>-<feature-slug>/`, or the governing request and repository policy for non-feature work;
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

## Independently assess every finding

The implementing agent must not implement feedback merely to satisfy the reviewer. For every finding, independently compare the claim with the approved specifications, code, tests, diff, repository policy, and reproducible validation evidence. Record one of these dispositions:

- `valid and fixed`: the claim is supported, the fix is identified, and validation proves the fix;
- `valid and user-waived`: the claim is supported, policy permits a waiver, and the user explicitly accepted the documented risk;
- `invalid with evidence`: the claim is unsupported or inapplicable, with concise evidence that the reviewer can evaluate;
- `needs user decision`: the claim exposes an unresolved product, risk, or authority choice.

Do not close a finding merely because the agent disagrees. Critical and high findings cannot be waived. Medium findings require resolution unless the user explicitly accepts and documents the risk. Low findings must be fixed, planned with a task ID, or explicitly accepted with rationale. Update specifications and the numbered plan if a finding exposes a scope or design gap.

## Review convergence loop

1. Obtain the initial independent review and record every finding.
2. Assess each finding using the required disposition. Make only justified fixes, obtain any permitted waiver, or prepare reproducible invalidity evidence.
3. Rerun all relevant validation after substantive changes.
4. Give the alternate reviewer the revised complete diff, latest validation results, and every prior finding with its disposition, evidence, and fix or waiver. Ask the reviewer to explicitly assess each disposition and identify any new issue.
5. If the reviewer disputes a disposition, reports a valid unresolved finding, or identifies a new finding, repeat from step 2. Record each round; do not replace earlier findings or evidence.
6. Finish only when the reviewer explicitly agrees that every finding is resolved, invalid with sufficient evidence, or validly user-waived, and states that no valid unresolved finding remains.

There is no arbitrary maximum number of review rounds. If the loop cannot converge, the reviewer is unavailable, evidence cannot be reproduced, or a finding needs user authority, stop without pushing and ask the user for direction.

## Resolve and record

Save a feature review to `docs/features/<NNN>-<feature-slug>/reviews/pre-push-review.md`. For a push without a feature slug, save it to `docs/reviews/<date>-<change-slug>.md`. Preserve every review round and include:

- alternate model identifier, review date, remote and destination ref, base, reviewed commit range, and exact pending push scope;
- reviewer prompt scope, feedback, severity, evidence, affected requirement/task IDs, impact, and suggested remediation for each finding;
- the implementing agent's validity assessment, disposition, and supporting evidence for each finding;
- fixes, user waivers, plan or specification updates, and validation commands with results;
- the reviewer's explicit assessment of each disposition in each follow-up round;
- the final explicit reviewer agreement and final gate result (`PASS` or `BLOCKED`).

Commit review fixes and the final review record as a dedicated milestone. The reviewer must inspect the pending review record before final agreement; after that agreement, commit it unchanged. Before push, verify the working tree is clean, the recorded commit range matches what will be pushed, required checks pass, and the final review says `PASS` with no unresolved blocking findings.

Push only with explicit user authorization.
