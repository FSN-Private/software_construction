# PR draft: Qualify nonprod rendering from the approved Singapore client

The last bootstrap passed native rendering but failed cache p95 at647ms. The
owner approved Singapore as the controlled nonprod nominal-client location.
This runs the unchanged rendering/ingest scripts through the same public HTTPS
API with unchanged500ms/2s/30s limits. Stopped-gateway transport diagnostics are
not healthy performance evidence; remote-user and production remain unqualified.

Release evidence includes the exact source-bound client zip. Published numeric
Lambda version/code/config checks precede database startup. Strict result checks
gate activation. The isolated client stack has bounded compute and log-only IAM;
no dependencies, schema changes or application allocation changes.

JSON-only acceptance evidence is retained, then activation runs, then final
deployment evidence is retained. Any failure invokes guarded recovery. Admission
creates a server-authored receipt in existing operator audit storage; recovery
restores the full prior approval transactionally with intake closed before old
ECS tasks/counts. Latest-receipt/operator/config/current-state and task-digest
guards prevent stale or incompatible rollback. Ambiguous admission aborts before
service replacement. Stopped prior API leaves intake closed. Production retains
its direct smoke path and all existing promotion/security restrictions.

Specification/ledger: docs/features/016-regional-deployment-acceptance/{spec,plan,tasks}.md
Branch: feature/016-regional-deployment-acceptance; follow-up Feature011T054/PR45.
T001-T006 complete; T007 final review/authority and T008 real rollout remain open.
No issue closure is claimed.

Validation PASS: full release-evidence and CI contracts; infrastructure build,
synthesis/IAM/ZIP byte equality, vendor integrity, secret/dependency/license checks
and authorized registry audit; migrated PostgreSQL approval/receipt/concurrency
regressions; full existing PostgreSQL/S3 ingest/HTTP/rendering/recovery integration;
Bash syntax and whitespace. Final alternate-model review pending. Local fixtures
never qualify real performance.

Rollout requires explicit publication, gated merge and isolated client apply
approval after source-bound release/template/cost checks. Save fixture IDs only
after native/security/readiness/regional latency/ingest/activation/final upload and
workflow success. Detailed cost, observation, recovery and cleanup procedure:
docs/releases/2026-10-21-regional-acceptance/release-plan.md.
