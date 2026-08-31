---
name: dependency-supply-chain-security
description: Assess and reduce dependency, build, package, and software supply-chain risk. Use when adding or updating dependencies, lockfiles, package sources, third-party actions, build plugins, release artifacts, or security automation.
---

# Dependency and Supply-chain Security

Treat every new executable dependency, build plugin, GitHub Action, package source, and release tool as code entering the trust boundary. Keep the assessment proportional to its privilege and production reach.

## Review the change

1. Establish why the dependency is needed and whether existing code or an existing dependency already provides the capability.
2. Verify the exact package identity and authoritative source to reduce typo-squatting risk.
3. Review version, maintenance activity, ownership, provenance, license compatibility, known advisories, transitive impact, install/build scripts, and requested permissions.
4. Require the ecosystem's lockfile or integrity mechanism and inspect lockfile diffs for unexpected sources, versions, scripts, or dependency growth.
5. Prefer the narrowest maintained dependency and remove unused packages. Do not bundle unrelated upgrades into the change.
6. For GitHub Actions, pin third-party actions to reviewed full commit SHAs and assess token, secret, and runner exposure.
7. Run relevant tests, build, package-manager audit, license or policy checks, and artifact verification supported by the repository.

Do not automatically execute untrusted package install scripts merely to inspect a dependency. Use read-only metadata and source review first, then obtain approval when installation or network access changes the risk materially.

## Record and gate

Put dependency rationale, accepted risks, applicable `NFR-*` IDs, validation evidence, and update/removal strategy in the feature documentation and numbered plan. For deployable artifacts, consider generating or validating an SBOM and provenance attestation when the ecosystem supports them.

Critical or high-severity exploitable findings block completion unless resolved. A user may explicitly accept a lower-severity risk only when its impact, compensating controls, owner, and review date are documented. Never expose a non-public vulnerability in a public issue or PR without approval.

When appropriate, propose Dependabot, dependency review, secret scanning, or CodeQL as separate planned controls. Do not enable paid features, organization policies, or noisy automation without user approval and a documented rollout.
