# ADR0020: Regional nonprod acceptance client

Date2026-10-21. Status: **Accepted design**, owner explicit "Approve Singapore
nominal-client design". Implementation/provisioning/release remain gated.
Scope: [Feature016](../../features/016-regional-deployment-acceptance/spec.md),
Feature011 T054 and Feature008 NFR001/SC001.

## Context and decision

Remote runner transport materially consumes the500ms nominal limit; diagnostics
are failed public gateway responses and not proof of healthy performance. Owner
defines Singapore as the controlled nonprod nominal client. Use one on-demand,
source-bound Node24 Lambda through the same public API,512MiB/300s/concurrency1,
no schedules/provisioned concurrency or app resource increase. Reuse real smoke
scripts and keep all deadlines, workloads, authentication and rollback gates.

Retain external observations separately; remote-user500ms and production remain
unqualified. No raising numerical thresholds or accepting prior failed runs.

## Alternatives and consequences

Retaining external-runner500ms was offered and not selected. Always-running
runner/CodeBuild add operational overhead; internal bypass/synthetic work or
sample selection would weaken acceptance. Regional execution may still fail.
Tradeoff: this measures the explicitly approved reference region, not global UX.

IAM-only exact-version invocation with code/source proof; token stays transient,
client AWS role logs only, caller invokes/reads this function only. No application
data/secrets permission or public function URL. Separate stack and published
version make changes attributable. No database migration or extra app resources.
Cost is bounded on-demand compute plus API/log usage; confirm rate/budget before
apply. Logs7days; preserve client versions for rollback. Retirement requires
explicit client/IAM cleanup; absence fails nonprod closed rather than bypassing.

Implement/test/independently review per Feature016 tasks; obtain final concrete
push/merge/provisioning approval. Success requires fresh CI/release/security and
real regional rendering/ingest/evidence gates before fixture persistence.
