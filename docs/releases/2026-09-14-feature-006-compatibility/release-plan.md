# Feature 006 post-master compatibility release plan

Owner: project maintainer  
Date: 2026-09-14  
Feature: [006 XMP version-control core](../../features/006-xmp-version-control-core/spec.md)  
Branch: `feature/006-xmp-version-control-core`  
Implementation commit: `e791a74e657d45ba36dba02bdd9c0a52813f1249`

## Scope and prerequisites

This pull request merges `origin/master` through `640c1996f61a049e215e4456db60f0932c332c45` into Feature 006 and fixes compatibility with scheduled non-production shutdown. It adds no dependency and makes no destructive schema change. Migration 006 remains additive and was rehearsed against the merged runtime-role provisioning path.

Before merge, require the committed alternate-model pre-push review to report `PASS`, every required PR check to pass for the exact PR head, GitHub to report the branch mergeable, and the infrastructure preparation below to be complete. Abort on any unresolved blocking review finding, failed required check, merge conflict, unexpected remote-head change, unavailable AWS credentials, CDK drift beyond this reviewed change, or failure to preserve the current rollback pair.

## Pre-merge infrastructure preparation

Run these steps from the exact green PR head using an authenticated non-production AWS operator session. They are a hard merge gate because the merged workflows require the generated alarm name and permissions.

1. Read and record the current API and worker service task-definition ARNs and desired counts. Confirm any nonzero service is stable and healthy; these exact task definitions are the known-good rollback pair.
2. Run `cdk diff` with the approved non-production contexts and inspect it. Permit only the reviewed CORS headers, 2048 MiB API task, version-control alarm/output, scoped alarm IAM, and already-expected changes from merged master. Abort on replacement or deletion of retained database, buckets, keys, secrets, repositories, or unexpected network/identity changes.
3. Run `cdk deploy --outputs-file <protected-temporary-path>` with the same contexts. The new non-production heartbeat alarm starts with actions disabled, preventing a false page while CDK services use zero desired count.
4. Immediately update the API and worker services back to the task-definition ARNs and desired counts captured in step 1; wait for stability when either count is nonzero. Verify both services reference the captured task definitions so subsequent deployment rollback cannot select CDK's unrunnable `bootstrap` images.
5. Read `VersionControlHeartbeatAlarmName` from the protected outputs file and set only `AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM` in the GitHub `nonprod` environment. Delete the temporary output file after recording non-secret output evidence.
6. Verify the live API task family reports 2048 MiB, API Gateway CORS includes `if-match` and `x-client-id`, and the deployment role's alarm enable/disable policy is scoped to the generated heartbeat alarm. If the restored API desired count is nonzero, enable alarm actions and require the next integrity heartbeat before merge; otherwise leave actions disabled.

Observable preparation evidence is the reviewed CDK diff, successful stack event, captured/restored task-definition pair and counts, configured GitHub variable name, live CORS/task-memory/IAM values, and alarm action state matching API desired count. Do not include credentials, Cognito secrets, or XMP data in evidence.

## Rollout and observation

1. After the pre-merge infrastructure gate passes, merge the approved PR into `master` using the repository's normal GitHub merge method. Observable result: GitHub reports the PR merged and the resulting `master` SHA.
2. Observe `Continuous integration` and `Build release evidence` for that SHA. Success requires every required job to conclude `success` and immutable release evidence to be produced for the same source revision. Abort deployment follow-up if either workflow fails or targets another SHA.
3. Observe `Deploy non-production`, which is triggered only by successful release evidence. Success requires the protected job to conclude `success`, the migration task to complete, both ECS services to stabilize at desired count one on the candidate task definitions, `/live` and authenticated `/ready` to pass, the ingest smoke test to pass, deployment evidence to upload, and version-control heartbeat alarm actions to be enabled.
4. Observe for one additional integrity interval (15 minutes maximum). Success requires no `version-control.integrity.failure`, the missing-heartbeat alarm not to transition to `ALARM`, and no automatic paired rollback evidence.

No production promotion or tag creation is authorized by this plan.

## Rollback and recovery

If CI or release evidence fails, do not deploy; fix forward through a new reviewed PR. If non-production deployment fails, rely on the deployment script's paired ECS rollback and retain all additive database objects and lifecycle holds. Do not drop `vc_*` tables or delete retained storage. If healthy services are running but heartbeat actions were not restored, run `aws cloudwatch enable-alarm-actions --alarm-names "$AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM"` through the protected operator path, then verify the next heartbeat interval.

For a post-deployment application regression, revert the PR through a separately reviewed commit while retaining migration 006 data. The rollback owner is the project maintainer. Escalate immediately on integrity failure, missing immutable content, an unexpected task definition, or a health failure lasting five minutes after ECS reports stable.

## Evidence record

- Local compatibility validation: [Feature 006 validation](../../features/006-xmp-version-control-core/validation.md#post-master-compatibility-evidence--2026-09-14)
- Pre-push review: [Feature 006 review record](../../features/006-xmp-version-control-core/reviews/pre-push-review.md)
- Pull request, merge SHA, workflow run IDs, and final deployment outcome: record in the PR and GitHub workflow history during execution.
