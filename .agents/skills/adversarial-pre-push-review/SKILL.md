---
name: adversarial-pre-push-review
description: Gate a proposed push through a focused independent alternate-model review for significant blocking issues.
---

# Adversarial Pre-push Review

Do not push local commits until this gate reports `PASS` with no unresolved blocking findings. This includes feature, maintenance, documentation, policy, agent-instruction, skill, CI, and configuration changes. The reviewer must use a genuinely different available LLM model from the implementing model. A fresh prompt, context, or agent using the same underlying model does not qualify unless the user explicitly accepts that limitation.

The reviewer pass is read-only. It does not authorize a push, merge, remote mutation, history rewrite, or unrelated change. The implementing agent may remediate supported, in-scope blocking findings after assessing them. If an alternate model is unavailable, stop before pushing and ask the user how to proceed; never self-certify the gate.

## Do not confuse a blocked gate with a stopped feature

`BLOCKED` means the proposed push cannot proceed in its current state. It is not a terminal result for an active request to complete the feature or prepare it for push. For each valid, in-scope blocking finding in that situation, the implementing agent must:

1. map the finding to its existing numbered task, or append a traceable remediation task when the approved plan has a genuine gap;
2. implement the smallest supported remediation, update required documentation and task state, and run relevant validation;
3. rerun independent review with the complete revised range and all prior blocking dispositions; and
4. repeat until the reviewer explicitly reports no valid unresolved blocking finding and `PASS`.

Use brief progress updates while this loop runs. Do not give the user a final handoff merely because a review round found defects, a first fix was partial, or another round is required. For a review-only request, record findings and stop without changing implementation unless the user separately asks to fix them.

Stop and ask the user only when a blocking finding needs a material product or design choice, unavailable required credentials or infrastructure, or authority outside the approved feature scope. A routine code, test, documentation, configuration, or validation fix within approved scope must be completed without a new approval.

## Blocking threshold

Focus the review on significant issues that could make pushing unsafe or materially violate approved requirements. A finding is blocking only when supported evidence shows a credible risk such as a security or privacy vulnerability, data loss or corruption, broken authorization, a material functional or compatibility regression, a failed required acceptance criterion, an unsafe migration or release path, or a failure that materially affects reliability, availability, or compliance.

Do not block a push for formatting, naming, stylistic preferences, minor documentation polish, speculative improvements, or narrow edge cases with negligible impact. Record these as non-blocking notes. They may be fixed voluntarily or planned later, but require neither remediation nor user waiver for `PASS`.

Spend review effort first on the blocking threshold. Mention non-blocking observations concisely; do not expand them into exhaustive style or edge-case review.

## Prepare an independent review

Give the reviewer only the evidence needed to assess the work:

- approved files under `docs/features/<NNN>-<feature-slug>/`, or the governing request and repository policy for non-feature work;
- the complete intended commit range and diff against its base;
- relevant source, tests, configuration, migrations, and generated artifacts;
- exact validation commands and their latest results;
- repository conventions and runtime constraints.

Do not prime the reviewer with desired conclusions or suspected findings. Ask it to attempt to identify significant blocking issues by checking:

- every requirement, acceptance criterion, NFR, and numbered task against the diff;
- functional bugs, boundary cases, failure paths, concurrency, state, and data integrity;
- security, privacy, authorization, secrets, dependency, and supply-chain risks;
- compatibility, accessibility, performance, reliability, observability, rollout, migration, and rollback;
- weak, missing, misleading, or non-deterministic tests;
- stale or inaccurate documentation and CI gaps;
- unrelated changes, accidental files, and commit-history clarity.

Require findings to include a `blocking` or `non-blocking` classification, severity, evidence, affected specification/task IDs, impact, and a concrete remediation or verification suggestion. Require an explicit statement when no blocking issue is found in a review area.

## Independently assess every finding

The implementing agent must not implement feedback merely to satisfy the reviewer. For every finding, independently compare the claim with the approved specifications, code, tests, diff, repository policy, and reproducible validation evidence. Record one of these dispositions:

- `valid and fixed`: a blocking claim is supported, the fix is identified, and validation proves the fix;
- `invalid with evidence`: a blocking claim is unsupported or inapplicable, with concise evidence that the reviewer can evaluate;
- `needs user decision`: a blocking claim exposes an unresolved product, risk, or authority choice;
- `non-blocking note`: the finding is recorded for optional follow-up and does not affect the push gate.

Do not close a blocking finding merely because the agent disagrees. Blocking findings must be fixed or shown invalid with evidence; they cannot be waived. Do not require fixes, a task ID, or a waiver for non-blocking notes. Update specifications and the numbered plan if a blocking finding exposes a scope or design gap.

## Review convergence loop

1. Obtain the initial independent review and record every finding, classified as blocking or non-blocking.
2. Assess each blocking finding using the required disposition. Make only justified fixes or prepare reproducible invalidity evidence; record non-blocking notes without remediation.
3. Rerun all relevant validation after substantive changes.
4. Give the alternate reviewer the revised complete diff, latest validation results, and every prior blocking finding with its disposition, evidence, and fix. Ask the reviewer to explicitly assess each blocking disposition and identify any new blocking issue.
5. If the reviewer disputes a blocking disposition or reports a valid unresolved blocking finding, repeat from step 2. Record each round; do not replace earlier findings or evidence.
6. Finish when the reviewer explicitly agrees that no valid unresolved blocking finding remains. Non-blocking notes may remain open.

There is no arbitrary maximum number of review rounds for blocking findings. If the loop cannot converge because the reviewer is unavailable, evidence cannot be reproduced after reasonable in-scope investigation, or a blocking finding needs user authority, stop without pushing and ask the user for direction. Otherwise, continue remediation and review; never stop solely because a blocking finding is unresolved.

## Resolve and record

Save a feature review to `docs/features/<NNN>-<feature-slug>/reviews/pre-push-review.md`. For a push without a feature slug, save it to `docs/reviews/<date>-<change-slug>-pre-push.md`. Preserve every review round and include:

- alternate model identifier, review date, remote and destination ref, base, reviewed commit range, and exact pending push scope;
- reviewer prompt scope, feedback, severity, evidence, affected requirement/task IDs, impact, and suggested remediation for each finding;
- the implementing agent's validity assessment, disposition, and supporting evidence for each finding;
- fixes, plan or specification updates, non-blocking notes, and validation commands with results;
- the reviewer's explicit assessment of each disposition in each follow-up round;
- the final explicit reviewer agreement and final gate result (`PASS` or `BLOCKED`).

Commit review fixes and the final review record as a dedicated milestone. The reviewer must inspect the pending review record before final agreement; after that agreement, commit it unchanged. Before pushing, verify the working tree is clean, the recorded commit range matches the proposed push, required checks pass, and the final review says `PASS` with no unresolved blocking findings.

Push only with explicit user authorization. Merge only with separate explicit user authorization.
