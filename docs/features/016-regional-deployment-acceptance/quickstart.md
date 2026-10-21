# Local verification and gated rollout

Run `pwsh -NoProfile -File scripts/package-regional-acceptance.ps1`, then the
regional client/packaging/invoker tests through `npm run test:release-evidence`.
Run infrastructure type/build and regional synthesis/IAM contract checks using
the generated zip. These use local fixtures; never claim cloud qualification.

After independent review and explicit publication authority: PR CI, merge,
master CI, exact release packaging/provenance/zip verification. With separate
concrete provisioning approval, synth/diff/apply only the regional client stack
using that exact zip and expected role/origin/account. Verify version metadata,
then configure AWS_NONPROD_ACCEPTANCE_FUNCTION_VERSION. Authorized bootstrap must
pass native, readiness, regional rendering/ingest, activation and evidence upload.
Only then save actual qualified fixture IDs. Failures invoke existing rollback.
