---
name: github-work-traceability
description: Keep feature specifications, numbered tasks, commits, GitHub issues, pull requests, review findings, and releases traceably linked. Use when preparing or updating GitHub work items or delivery metadata.
---

# GitHub Work Traceability

Make GitHub work items a navigable view of repository documentation, not a second conflicting source of truth. The approved files under `docs/features/<NNN>-<feature-slug>/` and the numbered implementation plan remain authoritative. Feature delivery occurs on the matching `feature/<NNN>-<feature-slug>` branch, never the default branch.

## Establish links

For a feature, maintain this chain where the repository uses the corresponding artifact:

`feature docs → numbered tasks → branch/commits → issue → pull request → review → release`

- Record the exact `feature/<NNN>-<feature-slug>` branch in the plan and PR when one exists.
- Put the feature-doc path and relevant requirement/task IDs in the issue or PR.
- Reference the issue in the implementation plan.
- Use commit messages or bodies to identify the outcome and related issue/task when that adds useful traceability.
- In the PR body, include purpose, linked issue, specification path, completed task range, validation evidence, risk, rollout/rollback notes, and documentation changes.
- Link review findings and release records back to the PR and feature documents.

Use GitHub closing keywords such as `Closes #123` only when merging to the default branch should actually close that issue. Otherwise use a non-closing reference. Verify current behavior in GitHub's [official issue-linking documentation](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/linking-a-pull-request-to-an-issue).

## Preserve authority and safety

Do not copy entire specifications into an issue or PR. Summarize and link them so later changes have one authoritative location. Flag mismatches instead of silently choosing one version.

Creating or editing issues, PRs, labels, comments, milestones, or projects is an external write and requires explicit user authorization for the exact operation. Drafting content locally is allowed. Never post secrets, private logs, credentials, or sensitive vulnerability details to public GitHub surfaces.

When review comments need action, use `gh-address-comments`. It owns thread discovery and confirmation of which comments to address. Before merging the PR, the repository's adversarial pre-merge gate applies; ordinary pushes and opening a PR do not require it.
