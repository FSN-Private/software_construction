# Feature 008 release plan

**Status**: Draft — no deployment authorized.

## Prerequisites

- All Feature 008 tasks, local tests, native corpus evidence, compatibility approval, and review gate pass.
- The image is digest pinned with SBOM, license, and vulnerability evidence.
- Database migration rehearsal, queue/DLQ permission review, derivative retention setting, and rollback drill pass.
- The non-production destination is verified as account `344531454551`, region `ap-southeast-1`, with the short-lived `software-construction-nonprod` AWS CLI profile. The renderer repository is created only through the reviewed CDK stack as `platform-nonprod-render-worker`.

## Renderer bootstrap sequence

1. Authenticate with `aws login --profile software-construction-nonprod --region ap-southeast-1` and record only the account ID returned by `aws sts get-caller-identity --profile software-construction-nonprod`; do not retain credentials in evidence.
2. Before deployment, inspect `cdk diff` for the approved renderer repository, task role, queue, storage, and observability changes. Abort on a replacement/deletion of retained resources or a wider IAM grant than the reviewed renderer boundary.
3. After explicit authorization for the external change, deploy with a protected outputs file. Confirm the repository has immutable tags and scan-on-push enabled, then record the renderer repository output for the non-production GitHub environment.
4. Publish only the source-verified, SBOM/CVE-reviewed image and record its registry digest. A tag or a local OCI manifest alone is insufficient.
5. Run the CC0 corpus: the existing `tests/fixtures/ingest/sample.dng` with its current XMP and three historical XMP revisions. Retain per-rendition input/XMP/output hashes, dimensions, renderer digest, and trusted-runner ID.
6. Obtain the explicit compatibility approval in [the compatibility matrix](../../rendering-compatibility.md) before enabling new renderer intake.

## 2026-09-23 read-only non-production preflight

- The short-lived `software-construction-nonprod` session resolved to `arn:aws:iam::344531454551:user/platform-admin` in account `344531454551`.
- `PlatformFoundation-nonprod` is `UPDATE_COMPLETE` (last update 2026-09-14). Its existing API and ingest-worker repositories are immutable and scan on push. The renderer repository is absent, as expected before this CDK change is deployed.
- CDK's template-only comparison against the live stack reports exactly one new `AWS::ECR::Repository`, the `RendererRepositoryName` output, and deployment-role permissions scoped to that repository (`ecr:DescribeImages` and pull/push actions). It reports no storage, database, queue, network, task, service, or replacement change.
- No deployment ran and no CloudFormation change set remains. Creating the repository, updating the deployment role/output, and updating protected GitHub configuration still require explicit external-mutation authorization.

## 2026-09-23 approved registry deployment

- The owner explicitly approved the reviewed non-production CDK diff. CloudFormation updated `PlatformFoundation-nonprod` from 15:40:45 UTC and reached `UPDATE_COMPLETE`.
- The new repository is `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker`. Post-deployment verification confirmed immutable tags and scan-on-push enabled.
- No renderer image was published, no ECS task/service was changed, and no GitHub environment variable or secret was changed. Image publication remains blocked on T003 security/corpus evidence; the protected GitHub configuration change remains separately approval-gated.

## Rollout and rollback

1. Apply the additive migration and verify the migration ledger.
2. Deploy the worker with intake paused; verify it has no database/source-write credentials.
3. Enable a small owner-scoped current-preview canary and observe queue age, failure categories, cache reuse, duration, and private-object access for 30 minutes.
4. Enable normal intake only if cached and uncached targets hold and no unsafe failure data appears.

Abort on source mutation, cross-library access, provenance mismatch, unbounded queue age, or any critical/high dependency finding. Roll back by pausing intake and restoring the last approved matrix; do not delete completed derivatives or source/history data during rollback.
