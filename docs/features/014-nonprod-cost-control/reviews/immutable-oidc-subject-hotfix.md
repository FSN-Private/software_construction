# Feature 014 immutable OIDC subject hotfix review

**Review date**: 2026-09-14

**Branch**: `fix/014-immutable-oidc-subject`

**Base**: merged `master` at `9815bbb85ba75ffa52c1e16c2981bef9e784a2d2`

**Destination**: `origin/fix/014-immutable-oidc-subject`

**Reviewed range**: `9815bbb85ba75ffa52c1e16c2981bef9e784a2d2..` plus the exact staged hotfix

**Alternate model**: GPT-5.6 Sol, high reasoning

## Live finding

Deployment run `34767713115` passed evidence and configuration validation, then AWS STS rejected all 12 `AssumeRoleWithWebIdentity` attempts. GitHub's OIDC configuration API reports `use_immutable_subject: true` with prefix `repo:FSN-Private@325881310/software_construction@1352353437`. CloudTrail recorded that same subject with `:environment:nonprod`, while the role trusted the older name-only subject.

The approved operational remediation changed only the live role's trust subject to the exact immutable repository/environment identity and retained the exact `sts.amazonaws.com` audience. Deployment retry `34795455725` subsequently passed its first OIDC authentication step.

## Reviewed push scope

- `docs/features/004-platform-foundation-ci-cd/pre-deploy-check.md`
- `docs/features/014-nonprod-cost-control/plan.md`
- `docs/features/014-nonprod-cost-control/reviews/immutable-oidc-subject-hotfix.md`
- `docs/runbooks/bootstrap-nonprod.md`
- `package.json`
- `packages/infrastructure/identity/github-oidc-trust-policy.template.json`
- `tests/infrastructure/protection-drift.mjs`

Validation passed:

- `npm run test:reliability`
- `npm run test:ci-contract`
- JSON parsing for the trust template and root package manifest
- `git diff --cached --check`

## Review rounds

### Round 1 — PASS

The reviewer found no blocking issue. The template has one `StringEquals` map containing both the exact `sts.amazonaws.com` audience and immutable owner/repository/environment subject, retains only `sts:AssumeRoleWithWebIdentity`, and rejects other identities without widening access. The parsed-JSON regression test verifies both conditions, rejects the former name-only subject, and runs in required infrastructure CI through `test:reliability`.

Documentation accurately distinguishes the completed OIDC repair from the still-running deployment. The reviewer recommended the non-blocking clarification that owner or repository renames also require re-querying the emitted subject; the bootstrap runbook now includes it.

## Final gate

**PASS** — alternate-model review completed with no unresolved blocking findings. Pushing remains separately authorized.
