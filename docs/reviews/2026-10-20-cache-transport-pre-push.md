# T054 cache transport diagnostic pre-push review

Reviewer: independent read-only `gpt-6-astra`, 2026-10-20.
Base: `1130d9cf7930cf6c1433930f8cf0129eaeb622e9`.
Reviewed implementation: `1130d9c..c772438`, plus pending local summary correction
and this record. Proposed destination: `origin/fix/011-render-cache-qualification`.
Initial branch was clean with zero divergence; the prior diagnostic branch and
rebase-merged history are preserved. **Push authorization is still pending.**

## Governing request and evidence

Owner requests final deployment repair; T054 already covers cache diagnosis with
unchanged 500ms/2s/30s thresholds. Owner approved PR45 creation, gated merge and
resulting verified nonprod bootstrap. PR/master CI and release39485682288 passed;
bootstrap39488893341 passed native/migration/startup/five uncached renders but
failed cache p95647ms. Independent AWS readback confirms stopped rollback
API6/ingest4/renderer1. No qualified fixtures, ingest acceptance or activation.

The reviewer checks the complete proposed diff, actual failed-run log, retained
API timing window, local diagnostic logs, exact-source archive/security/registry
verification reports, governing plan/tasks and repository push rules. Twenty
successful API events in the cache window have p9551.4824ms; missing route and
request correlation prevents calling this exact per-request attribution. Mixed
one-minute gateway metrics do not isolate cache requests. No specific network
cause or runner location is established.

## Findings and independent assessment

- Functional/failure handling: no blocking finding. Fixed public GET/live,
  verified default TLS/H2, three PINGs and twenty simultaneous complete responses,
  15s request/45s global/64KiB bounds and finally session destruction. HTTP errors
  remain diagnostic data with explicit qualification:false.
- Security/privacy/provenance: no blocking finding. Read-only contents token,
  existing pinned actions, no persisted checkout credentials, protected
  environment, AWS/OIDC/secrets, bodies, artifact upload or deployment writes.
  Fixed origin prevents arbitrary endpoint input. No dependency addition.
- Scope/reliability/tests/docs: no blocking finding. Two bounded advisory samples
  on ubuntu-24.04, five-minute jobs, push/path restricted to the proposed branch.
  No acceptance, resource, geography or runtime change. Reviewer independently
  recomputes the twenty API event durations/p95 and checks failed gate/local
  transport logs; docs preserve evidence limits and unresolved qualification.
- N2, non-blocking/low: local response summary204-400ms did not include a second
  settings run's411.6ms maximum. Implementer assesses this as valid summary polish
  and changes the summary to204-412ms. Reviewer inspects that exact pending edit
  with this record; it is not a new acceptance result.

Prior R1 (effective release ID binding in the native diagnostic) remains
valid-and-fixed and unaffected. No new blocking disposition or waiver is needed.

## Validation and final gate

Implementer and reviewer pass `node --check scripts/diagnose-rendering-transport.mjs`,
`node tests/infrastructure/ci-contract/validate-ci.mjs` and complete whitespace
checks. Actual local TLS/H2/PING/twenty-response execution passes and records
stopped-gateway500 responses; no healthy qualification is claimed. No application
test rerun is necessary for this read-only diagnostic and evidence-only slice.

Final gate: **PASS for publishing/running this bounded diagnostic only**, after
the reviewer explicitly agrees to this exact record and pending N2 edit before
unchanged commit. No unresolved blocking finding may remain. The review grants
no push, merge, deployment or acceptance authority. Obtain explicit authorization
for the proposed branch push; remote results and final T054 remediation remain
pending. No application code, fixture variable or deployment threshold changes.

## Round 2: control Node version before interpreting transport

Reviewer: same independent read-only `gpt-6-astra`, 2026-10-21.
Scope: `d822b9d..f8e509b` plus this pending append, destination unchanged
`origin/fix/011-render-cache-qualification`. The owner explicitly approved the
diagnostic branch push/run; this bounded same-branch follow-up uses that scope.

First run39543960738 completes both samples under .nvmrc Node24.20.0. PING is
207.6-221.1ms; twenty public35byte gateway500 responses have p95425.7/502.3ms.
The deployment used installed Node22.23.3. Preserve that mismatch as an evidence
limit: neither a particular network cause nor healthy application latency is
proved. Compare exact22.23.3 followed by .nvmrc on each of the same two runners,
using separate sessions and the unchanged script. All results remain visible.

Only existing SHA-pinned setup-node/runtime execution and plan/task evidence
change. Token permissions, timeout, runner count, fixed public endpoint,
TLS/PING/burst bounds, qualification:false, no AWS/secrets/artifacts/deployment
writes and unchanged500ms acceptance remain intact. No runtime or application
fix is claimed and no region/resource/nominal-load change is implemented.

Implementer passes CI contracts and whitespace checks. The unchanged script's
actual local TLS/H2/PING/burst validation remains applicable. No application
test rerun is claimed. Independent review assesses first-run logs, full revised
range and record; prior R1 and N2 remain fixed and unaffected.

Final gate: **PASS for this controlled diagnostic follow-up only**, after the
reviewer explicitly confirms no unresolved blocking findings and agrees to these
exact record bytes before unchanged commit. This is not deployment acceptance
or permission for a later application/architecture change. Remote comparison
results and final T054 remediation remain pending.
