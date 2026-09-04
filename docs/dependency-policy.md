# Dependency and action policy

## Scope and ownership

Packages, base images, build tools, GitHub Actions, and CDK constructs are executable supply-chain inputs. The change author owns initial review; the project maintainer approves additions and material upgrades. The applicable feature records the rationale, exact identity, source, license, advisory state, permissions, installation behavior, transitive impact, and validation evidence in `docs/dependency-inventory.md`.

## Deterministic inputs

- Commit ecosystem lockfiles. Do not hand-edit them except through the ecosystem-supported mechanism.
- NuGet uses `packages.lock.json` in locked mode; npm uses a committed `package-lock.json` and `npm ci`; container base images use immutable digests.
- GitHub Actions use reviewed full commit SHAs with a readable version comment. Do not use mutable tags for third-party actions.
- Package registries must be the ecosystem default or a documented, approved private registry. New registries, install hooks, unsigned tools, or unpinned images require explicit review before installation.

## Review and release gates

Before merging a dependency change, verify package identity and authoritative source, maintenance/ownership, license compatibility, advisories, install scripts, permissions, and unexpected transitive growth. Run the applicable lockfile, build, test, vulnerability, secret, license, and provenance checks. Critical or high exploitable findings block release; a lower-severity exception requires documented owner, compensating controls, review date, and approval.

The current CDK transitive closure permits `BlueOak-1.0.0` for `minimatch`, provided release notices include the license text or link, and `(MIT OR GPL-3.0-or-later)` for `case`, selecting its MIT alternative. These are reviewed permissive-license exceptions; the full release inventory remains required by T029.

Release manifests must identify the source revision, artifact checksums, SBOM, provenance, dependency/security evidence, and unresolved accepted risks. Mutable or unverifiable inputs block promotion.

## Update and removal

Keep upgrades narrow and reviewable. Remove unused dependencies in the same change when safe. Revalidate action SHAs, package patches, base-image digests, and advisories before external delivery or deployment, as tracked by feature tasks.
