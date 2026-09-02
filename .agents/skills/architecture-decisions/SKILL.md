---
name: architecture-decisions
description: Create and maintain architecture decision records for consequential, durable technical choices and their tradeoffs. Use when a design changes system boundaries, data ownership, interfaces, infrastructure, security posture, or long-term constraints.
---

# Architecture Decisions

Record decisions whose rationale future maintainers would otherwise need to rediscover. Do not create an ADR for routine implementation details that are adequately explained in the feature design.

## Decide whether an ADR is warranted

Use an ADR when the choice is costly to reverse, spans features or components, establishes a durable constraint, changes a public contract or trust boundary, selects foundational technology, or deliberately accepts significant risk or debt.

Inspect `docs/architecture/decisions/` for the repository's format and highest existing number. Create `NNNN-<short-title>.md` using the next unused number; never renumber existing ADRs.

## ADR content

Include:

- title, date, status, decision owners, and links to feature specs, issues, and superseded ADRs;
- context, decision drivers, constraints, and the exact decision being made;
- viable options considered, including “do nothing” where meaningful;
- evidence and tradeoffs for each option;
- positive and negative consequences, risks, security/privacy impact, operational impact, migration, rollback or exit strategy, and follow-up tasks.

Use statuses `Proposed`, `Accepted`, `Deprecated`, or `Superseded by ADR-NNNN`. Preserve history: supersede an accepted decision with a new ADR rather than rewriting it to imply the new choice was always intended.

An ADR may remain `Proposed` while approval is pending. Do not mark it `Accepted` without the user or repository's designated decision authority. Link the accepted ADR from the feature's `plan.md` and map resulting work into the numbered implementation plan.
