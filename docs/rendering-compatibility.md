# Rendering compatibility matrix

## 2026-10-20 exact Intel corpus reference approval

The owner explicitly approved the [eight additional Intel hashes](features/011-backup-disaster-recovery/reviews/intel-renderer-reference-proposal.md)
for controlled non-production qualification. Existing reference PFMs remain
unchanged; the gate also accepts only the fixture-specific exact SHA-256 in
`tests/rendering/corpus/reference/intel-reference-hashes.json`. Finite/size
validation remains mandatory and every other output fails. There is no numeric
tolerance or learned baseline. The approved candidate manifest, two independent
Intel captures, AMD comparisons and display evidence remain linked in the
proposal. The compressed historical-v1 test fixture is one of those exact
approved candidates, not a new renderer input or dependency.

This reference decision does not activate a runtime compatibility row, approve
new vulnerabilities or qualify a deployment. Exact-source CI/artifact/security,
complete native checks and real HTTP/ingest/latency acceptance are still required.
Production eligibility and all existing isolation/provenance rules are unchanged.

No renderer image or profile is approved until this record contains a reviewed immutable artifact digest, SBOM, license result, vulnerability result, corpus evidence, and named owner approval.

| Matrix | darktable release | Artifact digest | Profile | Corpus evidence | Status |
|---|---|---|---|---|---|
| `rendering-v1` | 5.6.1 | `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker@sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b` | `render-v1` | [Hardened SBOM/SARIF evidence](features/008-darktable-rendering/evidence/README.md) | Pending trusted-Linux corpus and HTTP acceptance (T026); intake is fail-closed. |

The active matrix version has one approved digest. Replacing it requires a new additive migration, immutable evidence, and owner approval; existing derivatives retain their original provenance and are never relabelled.

Feature 009 consumer validation: the local `platform/darktable-renderer:5.6.1-runtime-candidate` successfully rendered a supported JPEG using gallery-patched XMP; the XMP checksum and unrelated `history_modversion` were preserved. This checks the gallery edit/renderer handoff only. It does not change the `rendering-v1` matrix status or replace the trusted Linux corpus and HTTP acceptance required by T026. See [feature 009 validation](features/009-gallery-metadata-search/validation.md).

## Prepared corpus input

The native corpus uses `tests/rendering/corpus/IMG_9406.CR2`, the CC0 Canon EOS 1000D sample from raw.pixls.us. Its published SHA-256 is `08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c`; [the corpus provenance](../tests/rendering/corpus/PROVENANCE.md) records its source and license.

Do not replace this file with a private photograph or an unlicensed download. The native corpus must retain one current and three distinct historical darktable XMP revisions for this exact input, then record for every rendition:

- the input and XMP SHA-256 values, version identity, output dimensions, output SHA-256, matrix, renderer image digest, and trusted-runner run identifier;
- the generated XMP content and reference derivatives in `tests/rendering/corpus/`; and
- a review that the four outputs are meaningful and that each historical request resolved its selected XMP rather than a branch head.

## Non-production registry preparation

The target is the existing non-production account `344531454551` in `ap-southeast-1`. The renderer repository is deliberately a CDK-managed resource, not a console-created repository: `platform-nonprod-render-worker`. It must use immutable tags, scan on push, retention on stack removal, and a separate output/CI variable. The deployment role may push the reviewed digest; the renderer runtime role may only pull that digest. No source-storage, database, or GitHub credential may be added to the renderer role.

Before any external mutation, authenticate with short-lived browser credentials and prove the destination:

```powershell
aws login --profile software-construction-nonprod --region ap-southeast-1
aws sts get-caller-identity --profile software-construction-nonprod
aws ecr describe-repositories --region ap-southeast-1 --repository-names platform-nonprod-api platform-nonprod-ingest-worker
```

The expected account ID is `344531454551`. A missing renderer repository is expected until the reviewed CDK change is deployed. Do not use static access keys or create the repository manually. CDK deployment, image publication, and GitHub environment updates remain separately approval-gated external actions.

## Approval checklist

1. Verify the release artifact comes from the documented upstream source and record its immutable digest and license.
2. Generate an SBOM and record the vulnerability scan result; critical/high findings block approval.
3. Run the current RAW plus three historical XMP corpus on the trusted Linux runner and retain checksums, dimensions, and provenance.
4. Verify malformed, missing, unsupported, timeout, and workspace-cleanup outcomes expose only safe codes.
5. Obtain the library owner’s explicit approval before changing the `approved` compatibility state.

## Owner approval record

Fill this only after every checklist item has immutable evidence. A local Docker manifest is not an eligible image digest.

| Field | Required value |
|---|---|
| Matrix and profile | `rendering-v1` / `render-v1` |
| Release and source evidence | 5.6.1, official source archive SHA-256, GPL-3.0-or-later review |
| Registry image | `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker@sha256:<digest>` |
| Security evidence | SBOM reference and completed CVE report; no unresolved critical/high finding |
| Corpus evidence | Trusted-runner run URL/ID and the four XMP/output provenance records |
| Approval | Product owner, 2026-09-24T12:59:25Z: accepts 5 medium findings for controlled validation only. `pending` prevents new derivatives until T026 succeeds. |

Any change to the digest, profile, output encoding, or darktable release requires an additive migration and new approval evidence before the active matrix row changes.

## Recorded approval

The product owner accepted 5 medium findings for controlled validation of `sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b` at 2026-09-24T12:59:25Z. It is not approved for new derivatives until post-merge T026 acceptance succeeds.

## 2026-10-16 corrected corpus approval

The owner explicitly approved [the portable reference/risk proposal](features/011-backup-disaster-recovery/reviews/portable-renderer-reference-proposal.md) at2026-10-16T11:29:25Z: exactly eight corrected finite exposure references and the documented46medium/26low findings for controllednonprod validation only. The old exposure-v5 inputs used wrong parameter fields; both historical-v3 references were entirelyNaN. Corrected mode0/black0/EV fields, finite-pixel validation and strict full-file comparison now bind meaningful outputs. Original bytes remain in Git history.

The local portable/security-patched candidate f26249d6 has0critical/high findings and reproduces those approved reference hashes. Its [SHA-bound candidate evidence](features/011-backup-disaster-recovery/evidence/portable-renderer-candidate-2026-10-16/candidate-manifest.json) records the exact lower-severity set. This approval permits the specified controllednonprod rollout only after independent review,current-source CI,digest-qualified trusted release/security/corpus evidence and real five-fixture HTTP/latency/ingest acceptance. New/unaccepted findings require assessment and critical/high findings block. Keep the runtime matrix pending until that evidence is recorded; no production or independent recovery qualification is established by the local approval.
