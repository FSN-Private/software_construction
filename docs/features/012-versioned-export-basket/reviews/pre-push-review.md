# Adversarial Pre-Push Review

**Reviewer model**: gpt-5.5  
**Review date**: 2026-09-03  
**Base**: `master`  
**Scope**: Feature 012 specification, ADR-0004, checklist, and existing adversarial review report; no implementation files.

## Findings and dispositions

The independent review's prior findings on snapshot pinning, metadata privacy, lifecycle semantics, and package integrity are documented in [adversarial-review.md](adversarial-review.md). Each was fixed and independently reassessed as PASS.

## Validation

The specification has 26 functional requirements, 15 acceptance criteria, and 18 non-functional requirements; the requirements checklist is 16/16 complete. `git diff --check` passes and no accidental files were found.

## Final agreement

The alternate reviewer agrees that all findings are resolved and no valid unresolved issue remains.

**Final gate result: PASS**

## 2026-10-05: staged implementation checkpoint (T031)

**Alternate reviewer:** `gpt-6-astra`, read-only `/root/plan_review`. This is a new implementation review; the September specification-only PASS above is preserved and is not used to certify this delivery.

**Remote:** `origin` (`https://github.com/FSN-Private/software_construction.git`). **Destination:** `refs/heads/feature/012-versioned-export-basket`; new PR base `master`. **Base:** `2680c6abed7ac463b13dfc36cc8327d2debfa04c`. **Reviewed head:** `f020eee9e7b250f3673d0b61ed1b0db843cee8aa`; complete cumulative range base..head, plus the dedicated final review-record/task-status commit reviewed before being committed unchanged. No unreviewed runtime change is included in that final commit. The prior specification PR #2 is already merged; this delivery requires a new implementation PR. The existing remote feature head is an ancestor, so a normal fast-forward push suffices.

**Authority and scope:** maintainer explicitly requested push, PR, CI follow-up and rebase merge if passing, returning when feature 011 is ready. T031 authorizes staged delivery of the disabled checkpoint. This does not waive a blocking implementation finding, complete feature 012/T026, enable production, or implement/merge feature 011. T029, deployed transfer, manual accessibility/usability, trusted-renderer evidence and related T025/T026 acceptance remain open.

### Review round 1: PASS for staged delivery

Prompt scope: attempt to identify significant blockers across the complete implementation, approved artifacts, numbered plan, tests, configuration, migrations, CI, supply chain, history and exact validation evidence. Assess whether the disabled checkpoint can safely be pushed/conditionally merged while acceptance remains pending; reassess every earlier finding. Require classified severity/evidence/impact/task mapping and remediation for any finding. No desired result was specified.

The reviewer inspected committed source/tests/configuration/migrations/workflows and the recorded validation, and ran the complete-range whitespace check successfully. Runtime suites were not independently rerun. Working tree was clean, 16 ahead and 0 behind freshly fetched `origin/master`.

| Area | Independent assessment |
|---|---|
| Disabled capability | No blocker: control/download admission and preparation claiming enforce disabled state; UI controls require explicit enablement; maintenance/revocation remain available. |
| Infrastructure | No staged-delivery blocker: ALB/CloudFront require explicit exportTransport=true, which still sets exports disabled; private routing/cache/export-prefix controls remain intact. |
| Authorization/privacy | No unresolved blocker: owner isolation, audited denials, grants/revocation, metadata treatment and safe telemetry retain their fixes. |
| Integrity/concurrency | No unresolved blocker: exact pins/snapshots/provenance, publication fences, durable write inventories and positive cleanup proof remain intact; unknown outcomes remain draining. |
| Migration/rollback | No blocker identified: 021 is additive and embedded; normal deployment migrates before replacing services and preserves prior readers. Enabled rollback/deployment recovery evidence remains pending. |
| Browser/accessibility/compatibility | No unresolved local blocker: presets, discovery, announcements and logout fixes remain intact; manual assistive/deployed-transfer acceptance is not certified. |
| CI/supply chain | No blocker identified: export contracts/integration and opt-in transport checks are in CI; no new dependency/action. PR checks must pass before merge. |
| Operations/delivery | No blocker for this scope: existing master CI/release-evidence/nonproduction deployment chain may run after merge; production promotion is separate tag/manual authority and export enablement is excluded. |
| Traceability/history | No blocker: milestones and every remediation round remain preserved; T031 distinguishes staged delivery from unfinished acceptance. |

All prior findings remain independently resolved, with their evidence/fixes/dispositions preserved in [implementation-review.md](implementation-review.md): 001 durable workspace inventory/verified removal; 002/004 owner/operator denial audits; 003 executable positive-evidence recovery; 005/006 safe metric collection/lifecycle backstop; 007 bearer-session revocation; 008 URL-safe expired-session logout; 009 retained-request discovery/pagination; 010 live announcements; 011 preset-specific originals presentation; 012 gallery localization; 013 approved admitted-transfer expiry behavior. Implementer assessment: valid and fixed, with no change to earlier evidence or disposition. No new blocking or non-blocking finding was raised.

### Validation supplied to the reviewer

Exact evidence and historical failures/retries remain in [validation.md](../validation.md):

- `node tests/integration/exports/run.mjs`: PASS PostgreSQL/MinIO, provenance, grants/revocation, actual admitted bearer expiry, native-cookie expiry, later range/HEAD denial, worker/provider/filesystem faults, late/lost multipart outcomes and unknown-outcome acknowledgment refusal.
- `node tests/performance/exports/run.mjs`: PASS 20 baskets/250 items/10 retained requests; p95 open 235.8 ms, update 316.5 ms, submission 90.4 ms. Local server timings do not certify browser usability.
- `BROWSER_TEST_DISABLE_FIREFOX_ACCESSIBILITY=1 BROWSER_TEST_PORT=4196 playwright test exports.spec.ts` in apps/web: 39 PASS Chrome/Edge/Firefox, 57.8 seconds. Windows diagnostic Firefox setting is not manual assistive acceptance.
- API formatter verification and web format/build/typecheck/source/accessibility structure: PASS. Existing baseline: 116 PASS, three live skips, one Firefox timeout passed isolated retry; no single-run all-pass claim.
- Infrastructure full validation/default synthesis and opt-in synthesis/transport assertions: PASS; infrastructure unchanged since the corresponding evidence. Local native renderer corpus PASS does not establish trusted promotion.

### Agreement and current gate

Reviewer explicitly states: **PASS for the explicitly authorized disabled-checkpoint delivery under T031; no valid unresolved blocking implementation finding remains.** All previous findings remain resolved. Conditional rebase merge requires passing PR checks; full feature acceptance and production enablement remain gated. Final pending-record fidelity verification is required before committing this record unchanged and pushing.
