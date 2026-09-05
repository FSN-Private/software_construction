# Deployment and recovery runbook

Owner: platform maintainer. This runbook applies only after the GitHub `nonprod` and `production` environments, the listed AWS OIDC roles, and repository variables have been explicitly bootstrapped and verified by T010/T032.

## Required configuration

Set repository variables, never secrets, for the approved region, the environment-specific OIDC role ARN, ECR registry and API repository, ECS cluster/service/task-definition/container name, and HTTPS health URL. The workflow names are `AWS_REGION`, `AWS_<ENV>_ROLE_ARN`, `AWS_<ENV>_ECR_REGISTRY`, `AWS_<ENV>_API_REPOSITORY`, `AWS_<ENV>_ECS_CLUSTER`, `AWS_<ENV>_ECS_SERVICE`, `AWS_<ENV>_ECS_TASK_DEFINITION`, `AWS_<ENV>_ECS_API_CONTAINER`, and `AWS_<ENV>_HEALTH_URL`, where `<ENV>` is `NONPROD` or `PRODUCTION`.

The roles must trust only this repository, the intended workflow/environment, and the GitHub Actions audience. Grant only ECR push/pull and the named ECS task-definition/service operations required by the corresponding workflow. Do not add static AWS keys or place runtime configuration in the manifest.

## Normal delivery

1. Merge a revision into protected `master` after `ci / required` succeeds. `Build release evidence` creates the four locked build artifacts, checksums, SPDX SBOM, provenance attestation, CI-validation record, and release manifest for that exact SHA.
2. `Deploy non-production` verifies every manifest digest before publishing the API image by registry digest, updates the named ECS service, waits for stability, and requires `/live` and `/ready` to succeed. Its retained evidence is the authoritative non-production record.
3. Create a `v*` tag pointing to that validated `master` SHA. `Promote production release` pauses at the protected `production` environment approval, verifies the tag ancestry and manifest, then promotes the same release evidence. It records the source SHA, manifest artifact checksum, registry digest, task definition, tag, and approval-protected workflow run.

Abort immediately if any digest check, OIDC role assumption, ECS stability wait, or health probe fails. Do not rerun against a different revision or rebuild an artifact to bypass a failed gate.

## Failed deployment and rollback

If a production health probe fails, keep the failed task definition and workflow log as incident evidence. Identify the latest named healthy task definition whose retained release manifest has `compatibility.rollbackAllowed: true`. Start `Roll back compatible production release` with that release-evidence run ID, exact task-definition ARN, and incident reference. The protected production approval is required again. The workflow verifies checksums and compatibility, updates only to the supplied prior task definition, waits for ECS stability, and retests `/live` and `/ready`.

Rollback is refused when the manifest is missing, checksum verification fails, compatibility is false, the target is unnamed, or health fails. In that case use an approved forward-recovery plan: contain traffic, preserve the persistent state and evidence, prepare an expand/migrate/contract-compatible corrective release, obtain production approval, and record the data-reconciliation result. Never report persistent state as rolled back merely because application tasks were restored.

## Time and escalation thresholds

The target is completion or safe refusal within 30 minutes. Escalate to the platform maintainer immediately for failed OIDC, evidence mismatch, an unavailable prior healthy task definition, or any health failure lasting five minutes after ECS reports stable. Treat leaked credentials or a manifest/artifact mismatch as a security incident; stop deployment and rotate/revoke through the credential-compromise runbook when it is published in T028.
