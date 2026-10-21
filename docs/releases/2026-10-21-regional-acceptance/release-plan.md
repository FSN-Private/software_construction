# Release plan: Regional nonprod acceptance, 2026-10-21

Owner/operator: repository owner; implementation/operator assistant. Scope:
[Feature016](../../features/016-regional-deployment-acceptance/spec.md), tasks
T001-T008, ADR0020. Branch `feature/016-regional-deployment-acceptance`, base
1130d9cf7930cf6c1433930f8cf0129eaeb622e9. Final commit, PR, merged source, CI and
release run IDs must be recorded after authorized publication; none inferred.
Production, DR promotion and remote-user 500ms qualification are excluded.

## Current state and review inputs

Last deployment39488893341 rolled back after cache p95647ms; no fixture IDs saved
and no live qualification. Native references passed. Controlled transport
runs39543960738/39544365704 motivated the owner-approved Singapore nominal-client
design. Local checks establish contracts only; real performance remains unknown.

Review separate synthesized template
`artifacts/regional-acceptance-synthesis/RegionalAcceptance-nonprod.template.json`
and `packages/infrastructure/src/regional-acceptance-{app,stack}.ts`. Local staged
zip bytes are tested equal to input; the cloud input MUST instead be the exact
Linux release's verified `regional-acceptance-client.zip`. No dependency/action
addition. Publish bundle checksum, SBOM and provenance with existing release.
Full independent review and security checks are prerequisites, not waived here.

Resources: one Singapore Node24 x86 Lambda512MiB/300s/reserved concurrency1,
seven-day log group, execution role with scoped log writes, retained published
code version, and policy on existing github-actions-nonprod-deploy role granting
only qualified-version InvokeFunction/GetFunctionConfiguration. No function URL,
triggers, VPC, NAT, provisioned concurrency or application resource replacement.
Protected configuration addition: AWS_NONPROD_ACCEPTANCE_FUNCTION_VERSION is the
verified numeric version ARN. Token and fixture locators are invocation-only data.

## Cost and prerequisites

Public AWS Singapore Lambda catalog read2026-10-21:
[regional pricing JSON](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AWSLambda/current/ap-southeast-1/index.json)
reports x86 first-tier duration USD0.0000166667/GB-second and requests
USD0.0000002/request. Configured execution budget150GB-seconds therefore costs
USD0.002500005 duration +0.0000002 request for a full300s invocation. Initialization,
CloudWatch logs/storage, API/ingest/rendering and transfer are additional; this is
not a hard total spend cap. No free-tier discount assumed. No invocation schedule.
Reconfirm public pricing, current account spend and existing USD20 nonprod budget
before apply; abort if insufficient headroom. Conservatively allow USD1 incremental
client spend for this rollout (at most100 operator-controlled invocations; no
automatic uncertain-execution retry), separate from the existing application bill.

Need valid nonprod AWS credentials, existing account/origin/deploy-role contexts,
reserved concurrency quota (AWS requires unreserved headroom), CloudFormation/CDK
asset publication permissions and GitHub environment-variable administration.
Read-only metadata/quota/budget checks first. Missing permissions/quota/budget
are blockers; do not broaden IAM, remove bounds or change allocations to bypass.
No new application schema or state migration. Existing coordinated application
migration/backup/recovery prerequisites remain in the deployment runbook.

## Authorized rollout sequence (execution still pending)

1. Obtain explicit final push, PR creation, gated merge, separate client apply,
   protected configuration and nonprod bootstrap approval after local review.
   Push exact reviewed range, create linked PR, wait all required checks; merge
   only approved passing head. Record PR/source. Do not deploy unreviewed bytes.
2. Require merged-source CI and attributable release success; download consolidated
   release, verify source/repository/run, checksums/SBOM/provenance with
   `--require-regional`, native/security gates and approved vulnerability policy.
   Record source, release run, manifest hash and client zip hash/bytes.
3. Synthesize isolated client app with explicit nonprod account, Singapore public
   origin, existing deployment role and exact release zip; inspect diff against
   reviewed resource/IAM scope. Only client stack may change. Check spend/quota.
   Apply only within final approval, record stack/version outputs and template.
4. Verify numeric version/code SHA, Node24, handler, origin-only environment,
   512MiB/300s/no layers/VPC; compare all with release. Set protected variable
   only after equality. Preserve previous qualified ARN for paired rollback.
5. Dispatch merged workflow on the verified release in fixture-bootstrap mode.
   Metadata preflight must pass before database startup. Preserve native,
   migration, readiness, bootstrap, pause/admit, security and rollback controls.
   Exact published client runs both real scripts through same public origin.
6. Require five uncached renders each within30s/start p95<=2000ms and twenty
   concurrent cached bodies p95<=500ms, then complete ingest checks. Both pass
   before activation. Retain the JSON-only acceptance evidence, activate, then
   upload the existing final deployment evidence. Require workflow success before
   saving qualified fixture IDs. Record all samples, source, function/runtime,
   manifest/code hashes and activation/evidence/fixture qualification timestamps.
7. Observe API/ingest/renderer desired=running=1, no ECS deployment failure,
   authenticated readiness successful and no new error/alarm during ten minutes.
   Record regional limitations in user delivery; no production/live claim if any
   gate failed. Preserve scheduled nonprod shutdown policy.

Abort on any source/code/config/region/account mismatch, security gate failure,
ambiguous invocation, missing proof, latency/ingest failure or failed activation/
evidence upload. Preserve safe logs, no fixture save or retry of ambiguous work.
Assistant stops at unexpected material state and owner decides unsafe recovery.

## Rollback, reconciliation and cleanup

Existing deployment error trap aborts acceptance and restores previous API,
ingest and renderer task definitions/counts; confirm desired/running state and
record actual outcome. Ambiguous admission attempts abort before task recovery.
Failed/skipped preliminary evidence, final activation or final evidence invokes
workflow recovery. It validates source/current task ARNs and prior running API/
renderer digests, then restores the server-authored full compatibility receipt
transactionally with intake closed before ECS replacement. Stale/foreign receipts,
wrong configured candidate/evidence or third compatibility states mutate nothing.
Restore/verify all prior tasks/counts, then resume only if prior API was running.
A stopped prior API leaves intake closed for the next qualified deployment. If
compatibility restore is unconfirmed, do not change ECS. If any restore fails,
keep the workflow failed and inspect actual state; never claim rollback complete.
DB migrations retain existing compatibility/recovery
requirements; task rollback does not undo persistent writes. Bootstrap and ingest
may create records/objects before failure; retain failure evidence and reconcile
only known operation IDs under existing cleanup policy, never broad deletion.

Client rollback pairs previous source release with its matching retained numeric
client version and protected ARN. A stale version cannot qualify new client bytes.
If disabling new deployment, leave stopped/previously verified application state;
there is no silent external-runner fallback. Explicitly remove only regional stack
and caller policy/log group when retired. Retained versions survive ordinary
in-place replacement, but explicit retirement deletes the parent function and its
versions together; that cleanup approval must cover both. Never delete app data resources.

Cleanup already reclaimed6,604,954,068 bytes from14 duplicate/superseded Actions
artifacts; consolidated release, latest intermediate bundles, diagnostics and
failure evidence retained. Subsequent cleanup only targets newly proven duplicate
artifacts, preserving source-bound release/qualification evidence and run logs.
No public/support communication beyond repository owner status is authorized.

## Execution record

Design approved2026-10-21. Local implementation/verification in progress. No new
cloud resource, client configuration, deployment or live acceptance executed.
Final external approval, PR/run IDs, exact release/template, timestamps, outcomes
and observation results remain pending T007/T008.
