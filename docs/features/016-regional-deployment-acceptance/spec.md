# Feature Specification: Regional deployment acceptance

**Feature Branch**: `feature/016-regional-deployment-acceptance`
**Created**: 2026-10-21
**Status**: Approved design; implementation authorized by owner reply
"Approve Singapore nominal-client design". External mutations remain gated.
**Owner**: repository product owner.
**Input**: Controlled nonprod Singapore nominal client, same public HTTPS API,
500ms/2s/30s and full existing rendering/ingest workload.

## Problem and scope

Feature011 T054 bootstrap39488893341 passed native/migration/startup/five uncached
renders but failed cached p95647ms. API timing cohort p9551.4824ms lacks exact
request correlation. Stopped-gateway probes39543960738/39544365704 measured
401-502ms p95; these errors are diagnostic only, not healthy lower bounds.
Node comparisons did not demonstrate a runtime-only repair. The owner approves
Singapore nominal performance acceptance. This changes acceptance geography;
it does not establish500ms for remote users or production.

Users are the protected release operator and photographers relying on qualified
rendering/ingest. Regional acceptance means real authenticated public API work,
not an internal shortcut. External observations remain separate. No threshold
waiver, app allocation increase, schema/data migration, production/DR promotion,
general-purpose runner or always-running compute.

## User Scenarios & Testing

### User Story 1 - Qualify the real workload regionally (Priority: P1)

The operator measures the deployed candidate from the approved nominal region
before activation. This prevents unrelated runner geography from defining the
regional reference benchmark while retaining the actual application gates.

**Independent Test**: real child-process/HTTP fixtures plus final exact-source
nonprod evidence, never synthetic local performance as cloud qualification.

1. Five distinct genuine uncached renders finish within30s, request-start p95
   stays within2000ms and twenty concurrent cached responses meet500ms p95
   through the existing public HTTPS API (AC-001).
2. Rendering then ingest upload/download/delete/refusal/restore/reconstruction
   checks pass before activation and evidence upload permit saved fixtures
   (AC-002).
3. Any missing evidence, failed smoke or ambiguity fails deployment and invokes
   existing rollback; no hidden retry, sample removal or fixture qualification
   (AC-003).

### User Story 2 - Trust the client and protect credentials (Priority: P1)

The operator verifies which client ran without disclosing owner tokens.

**Independent Test**: tampered bundles, wrong published versions/bindings,
malicious child output and transport/function errors fail closed.

1. Only the release's checksummed client bytes in an exact published numeric
   function version can qualify that source/release/invocation (AC-004).
2. Owner tokens are transient invocation data, absent from configuration,
   logs, artifacts, AWS CLI output and raw child errors (AC-005).

### Edge Cases

Reject wrong region/origin, production use, aliases/$LATEST, wrong code/source,
duplicate/malformed fixtures, bad/expired token, nonfinite/wrong-count metrics,
FunctionError, transport ambiguity, child timeout/reset/excess output and
malformed/missing response. Activation/upload failures cannot qualify fixtures.

## Requirements

### Functional Requirements

- **FR-001**: Nonprod MUST run the existing rendering and ingest scripts from
  ap-southeast-1 against the same public HTTPS origin: five distinct fixtures,
  twenty concurrent cache requests, full-response timing and unchanged
 500ms/2000ms/30s. No prewarm, hidden retry or discarded samples.
- **FR-002**: The protected workflow MUST keep bootstrap/admit/activate,
  native/security/artifact/migration/readiness and rollback control. Both smokes
  and successful evidence upload are necessary before saved fixtures.
- **FR-003**: Verify release-attributable client bundle/code checksum, numeric
  published version, source/release/manifest/invocation, region/origin, sample
  counts and recomputed result thresholds. Retain safe proof in evidence.
- **FR-004**: Restrict the bounded synchronous client to nonprod/Singapore and
  its configured public origin. Execution role has only scoped log writes.
  Caller has scoped invocation/necessary metadata reads, no function creation,
  update or application-data/secret permissions for this task.
- **FR-005**: Tokens stay in memory/restricted temporary invocation files cleaned
  in finally, never configuration/logs/artifacts/raw errors. Children receive
  only required environment, excluding Lambda AWS credentials.
- **FR-006**: Errors and ambiguity fail closed with safe stage/count/timing
  evidence and no automatic reinvocation. Production keeps its existing gate.

### Key Entities

Client bundle (zip checksum/source-bound manifest subject), published client
version (ARN/code/runtime), invocation (unique ID/source/release/manifest/origin/
fixtures/transient token), safe result (binding, region/runtime, smoke metrics),
deployment record (existing task/digest bindings plus client proof). No new
authoritative application data or credential storage.

### Non-functional Requirements

- **NFR-001 Performance**: FR001 limits unchanged; first real API request includes
  HTTP/2 setup. Client512MiB/300s/concurrency1, configured execution budget
  150GB-seconds/invocation; separately billed initialization is additional.
- **NFR-002 Reliability/availability**: timeout/error/malformed/ambiguous paths
  fail closed;64KiB output bounds; no invocation retry or recurring availability
  dependency outside deployment acceptance.
- **NFR-003 Security/privacy/compliance**: IAM-only invocation, no function URL,
  events, VPC/NAT or broad data grants; verifiedTLS; zero token/input/locator/raw
  exception disclosure; existing supply-chain gates; seven-day client logs.
- **NFR-004 Compatibility/maintainability/testability**: standard Node builtins,
  existing CDK/actions/toolchain; no new package; reuse both scripts. Test real
  child execution and AWS CLI seam rather than duplicate implementations.
- **NFR-005 Observability**: source/release/function/code/runtime/region,
  invocation and all safe samples; distinguish diagnostics, regional acceptance
  and unqualified remote-user performance.
- **NFR-006 Deployment/operations/cost**: separate client stack, no app/data
  replacement, schedules, provisioned concurrency or always-running compute.
  Confirm Singapore prices and existing20USD budget before apply. Version/code
  mismatch aborts. Explicit cleanup/rollback procedure for client resources.
- **NFR-007 Accessibility/localization**: machine-only client has no new UI;
  human-readable English failures and unchanged product accessibility.

## Success Criteria

- **SC-001**: AC001-005 and negative-path/source-binding checks pass.
- **SC-002**: Required CI, alternate-model review, attributable release and real
  regional rendering/ingest/evidence gates pass before any live claim.
- **SC-003**: No token disclosure, app allocation/numerical threshold change or
  production qualification.

## Assumptions and clarification

Approved proposal resolves material choices. Routine details: package both
scripts and the existing PNG fixture; verify numeric version/zip checksum;
seven-day logs and a separate stack. No unresolved material clarification, so
Spec Kit clarify requires no additional question. Design approval authorizes
implementation, not push/merge/provisioning. Prepare concrete tested artifacts
and obtain final external authority. Node24 client runtime is recorded in proof.
