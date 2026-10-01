# ADR-0013: Temporary patched CDK bundle

**Date:** 2026-10-01
**Status:** Accepted by project owner on 2026-10-01
**Decision owner:** Project owner
**Feature:** [015 gallery-first frontend](../../features/015-gallery-first-frontend/spec.md)
**Delivery:** [PR #30](https://github.com/FSN-Private/software_construction/pull/30), tasks T021/T022

## Context

PR CI rejects high-severity denial-of-service vulnerabilities in `brace-expansion` 5.0.9 bundled inside pinned `aws-cdk-lib` 2.267.0. The latest published CDK 2.272.0 still bundles 5.0.9. An isolated npm 11.19.0 override experiment confirmed that installing with an override to 5.0.12 still installs 5.0.9. Waiting for an upstream fixed release leaves required CI red.

## Decision

Temporarily store a patched CDK archive under `packages/infrastructure/vendor/` and resolve `aws-cdk-lib` through that local archive in the npm manifest and lockfile. Rebuild it from the official npm CDK 2.267.0 and `brace-expansion` 5.0.12 archives after verifying their registry SHA-512 integrity. Replace only the bundled `package/node_modules/brace-expansion/` subtree. Every other CDK file must match the upstream archive byte for byte; replacement files must match the official patched package. Keep package versions and licenses intact, identify the patched distribution in its filename and provenance record, and commit its own lockfile integrity.

Use a deterministic, standard-library rebuild procedure with no third-party install scripts. Before adoption, verify clean `npm ci --ignore-scripts`, actual installed version, audit, infrastructure validation and Trivy CI. Require independent pre-push review. Do not suppress vulnerabilities or change scanner severity.

## Candidate evidence

The isolated candidate is `artifacts/dependency-probe/aws-cdk-lib-2.267.0-brace-expansion-5.0.12.tgz` (36,035,775 bytes). SHA-512: `fc3cdba3244ef7bf4dd98236d79c9a37dbd8a4e0e370f86c585ab5ed120aa10386ef44cdbf7f16d1f56a2632a94ea5079d0d65637a0b7a10007594f65e7ec056`.

On 2026-10-01, isolated installation reported zero npm vulnerabilities and the actual nested manifest reported 5.0.12. Type checking, offline synthesis, template assertions, secret scanning, dependency-tree validation, license validation and high-severity npm audit passed. SHA-256 comparison of every archive file proves that only the targeted subtree differs and that its replacement matches the verified npm release exactly. The candidate was tested in ignored experiment output before changing real dependency files. After approval, the same archive is adopted with lockfile integrity and a normal-validation guard that checks archive, lockfile and installed contents. Clean installation and Trivy confirmation are required for T022 completion.

## Alternatives and tradeoffs

| Option | Benefit | Cost |
|---|---|---|
| Temporary patched archive (accepted) | Removes the vulnerable installed code; no scanner exception | Adds about 36 MB to Git and makes this project responsible for temporary package provenance and refreshes |
| Wait for upstream release | Keeps distribution ownership entirely upstream | Required CI and merge remain blocked for an unknown duration |
| Consumer override | Small configuration change | Experiment shows the vulnerable bundled copy remains; ineffective |
| Scanner exception | Avoids archive maintenance | Leaves vulnerable code installed and weakens required checks; not proposed |

## Consequences, operations and exit

The infrastructure build becomes dependent on a repository-maintained distribution. Preserve Apache-2.0/MIT notices, upstream source identities, checksums, the exact rebuild command and content verification. The archive affects synthesis tooling; no application API, schema, deployment state or CloudFormation behavior is intentionally changed. No deployment is part of candidate validation.

Remove the local archive, rebuild procedure and patch provenance when an upstream CDK release bundles a patched `brace-expansion`, after verifying installed contents and all required checks. Rollback to the original lockfile reintroduces the vulnerability and failing CI, so it cannot be presented as a security fix. The project owner explicitly approved the temporary patched archive and another CI run on 2026-10-01.
