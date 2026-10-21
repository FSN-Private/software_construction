# PR draft: Run nonprod acceptance from the approved Singapore client

The last bootstrap passed native rendering but failed cached response p95 at647ms.
Controlled GitHub-runner stopped-gateway error probes measured401-502ms p95;
they do not establish healthy-response lower bounds. The owner
approved Singapore as the controlled nonprod nominal-client location; this change
implements that decision through the same public HTTPS API with unchanged
500ms/2s/30s thresholds and full existing rendering/ingest scripts.

Packages a source-bound client zip in release evidence and runs it as a numeric
published Lambda version. Metadata preflight rejects stale code/config before
database startup. Sanitized, independently checked results gate activation; failures
use existing rollback. The isolated client stack has bounded compute and log-only
execution IAM. Production retains its existing smoke path. No new dependencies,
application allocations or schema changes.

Specification: docs/features/016-regional-deployment-acceptance/spec.md
Plan/ledger: docs/features/016-regional-deployment-acceptance/{plan,tasks}.md
Branch: feature/016-regional-deployment-acceptance
Implementation: T001-T006 complete; T007 security/review and T008 live rollout
remain separately recorded. Follow-up to Feature011 T054 / PR45; no issue closure.

Validation: complete release-evidence suite, CI contracts, infrastructure
TypeScript/build, isolated synthesis/IAM/byte equality, vendored archive integrity,
secret scan, dependency tree, licenses, Bash syntax and whitespace checks PASS.
Registry audit and final independent-review status must be updated from the ledger
before publication. Registry audit subsequently PASS with explicit metadata-export
approval. Post-activation failed/skipped evidence upload restores previous tasks/
counts using a source/current-state guarded recovery script; its real Bash failure
tests PASS. Final independent review is still pending. Local fixtures do not
qualify cloud performance.

Risk/rollout: no production or remote-user latency claim. Require explicit approved
client apply and protected numeric-version configuration after merged-source CI and
exact release verification, then bootstrap. Keep native/security/readiness/latency/
ingest/activation/evidence gates; save fixture IDs only after successful upload.
Application rollback and paired release/client version rollback are documented in
docs/releases/2026-10-21-regional-acceptance/release-plan.md and deployment runbook.
