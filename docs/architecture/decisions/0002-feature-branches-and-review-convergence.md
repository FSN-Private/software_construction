# ADR-0002: Require feature branches and convergent adversarial review

- Status: Accepted
- Date: 2026-09-02
- Decision owners: Project maintainer
- Related feature: [Feature branch and recursive review policy](../../features/002-branch-and-review-policy/spec.md)

## Context

The repository has specification-first delivery and independent alternate-model pre-push review, but feature work can still occur on the default branch and reviewers can issue feedback that is accepted or dismissed without a recorded evidence-based assessment. These gaps weaken isolation, auditability, and the usefulness of adversarial review.

## Decision

All features and material behavior changes use a branch named `feature/<NNN>-<slug>` matching the feature artifact directory. Feature work is forbidden on the default branch. Spec Kit's optional Git extension remains disabled; agents create or switch branches explicitly after confirming a clean worktree and before feature edits.

Adversarial review is a recursive convergence process. The implementing agent independently validates every finding, records evidence and a disposition, changes only valid findings, and asks the alternate reviewer to evaluate the revised diff and those dispositions. The process ends only when the reviewer explicitly agrees no valid unresolved finding remains. Lack of convergence or a decision requiring product authority blocks push and returns control to the user.

## Alternatives considered

1. Allow direct default-branch feature work for small changes. Rejected because scope classification is error-prone and weakens a clear invariant.
2. Install Spec Kit's Git extension for automatic branches. Deferred because branch automation and its interaction with existing Git policies need separate approval.
3. Require agents to implement every reviewer suggestion. Rejected because reviewer feedback can be incorrect, out of scope, or harmful; evidence-based assessment is required.
4. Use one review pass. Rejected because fixing a finding can introduce another issue and initial feedback can remain disputed.

## Consequences

Positive consequences:

- feature changes have isolated, traceable branch context;
- review records show both the reviewer perspective and the agent's evidence-based assessment;
- no push proceeds on unresolved disagreement.

Costs and risks:

- routine work requires branch setup and branch/base checks;
- recursive review can take additional time and may require user input when a disagreement cannot be resolved;
- generated Spec Kit commands must obey repository routing rather than their default feature-directory behavior alone.

## Safety and exit

The policy never authorizes merge, rebase, force-push, or remote mutation. If this process later proves unsuitable, supersede this ADR and update the affected skills, constitution, and documentation in one reviewed feature change.
