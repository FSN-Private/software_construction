# Non-production deployment blocked before bootstrap

Date: 2026-10-20. Owner: platform maintainer. Related work: Feature 011
T025/T038/T048/T051/T052/T054 and Feature 008 rendering acceptance.

## Impact and confirmed causes

No successful current-release deployment is established. At 13:32 UTC, AWS
reported API/ingest/renderer desired and running counts of zero, on task
definitions 6/4/1. RDS was available with seven-day backup retention.

The [manual bootstrap deployment 39390207398](https://github.com/FSN-Private/software_construction/actions/runs/39390207398)
used revision `3ac497885fdb534648d03974e1acd29b2ccda504`. It passed native
comparison, migration, service stability, fixture creation and five uncached
renders. Cache-hit p95 was 1582.3 ms against the unchanged 500 ms criterion;
acceptance failed and rollback stopped the services. This establishes that the
earlier input-staging/thread fixes reached the real workload, but does not
establish full release qualification.

The cache repair subsequently merged in PR #44. All required checks in
[master CI 39396072375](https://github.com/FSN-Private/software_construction/actions/runs/39396072375)
and [release build 39397523975](https://github.com/FSN-Private/software_construction/actions/runs/39397523975)
passed for revision `acc789ca0a7f16fc140b97a69418a42d84909dbd`.
The [automatic deployment 39399276343](https://github.com/FSN-Private/software_construction/actions/runs/39399276343)
then failed configuration validation at 09:15 UTC:
`Missing required deployment setting: RENDERING_ACCEPTANCE_FIXTURES`.
The protected environment has no `AWS_NONPROD_RENDERING_ACCEPTANCE_FIXTURES`
variable. Automatic runs do not opt into the existing manual bootstrap mode.
Consequently this run never exercised the cache repair or changed AWS services.

Earlier failures and their repairs remain recorded in the
[release plan](../releases/2026-10-15-feature-011-nonprod/release-plan.md).
The T048 native mismatch's cause and a historical startup callback 500 remain
unresolved; subsequent native passes do not establish their causes.

## Prepared recovery

The existing reviewed deployment supports the required recovery; another code
change is not yet supported by the evidence.

1. After explicit deployment approval, dispatch `deploy-nonprod.yml` on `master`
   with `release_run_id=39397523975` and `bootstrap_rendering_fixtures=true`.
   Reconfirm master still matches the source revision before dispatch. This
   invokes the normal manifest verification, native gate, migration, service
   cutover, fixture creation, rendering/ingest acceptance and rollback path.
2. Require workflow success and retrieve `nonprod-deployment-evidence`. Verify
   `nonprod-deployment.json` binds the exact revision and release run and includes
   successful rendering and ingest results. Require the existing 2000 ms start,
   30000 ms completion and 500 ms cache thresholds; do not substitute retries,
   generated fixtures alone or a healthy `/live` response for acceptance.
3. Only after that success, save `.fixtures` from the qualified
   `rendering-fixtures.json` as the protected nonprod variable
   `AWS_NONPROD_RENDERING_ACCEPTANCE_FIXTURES`. Verify it contains at least five
   distinct library/asset/edit tuples. This completes the initial configuration
   required by later automatic deployments. Failed-run fixtures are unqualified.
4. Verify the intended task definitions are running, public `/live` succeeds,
   authenticated readiness and both smoke results passed, and heartbeat alarm
   actions were restored. Record the actual run, image digest and latency values
   in the release plan. Preserve configured daily nonprod shutdown.
5. If a gate fails, retain diagnostic evidence, verify paired rollback and
   investigate the actual failure before selecting another change. Never relax
   the acceptance criteria to obtain a green deployment.

The archive/provenance check on the exact trusted renderer passed locally:
763433472 bytes, archive SHA-256
`6413681ab1040aace6fa024934d6389d93b83e1c2cbe6fbe8700957fa6f8d284`,
image configuration SHA-256
`683e2ba097ecd79b3a01472f8b30f7d77db3f9499efda31907f16cea0c6446ea`.
The 13:32 UTC Trivy scan found zero critical/high and the exact owner-accepted
46 medium/26 low unique findings; no additional ID/severity pair was present.
These checks do not qualify deployed performance or production promotion.
Evidence is retained locally under ignored
`artifacts/nonprod-renderer-setup/trusted-release-39397523975/`.

## Actions artifact cleanup completed

Removed fourteen superseded intermediate artifacts: seven
`linux-release-artifacts` and seven `windows-release-artifact` uploads from
release runs 38855104337, 38957831348, 39013396051, 39017695531, 39039125785,
39043721217 and 39100497886. Each has a retained consolidated `release-evidence`
bundle containing the same release outputs. Reclaimed 6,604,954,068 bytes.

Preserved all nine consolidated release bundles, the latest two builds'
intermediate uploads, the older standalone Windows upload without a retained
consolidated bundle, all workflow logs/run history, and the existing
`nonprod-deployment-evidence`. The post-cleanup API inventory reports fifteen
artifacts totaling 10,804,277,327 bytes. Local before/after evidence and deleted
IDs are under ignored `artifacts/deployment-diagnosis/`.

## Current status

Diagnosis, archive/security verification and artifact cleanup are complete.
Bootstrap deployment and qualified fixture-variable persistence await the
explicit approval requested in this session. No successful deployment,
production promotion or disaster-recovery qualification is claimed.
