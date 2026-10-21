# Implementation Plan: Regional deployment acceptance

**Branch**: `feature/016-regional-deployment-acceptance` | **Date**:2026-10-21
**Input**: [approved specification](spec.md); approved Singapore nominal-client
proposal from diagnostic commitdb8f18c. Related Feature011 T054, Feature008 NFR001.

## Summary and technical context

One Node24 Lambda executes the existing rendering/ingest scripts against the
same public API. GitHub retains deployment controls. Standard Node builtins,
existing PowerShell/.NET zip library, AWS CLI, pinned CDK2.267/actions; no package
addition. Separate client stack,512MiB/300s/concurrency1, log-only role. The
canonical Linux release zip is the exact provisioning input, not a local rebuild.
No new application storage, schema, runtime image or app allocation changes.

## Constitution check

PASS: approved material design; matching feature directory/branch, initial clean
tree and0divergence; all requirements traced below. Separate milestones, real
failure/security tests, documentation and different-model pre-push review.
No history rewrite or inferred push/merge/provisioning authority. No extension
hooks configured. Clarification finds no unresolved material question.

## Components and interfaces

- `scripts/regional-acceptance/handler.mjs`: bounded input, fixed configured
  public origin, Singapore-only runtime, numeric published version, real children
  with credential-free environment except transient owner token. Rendering then
  ingest,120s child/64KiB bounds, no child raw output on failure. Safe duration
  diagnostics may be extracted only from the known latency-failure record.
- `scripts/package-regional-acceptance.ps1`: standard ZipArchive packages the
  handler, both unchanged scripts and existing PNG; normalized text/timestamps,
  fixed entry names and deterministic order. No download/install or new package.
- Build release evidence publishes `regional-acceptance-client.zip` as an
  additional checksummed/provenance subject. Generator/verifier keep old release
  sets readable; the new nonprod invoker requires the new exact bundle.
- `scripts/invoke-regional-acceptance.mjs`: SHA/source/ARN/config check, synchronous
  AWS CLI invoke of numeric version, no retries,10s connect/330s read/340s process
  bounds. Restricted temporary payload/result files cleaned in finally, raw CLI
  errors suppressed. Verify bounded response and recomputed metrics/bindings.
- Separate `packages/infrastructure/src/regional-acceptance-{app,stack}.ts`:
  explicit nonprod/Singapore/account/origin/deploy-role/bundle contexts. No main
  app stack changes. Function code from the exact release zip; published version
  has expectedCodeSha256; output versionARN. No URL/events/VPC/NAT/schedules.
  Seven-day log group and log-only execution role; deploy role can invoke/read
  metadata for qualified versions of this one function, not update/create it.
- `.github/workflows/build-release.yml` packages zip before artifact upload.
  Existing `ci / infrastructure` validates handler/invoker/packaging and client
  synthesis contracts through the existing release-evidence suite; stable CI
  check names/events, timeouts and pinned actions remain. No new workflow token
  permission or external package/action. Deploy nonprod pins .nvmrc Node for its
  invoker and requires protected `AWS_NONPROD_ACCEPTANCE_FUNCTION_VERSION`.
- `scripts/deploy-ecs-release.sh`: nonprod calls the regional invoker after
  bootstrap/admission, preserving native/readiness/security checks. Production
  retains direct existing smokes. Merge safe regional proof into existing
  deployment evidence; original activation/error trap remains authoritative.

## Security, data and failure behavior

See [contracts](contracts/invocation.md) and [data model](data-model.md). Never
store token in Lambda configuration/logs/artifacts or pass Lambda AWS credentials
to children. Strict fixed origin, fixture/count/string bounds, request/source/
manifest/code/version/region bindings. Lambda return status is not invocation
success: reject FunctionError, failed result, malformed/oversized/nonfinite/
mismatched data or transport ambiguity. No retry/prewarm/sample selection. CLI
temporary directory0700 and files0600 on Linux; delete on every completion path.
No function-event logging. Only validated safe failure stage/timing evidence.

## Rollout, migration, rollback and costs

No data migration. Complete implementation/tests/template review first, then
obtain concrete push/PR/merge/provisioning approval. Require fresh source CI and
release. Verify zip checksum/provenance, synthesize separate client stack using
that exact zip, inspect IAM diff and regional cost, then apply only if authorized.
Set the protected numeric versionARN only after metadata/code validation. Existing
automatic nonprod deploy fails closed until this setting exists. Rerun authorized
bootstrap with all gates; save fixture IDs only after success/evidence upload.

The configured compute cap is150GB-seconds/invocation, no recurring compute.
Confirm Singapore prices/budget before apply; logs retain7days. Preserve previous
published client versions for attributable rollback. Existing app paired rollback
is unchanged. Disabling/removing the client cannot qualify a release: nonprod
fails closed; no silent fallback. Remove client/IAM/log resources explicitly when
retired. Production and global remote-user500ms remain unqualified.

## Alternatives and risks

Owner rejected retaining arbitrary external-runner geography as this nominal
benchmark, not the500ms number. Always-running runner, broad CodeBuild/task role,
internal API bypass, threshold increase and synthetic qualification add risk or
violate approval. Regional latency may still fail. Function/runtime/version drift,
token disclosure, stale client and malformed success are testable failure risks.
No open material decision; final external authority is still required.

## Validation, milestones and traceability

[tasks.md](tasks.md) is the sole execution ledger. M0 specification/design/ADR;
M1 real handler/zip tests; M2 source-bound invoker/artifact contracts; M3 isolated
infra/IAM synthesis; M4 deploy integration and docs; M5 cross-artifact convergence,
full relevant checks, independent review and concrete publication/apply package.
Live acceptance is a distinct final task, never inferred from local PASS.
