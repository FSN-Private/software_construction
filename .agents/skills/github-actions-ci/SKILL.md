---
name: github-actions-ci
description: Design, create, or revise secure GitHub Actions continuous-integration workflows and their required checks. Use for CI architecture and workflow-file changes; use gh-fix-ci instead when diagnosing an already failing pull-request check.
---

# GitHub Actions CI

Build CI from the repository's approved specifications and numbered implementation plan. Inspect the actual toolchain, lockfiles, supported platforms, and local validation commands before writing `.github/workflows/*.yml`. Do not invent commands or version matrices.

## Define the contract

Record in the feature plan:

- events, branches, and path filters that should trigger each workflow;
- stable job/check names expected by branch protection;
- supported runtime and operating-system matrix;
- required build, lint, type, unit, integration, security, and packaging checks;
- expected duration, artifact outputs and retention, failure ownership, and retry policy;
- which checks are required versus advisory.

Keep CI behavior reproducible locally where practical. Prefer repository scripts over duplicated shell logic in workflow YAML.

## Design securely

- Declare the least `GITHUB_TOKEN` permissions, narrowing further per job when useful.
- Treat event payload, branch names, PR titles, issue bodies, and other contributor-controlled context as untrusted input. Do not interpolate it into executable shell code.
- Avoid `pull_request_target` with untrusted code checkout. If it is truly required, document the threat model and isolate privileged steps.
- Pin third-party actions to reviewed full commit SHAs and leave a version comment for maintainability. First-party policy may follow a stricter repository rule.
- Never print secrets or persist credentials unnecessarily. Prefer GitHub OIDC over long-lived cloud credentials when supported.
- Do not cache secrets, credentials, or untrusted build outputs. Scope caches and artifacts deliberately.
- Add timeouts and concurrency/cancellation where they prevent stuck or obsolete work without hiding required results.

Verify version-sensitive syntax against the current [official workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax) and [GitHub Actions security guidance](https://docs.github.com/en/code-security/tutorials/secure-your-organization/protect-against-threats).

## Verify changes

Review YAML parsing, expressions, event semantics, permissions, matrix expansion, dependency caching, fork behavior, and required-check names. Run the exact underlying commands locally. Use a workflow linter when already available or justified by the plan; do not add a dependency solely to claim validation.

Document validation and update the numbered task in the same commit. A local workflow edit is not proven by syntax alone: when a remote run is needed, state that clearly and do not claim it passed before evidence exists.

For failures from an existing GitHub Actions run or PR check, route to `gh-fix-ci`. That official skill owns remote log inspection and failure diagnosis.
