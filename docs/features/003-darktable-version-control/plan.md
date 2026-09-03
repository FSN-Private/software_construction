# Program Decomposition Plan: Darktable Version-Controlled Image Library

**Branch**: `feature/003-darktable-version-control` | **Date**: 2026-09-02 | **Spec**: [spec.md](spec.md)
**Status**: Approved for decomposition; not an implementation plan

## Summary

Feature 003 is the umbrella product contract. It is delivered through eight independently specified child features rather than one code change. Shared technical research is retained in [research.md](research.md); each child must confirm the relevant choices in its own `$speckit-plan` phase before implementation.

## Constitution Check

- **PASS — specification authority**: The approved umbrella remains canonical for shared outcomes; child specs refine only allocated scope.
- **PASS — feature isolation**: Every child uses `feature/<NNN>-<slug>` matching `docs/features/<NNN>-<slug>/`.
- **PASS — implementation gate**: No child implementation begins until that child's spec, plan, and numbered tasks are approved.
- **PASS — traceability**: Child specs link to this program; child plans/tasks map both local and allocated program requirement IDs.
- **PASS — coherent milestones**: Each child specification, plan, task set, and implementation slice is committed separately.
- **PASS — supply chain and deployment**: CI/CD, dependencies, releases, and recovery retain their specialized repository gates.

## Child Feature Roadmap

| Order | Feature | Depends on | Independent outcome |
|---|---|---|---|
| 1 | `004-platform-foundation-ci-cd` | 003 | Reproducible greenfield toolchain, secure validation, deployable non-production skeleton, identity and telemetry baseline |
| 2 | `005-image-ingest-storage` | 004 | Checksummed images and XMP sidecars can be imported, identified, stored, retrieved, moved, soft-deleted, and restored |
| 3 | `006-xmp-version-control-core` | 004, 005 | Atomic library-wide XMP commits, branches, tags, working state, protected history, reflog, and recovery semantics |
| 4 | `007-windows-cloud-files-sync` | 004–006 | A Windows device can browse placeholders, pin/edit offline, synchronize safely, and check out an independent branch |
| 5 | `008-darktable-rendering` | 004, 005 | Current and historical image/XMP pairs render safely with recorded darktable provenance |
| 6 | `009-gallery-metadata-search` | 004, 005, 006, 008 | Users can find images by darktable metadata and create versioned metadata changes from the portal |
| 7 | `010-lineage-merge-interface` | 006, 008, 009 | Users can inspect image lineage, compare/render revisions, merge conflicts, hide versions, and restore safely |
| 8 | `011-backup-disaster-recovery` | 004–010 | All recovery authorities are copied cross-region and an isolated regional-loss restore meets RPO/RTO |

Dependencies describe delivery order, not permission to mix child changes on one branch. Contract stubs may be created in an earlier child so later children can integrate without rewriting authority.

## Shared Architecture Direction

The research baseline proposes:

- a modular .NET backend with isolated ingest and darktable workers;
- a React/TypeScript web portal and a .NET Windows Cloud Files provider;
- PostgreSQL for transactional metadata and commit graph state;
- immutable, content-addressed image/XMP objects in S3;
- a conservative, versioned XMP merge policy;
- ECS Fargate, RDS Multi-AZ, SQS, Cognito, CloudFront, and AWS CDK;
- cross-account, cross-region cold recovery;
- GitHub Actions with OIDC, immutable build promotion, SBOMs, provenance, and protected environments.

These are proposed architecture inputs, not accepted ADRs. Each consequential choice is accepted only through the child plan/ADR that first owns it.

## Cross-Feature Contracts

- Stable asset, content-object, revision, commit, branch, device, job, and audit identifiers are established in feature 005/006 and must not be forked by later features.
- The API uses versioned contracts; Windows, gallery, rendering, and lineage consume the same canonical object and error semantics.
- All state-changing requests carry idempotency and expected-version/base identifiers.
- Original image bytes and committed XMP bytes are immutable. Derivatives are regenerable.
- Audit, telemetry, security, accessibility, privacy, cost, compatibility, CI/CD, migration, and rollback requirements apply to every child in proportion to its surface.
- Feature 011 validates recovery of the integrated outputs of every earlier child; it does not retroactively add missing recoverability.

## Integration Gates

1. Feature 004 publishes local validation commands and protected CI check names before application features add code.
2. Feature 005 proves immutable content integrity and lifecycle before feature 006 publishes commits.
3. Feature 006 freezes version/sync contracts before feature 007 implements filesystem behavior.
4. Feature 008 freezes render provenance before features 009/010 rely on previews.
5. Features 009/010 pass accessible end-to-end workflows against the real version/render contracts.
6. Feature 011 restores a representative integrated library and release artifact in isolation.
7. Program completion requires the umbrella success criteria, load targets, security review, cost review, rollback exercise, and adversarial pre-push gate.

## Rollout Strategy

Each child is independently reviewable and may stop at its validation checkpoint. Production exposure remains disabled until features 004–011 are integrated and feature 011's recovery exercise passes. Non-production deployment is permitted only when the owning child plan/tasks explicitly authorize and validate it; this roadmap itself authorizes no AWS, GitHub, or external mutation.

## Risks

- Cross-feature contract drift: mitigate with canonical versioned contracts and consumer tests.
- Foundational feature overreach: keep feature 004 limited to deployable skeleton and shared controls, not domain behavior.
- False independence: child acceptance tests use stubs only until dependency integration gates pass; program completion uses real components.
- Stacked local branches before 003 is merged: create sibling child branches from the committed 003 baseline and report divergence; do not merge/rebase silently.
- Cost discovered too late: every child adds a cost delta and feature 004 establishes budgets before non-production resources persist.

## Outputs and Next Phase

This umbrella plan produces the child specification set only. It intentionally has no `tasks.md` and no direct implementation. Each child proceeds through `$speckit-clarify`, `$speckit-plan`, `$speckit-tasks`, `$speckit-analyze`, and approval independently.
