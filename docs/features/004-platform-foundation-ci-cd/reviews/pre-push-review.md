# Adversarial pre-push review - Trivy required security gate

**Status**: PASS
**Review date**: 2026-09-04
**Alternate reviewer model**: GPT-5 Codex model-switch review, independent of the implementation model used before handoff
**Remote/destination**: `origin/feature/004-platform-foundation-ci-cd`
**Base**: `origin/feature/004-platform-foundation-ci-cd` (`4d4366a`)
**Reviewed pending scope**: working-tree changes replacing the required PR npm audit with pinned Trivy vulnerability scanning in `.github/workflows/ci.yml`, adding `.github/workflows/dependency-audit.yml` as a scheduled/manual npm audit diagnostic, and updating dependency policy, Feature 004 plan/tasks, and CI contract coverage. No push occurs until final PASS.

## Reviewer prompt scope

The review covered repository instructions, Feature 004 plan/tasks, dependency policy, the full pending diff including the untracked diagnostic workflow, CI contract tests, and validation evidence. It specifically checked the change against FR-001/002/003/015, AC-001/008, NFR-001/003/009/015, and T035 after repeated npm advisory endpoint failures in GitHub Actions run `33869612368`.

## Round 1

No valid unresolved finding was identified. The required security check remains read-only, uses no secrets, does not use `pull_request_target`, and is pinned to `aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25` with version comment `v0.36.0`. The workflow configures filesystem vulnerability scanning, high/critical severity failure, exit code `1`, Trivy `v0.70.0`, and disabled pull-request cache. The full npm audit is retained as a scheduled/manual diagnostic using the existing bounded retry wrapper with `--package-lock-only`, so npm advisory endpoint instability remains visible without blocking unrelated PR merges.

### Residual risk

A live Trivy scan was not run locally because the action and vulnerability database are GitHub Actions/runtime concerns. This is acceptable for this push gate because `npm run test:ci-contract` verifies the workflow contract locally, and remote PR CI revalidation is explicitly required after the authorized push.

## Validation

- `npm run test:ci-contract` - passed.
- `node tests/unit/audit-dependencies.mjs` - passed.
- `git diff --check` - passed with only CRLF conversion warnings.

## Final agreement

The independent review confirms that no valid unresolved finding remains for the pending Trivy security-gate scope, with remote CI revalidation required after push.

**Final gate result: PASS**

---

# Adversarial pre-push review — locked packages and immutable release path

**Status**: Round 1 findings fixed; awaiting independent Round 2
**Review date**: 2026-09-05
**Alternate reviewer model**: gpt-5.5
**Remote/destination**: `origin/feature/004-platform-foundation-ci-cd`
**Base**: `origin/feature/004-platform-foundation-ci-cd` (`937050c`)
**Reviewed pending scope**: `origin/feature/004-platform-foundation-ci-cd..HEAD` (`dd624d1..329dfd1`) and the follow-up corrections below. No push is authorized by this review.

## Reviewer prompt scope

The reviewer received the approved Feature 004 specification, plan, numbered tasks, repository instructions, complete pending diff, release tooling/workflows, package definitions, runbook, tests, and the latest local validation results. It was instructed to attempt to disprove correctness, release safety, supply-chain controls, rollback behavior, test adequacy, and documentation accuracy without editing or performing external mutations.

## Round 1 — independent findings and implementer dispositions

| ID | Severity | Evidence, affected IDs, impact | Disposition and corrective evidence |
|---|---|---|---|
| R1-1 | High | `build-release.yml` allowed manual dispatch and `generate-release-evidence.mjs` could label build-workflow context as successful CI. FR-005/006/015, AC-008, T017/T021. Unvalidated source could gain self-certified release evidence. | **Valid and fixed.** Removed manual dispatch entirely; only a successful `Continuous integration` `workflow_run` on `master` can build evidence, and every checkout/tag/evidence source uses that event's `head_sha` and run ID. Contract test now rejects `workflow_dispatch` and requires the CI trigger/source binding. |
| R1-2 | High | `release.yml` did not require matching successful non-production evidence before production approval. FR-006/007/008, AC-003, T018/T019/T021. A tagged artifact could bypass non-production deployment, smoke, and digest comparison. | **Valid and fixed.** A non-privileged `preflight` resolves successful build and non-production runs for the exact tag SHA, downloads both retained evidence sets, and uses `verify-promotion-evidence.mjs` to verify source SHA, build-run ID, manifest checksum, artifact/SBOM/provenance checksums, and immutable non-production API digest before the protected production job may begin. |
| R1-3 | High | Production updated ECS before health probes, but a failed probe only failed the workflow. FR-009, AC-004, NFR-011, T019/T021. The failed task definition could remain live. | **Valid and fixed.** Promotion captures `PREVIOUS_TASK`; any ECS stability, liveness, or readiness failure restores that exact task definition, waits for it to stabilize, rechecks health, and exits nonzero. Workflow-contract coverage asserts restoration logic. |
| R1-4 | High | Rollback checked a manifest but did not bind the caller-supplied task definition to a retained healthy record. FR-009, AC-004, NFR-015, T020/T021. An arbitrary task-definition ARN could be restored. | **Valid and fixed.** Rollback consumes a named `production-promotion-evidence` artifact, verifies its release manifest, then requires its recorded task definition, source revision, and API artifact digest to match the supplied target and manifest before OIDC configuration or ECS update. |

### Round 1 areas with no finding

The reviewer found no issue in pending range shape or cleanliness, full-SHA action pins, PR trust boundaries, static AWS-key exposure, locked Docker/package definitions, manifest tamper checks, or the explicitly pending US3/cross-cutting tasks.

## Validation after fixes

- `node --check scripts/verify-promotion-evidence.mjs` — passed.
- `npm run test:release-evidence` — passed, including generated manifest verification and matching non-production promotion-evidence verification.
- `npm run test:package-definitions` — passed.
- `npm run test:ci-contract` — passed.
- `node tests/unit/release-manifest.mjs` — passed.
- `git diff --check` — passed (CRLF notices only).

## Round 2

Pending independent alternate-model assessment of the revised complete diff, all R1 dispositions, and this review record.

---

# Adversarial Specification Review

**Status**: Round 1 findings fixed; awaiting independent Round 2  
**Reviewer model**: gpt-5.5  
**Review date**: 2026-09-03  
**Base**: `feature/003-darktable-version-control`  
**Scope**: Feature 004 specification; no push is proposed.

## Round 1 findings and dispositions

| Finding | Severity | Affected IDs | Disposition and evidence |
|---|---|---|---|
| GitHub/AWS delivery controls could be manual and drift silently. | Medium | 004 FR-017, AC-010 | **Valid and fixed.** Required external settings must be versioned or have verifiable bootstrap evidence with drift reporting. |
| Shared conventions lacked direct acceptance coverage. | Medium | 004 FR-016, AC-011 | **Valid and fixed.** A child contract fixture and CI validation are required. |

## Validation

`git diff --check` passed before commit. Requirement IDs were checked for duplicates and the specification quality checklist remains 16/16 complete.

## Round 2

## Final agreement

The alternate reviewer confirmed FR-017/AC-010 protection-drift coverage and FR-016/AC-011 child convention-contract coverage. No valid unresolved finding remains.

**Final gate result: PASS**

---

# Adversarial pre-push review — CI audit reliability

**Status**: PASS
**Review date**: 2026-09-04
**Alternate reviewer model**: gpt-5.5
**Remote/destination**: `origin/feature/004-platform-foundation-ci-cd`
**Base**: `origin/feature/004-platform-foundation-ci-cd` (`ecdbcc9`)
**Reviewed scope**: `origin/feature/004-platform-foundation-ci-cd..HEAD`, initially `545f5ee`, plus the uncommitted correction and this review record. No push occurs until final PASS.

## Round 1

| ID | Severity | Evidence and affected IDs | Impact and remediation | Implementer disposition |
|---|---|---|---|---|
| R1-1 | Medium | The retry wrapper combined npm audit stdout and stderr, then parsed the combined text as JSON. A high/critical audit report on stdout plus transient-looking stderr could be retried. Affected: T034, FR-015, AC-008, NFR-003, NFR-015. | A real high/critical advisory could fail to get the required immediate classification; a later success could mask the original vulnerability observation. Keep stdout/stderr separate and parse stdout before retry classification. | **Valid and fixed.** `executeNpm` now returns separate streams; `runAudit` parses high/critical metadata from stdout first and uses combined streams only for diagnostics/transient classification. The regression test supplies high-vulnerability JSON plus `503` stderr and proves exactly one attempt with a high-vulnerability failure. |

### Round 1 areas with no finding

No issue was found in commit scope, workflow trust boundaries, pure transient bounded retries, direct high/critical JSON handling, increased job time budgets, local parity, documentation/task traceability, or pinned actions/permissions.

## Validation after the fix

- `node tests/unit/audit-dependencies.mjs` — passed, including transient retry, immediate high-vulnerability failure, and high-vulnerability stdout with transient stderr.
- `npm run test:ci-contract` — passed.
- `git diff --check` — passed.

## Round 2

The alternate reviewer confirmed R1-1 is **valid and fixed**: high/critical JSON on stdout now takes precedence over transient-looking stderr and cannot be retried into a later success. It found no new valid issue in the revised working-tree diff or review record.

## Final agreement

The alternate reviewer explicitly confirmed that no valid unresolved finding remains for the complete pending CI audit reliability scope.

**Final gate result: PASS**

---

# Adversarial pre-push review — CI MVP and foundation controls

**Status**: PASS
**Review date**: 2026-09-04
**Alternate reviewer model**: gpt-5.5
**Remote/destination**: `origin/feature/004-platform-foundation-ci-cd`
**Base**: `origin/feature/004-platform-foundation-ci-cd` (`5b0842e`)
**Reviewed committed range**: every commit reachable from `HEAD` and absent from `origin/feature/004-platform-foundation-ci-cd` at push time. The initial implementation commits were `5966bd2` and `1cc8076`; the dedicated review milestone is `ccaaa37`.
**Pending scope**: the complete dynamic range `origin/feature/004-platform-foundation-ci-cd..HEAD`, including the reviewed implementation, review fixes, and final review-record milestone; no push or external mutation is authorized by this record.

## Reviewer prompt scope

The alternate reviewer received the approved Feature 004 specification, plan, numbered tasks, ADR-0003, dependency policy, repository instructions, complete committed diff, relevant source/configuration/tests, and latest local validation evidence. It was instructed to attempt to disprove completed-task correctness, CI executability and security, supply-chain controls, observability, tests, operational risks, documentation, and scope discipline. The review was read-only.

## Round 1 — initial independent review

| ID | Severity | Evidence and affected IDs | Impact and suggested remediation | Implementer assessment and disposition |
|---|---|---|---|---|
| R1-1 | High | `verify-protection.yml` compared the committed baseline with the committed test evidence; `verify-protection.mjs` only read those two files. Affected: FR-017, AC-010, T010. | Actual branch or environment protection drift could pass the scheduled workflow. Fetch live settings with a read-authorized mechanism, or reject stale/missing observed evidence; prove a live-shaped mismatch fails. | **Valid and fixed.** `verify-protection.mjs` now calls GitHub’s protection and environment APIs using `PROTECTION_AUDIT_TOKEN`, normalizes their results, and compares them to the versioned baseline. The workflow invokes this live check and fails closed when the token/API access is absent. The mocked API test proves both matching live settings and a missing required check. The repository owner must still create the read-only secret before a remote run can pass; that external GitHub change is intentionally not performed here. |
| R1-2 | Medium | `README.md` advertised `npm run validate:security`, but `package.json` routed it to the deliberate placeholder failure in `scripts/validate.mjs`. Affected: FR-001, FR-015, AC-001, AC-008, NFR-003, NFR-009, T013. | The documented local security validation was unusable and CI/local parity was misleading. Wire the command to the implemented audit, secret, dependency, and license checks. | **Valid and fixed.** `validate:security` now runs root/web/infrastructure high-severity audits plus infrastructure secret, dependency-tree, and license checks. It passed with zero reported vulnerabilities and all local gates passing. |
| R1-3 | Low | `PlatformTelemetryMiddleware` recorded metrics/logs only after `await next(context)`, so a downstream exception bypassed its normalized request signal. Affected: FR-013, AC-006, NFR-007, T006. | Failed requests could lose operation-scoped middleware telemetry. Record in `finally`, mark activity failure, and test it. | **Valid and fixed.** Middleware now records a normalized `500` signal in `finally`, sets the activity status on exceptions, logs the failure, and preserves the original exception. The API foundation test creates a throwing downstream delegate and proves one request metric is emitted. |
| R1-4 | Medium | Independent implementation assessment found the backend job invoked root `npm run format`, which also invokes web Prettier, after installing only root dependencies. The web/infrastructure jobs also cached the root lockfile rather than their component lockfiles. Affected: FR-001/002, AC-001, NFR-009, T011–T014. | A clean CI backend job would fail for missing web dependencies; component caches would be keyed incorrectly. | **Valid and fixed.** Backend CI now invokes its .NET formatter directly, and each Node job declares its own lockfile cache path (security keys all three). CI-contract coverage asserts these isolation/cache invariants. |

### Round 1 areas with no finding

The alternate reviewer found no issue in branch/scope cleanliness, untrusted-PR exposure (no `pull_request_target`, OIDC, protected environment, or self-hosted runner), full-SHA action pins, completed T008 release-manifest tamper/compatibility logic, accidental artifacts/secrets, or the fact that US2/US3 tasks remain explicitly pending.

## Validation after Round 1 fixes

- `npm run format` — passed.
- `npm run lint` — passed, 0 warnings and 0 errors.
- `dotnet run --project tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj` — passed, including the throwing-middleware metric test.
- `node tests/infrastructure/protection-drift.mjs` — passed, including mocked live GitHub API normalization and missing-check detection.
- `npm run validate:security` — passed with root and web audits reporting 0 vulnerabilities; infrastructure secret, dependency, and license gates passed.
- `npm run test:ci-contract` — passed.
- `git diff --check` — passed.

## Round 2

The alternate reviewer agreed that R1-1 through R1-4 are **valid and fixed**. It confirmed that the required external `PROTECTION_AUDIT_TOKEN` is an intentionally unperformed bootstrap input, not a code-review blocker: the live audit fails closed and the record makes clear that no remote execution is claimed.

| ID | Severity | Evidence and affected IDs | Implementer assessment and disposition |
|---|---|---|---|
| R2-1 | Low | The reviewer ran `git diff --check` and found trailing whitespace in this review record’s metadata lines. Affected: review record and validation hygiene. | **Valid and fixed.** The trailing whitespace was removed. `git diff --check` is rerun before the Round 3 agreement; the record’s earlier validation statement now remains accurate. |

### Round 2 areas with no new finding

No new issue was found in CI trust boundaries, action pinning, aggregate behavior, security-command wiring, protection live-audit design, telemetry exception handling, task status, or the external-token documentation.

## Round 3

The alternate reviewer inspected the updated record and complete working-tree diff. It confirmed that `git diff --check` passes (the remaining CRLF notices are not whitespace errors) and agreed that R2-1 is **valid and fixed**. It also reconfirmed the valid-and-fixed dispositions for R1-1 through R1-4.

## Final agreement

The alternate reviewer explicitly confirmed that no valid unresolved finding remains for the reviewed pending code and review-record scope. The external `PROTECTION_AUDIT_TOKEN` remains a documented operational bootstrap prerequisite before the scheduled/manual live protection audit can pass remotely; it is not a code-review blocker because the workflow fails closed and no remote run is claimed.

**Final gate result: PASS**

## Pre-push review — 2026-09-03

**Reviewer model**: gpt-5.4 | **Remote/destination**: `origin/feature/004-platform-foundation-ci-cd` | **Reviewed range**: `origin/feature/004-platform-foundation-ci-cd..HEAD`

The independent review found the prior scope stale and the recorded master-divergence direction incorrect. Both are **valid and fixed**: this record names the exact pending range and [plan.md](../plan.md) now reports divergence without unstable directional counts. `git diff --check origin/feature/004-platform-foundation-ci-cd..HEAD` passed. Follow-up alternate-model agreement is required before push.

## Final agreement for current pending range

The alternate reviewer rechecked the pending push range `origin/feature/004-platform-foundation-ci-cd..HEAD` and confirmed the previously identified scope-record staleness and reversed divergence wording are fixed. The current review record now names the exact pending range, the plan uses stable non-directional divergence wording, and the added ADR/contract/data-model/plan/tasks set is internally consistent with the reviewed specification scope. No valid unresolved finding remains on the provided evidence.

**Final gate result: PASS**
