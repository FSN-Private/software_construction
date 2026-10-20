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

## Approved bootstrap execution and reproduced T048 blocker

The owner approved bootstrap deployment and persistence of qualified fixture IDs.
At 13:49 UTC the agent dispatched
[deployment 39433716049](https://github.com/FSN-Private/software_construction/actions/runs/39433716049)
for the exact approved release. Manifest verification, bootstrap configuration,
database availability and image publication passed. ECR renderer digest
`sha256:903152c152debb8b4ae802a15ad345244cc66f46c6c95f1ee21054d9d7490ede`
binds the scanned archive's exact image configuration hash.

At 13:53:38 UTC native qualification failed `historical-v1.pfm` before task
registration, migration, service startup or fixture creation. The retained
`rendering-native-failure` artifact is ID 12269393053 (14-day retention).
No fixture-variable write was made; acceptance did not run. AWS inspection
confirmed services remained stopped on task definitions 6/4/1.

The captured candidate SHA-256 is
`1d370508d24a73c406cced938ed83b7bd64ee0207d61dc16458bcb5d49b58ed6`;
the approved reference is
`9a36b8507a7e6c6186e9ea85b168eaeb57e1eb4faa6ed0aa416c18617dd45e80`.
Both are 512 by 341 finite RGB PFMs, 2095120 bytes. Of 523776 floats, 15942
differ; maximum absolute difference is approximately 0.0000429153. This is
diagnostic measurement, not an accepted tolerance. The first different byte
is 204. The host was an Intel Xeon 6973P-C with two logical CPUs.

The exact failed image locally reproduces the approved reference on an AMD
Ryzen 4700U. Disabling several libc CPU features, changing reported host memory
from 2 to 32 GiB, and selecting default/AVX2/AVX-512 clones of the two color
conversion functions and highlight interpolation all still reproduce that
reference. The darktable CLI, core library, libc, libm, libjpeg and liblcms2
bytes match the previous release. No changed executable or those libraries
explains the new failure.

Pinned upstream source and released-binary inspection show compiler CPU clones
despite the generic package option, and approximate reciprocal instructions in
demosaicing. Neither observation establishes the failed comparison's cause.
A diagnostic native rebuild lacked the production build's OpenEXR exclusion
and could not execute in the hardened runtime; it is not qualification evidence.
A reciprocal-control demosaicing rebuild changes the local hash but does not
establish a stable cross-host result. These experiments remain ignored local
artifacts and are never proposed for deployment. References, finite checks,
full-file equality, latency limits and live services are unchanged.

## Prepared controlled runner diagnosis

`native-renderer-diagnostics.yml` is a read-only diagnostic workflow,
not a deployment or acceptance waiver. A push of its workflow/script to the
exact maintenance branch runs diagnosis of release 39397523975. This permits
the experiment without merging a new workflow into the default branch, which
GitHub requires for manual dispatch of a new workflow. The workflow also defines
manual inputs for use if it is subsequently retained on the default branch.
It verifies the selected run is a
successful default-branch `build-release.yml` workflow for the requested SHA,
then verifies the complete manifest and provenance before loading that exact
renderer archive. It runs baseline twice and once with libc CPU optimizations
disabled, using the real unchanged native corpus. Darktable's compiler clones
are separate from that libc setting. Every mismatch remains a workflow failure;
all cases run so their logs and first mismatching public-corpus PFM can be compared.
Only the bounded diagnostic directory is uploaded, for fourteen days.

The workflow has contents/actions read permissions, no protected environment,
no AWS credentials, no OIDC token, no deployment and no repository write. It
cannot qualify or activate a candidate or configure acceptance fixtures.
The script regression verifies all cases execute after a baseline failure,
only the intended case receives the libc tunable, a mismatch keeps a nonzero
result, all passing cases return zero and invalid revision input is rejected.

## Current status

Bootstrap configuration is understood and artifact cleanup is complete. The
approved deployment attempt reproduced T048 before testing the merged cache fix.
The subsequently approved diagnostic branch was published after both independent
reviews passed. Controlled run39439825637 now shows two AMD samples matching the
existing references and two Intel samples returning an identical alternate
eight-file set. The owner approved the exact reference extension under T055
on2026-10-20. Its maintenance implementation retains the original comparison
and permits only the eight approved additional hashes in explicit nonprod scope.
Source-bound runner verification and delivery remain in progress. No runtime
activation, deployment, fixture qualification, production promotion or
disaster-recovery success is claimed.

## First controlled diagnostic and cross-host follow-up

The owner explicitly approved diagnostic branch publication and execution.
Reviewed commit `97b1faf` was pushed to the named maintenance branch.
[Diagnostic 39438298299](https://github.com/FSN-Private/software_construction/actions/runs/39438298299)
verified the same release archive and passed baseline, baseline-repeat and
libc-baseline. This does not reproduce or invalidate the failed deployment:
the initial successful-run artifact did not record CPU identity (the existing
mismatch report records identity only on failure).

The upstream [cross-machine test discussion](https://github.com/darktable-org/darktable/issues/22589)
reports output differences between AMD and Intel generic builds and extra
differences from Release fast-math. That supports measuring CPU dependence;
it does not establish this deployment failure's cause or authorize a reference
change.

The bounded follow-up samples four independent GitHub runners, records CPU
vendor/model and exact source/image identity even on success, and retains
separate sample artifacts. If a comparison fails, it additionally renders all
eight finite public-corpus PFM candidates using baseline arithmetic, with the
same non-root/read-only/one-CPU/two-GiB/one-thread settings. Candidate collection
never turns the failed comparison green, and its manifest explicitly labels
the pixels unapproved. Eight dimension-bounded RGB PFMs total about143MB per
failing sample; retention remains fourteen days. No reference, runtime image,
deployment workflow, compatibility gate or latency criterion changes.

The real collector was exercised locally with the exact trusted release: all
eight dimension-bounded finite candidates matched the approved reference
hashes, totaling142467264bytes. Only Windows mount paths and checksum-file CR
handling were adapted for that local run. Script regression, full release
workflow suite, CI contracts, both shell syntax checks and whitespace pass.

Run39439825637 completed all four samples: AMD samples1/3 pass all three cases;
Intel samples2/4 fail all three first comparisons with the actual deployment's
same first candidate hash. Both complete Intel manifests are identical, and all
eight outputs have approved dimensions/lengths and finite pixels. The
[reviewable exact-reference proposal](../features/011-backup-disaster-recovery/reviews/intel-renderer-reference-proposal.md)
contains the candidate hashes, provenance, differences and display comparisons.
T055 requires an owner decision before changing accepted outputs; accepting
an observed small numerical difference automatically would violate that gate.

2026-10-20 updated-gate qualification: [run39478819138](https://github.com/FSN-Private/software_construction/actions/runs/39478819138), harness b97d90f, passes all four samples (two Intel Xeon Platinum8573C, AMD EPYC9V74 and AMD EPYC9V45) and baseline/repeat/libc-disabled full-corpus cases on each. Intel logs match all eight approved additional hashes; AMD retains the original set. Verified image/source are unchanged. The native blocker is reproduced and repaired on these hosts; master integration, source-bound CI/release/security and live bootstrap/latency/ingest acceptance are still pending. No qualified fixture IDs are saved.
