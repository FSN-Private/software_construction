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

## Rollout and rollback

1. Apply the additive migration and verify the migration ledger.
2. Deploy the worker with intake paused; verify it has no database/source-write credentials.
3. Enable a small owner-scoped current-preview canary and observe queue age, failure categories, cache reuse, duration, and private-object access for 30 minutes.
4. Enable normal intake only if cached and uncached targets hold and no unsafe failure data appears.

Abort on source mutation, cross-library access, provenance mismatch, unbounded queue age, or any critical/high dependency finding. Roll back by pausing intake and restoring the last approved matrix; do not delete completed derivatives or source/history data during rollback.
