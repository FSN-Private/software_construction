# Rendering compatibility matrix

No renderer image or profile is approved until this record contains a reviewed immutable artifact digest, SBOM, license result, vulnerability result, corpus evidence, and named owner approval.

| Matrix | darktable release | Artifact digest | Profile | Corpus evidence | Status |
|---|---|---|---|---|---|
| `rendering-v1` | 5.6.1 | Candidate local image `sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb` (not registry-published) | `render-v1` | Four-state runtime candidate corpus passed on Ubuntu 24.04; [candidate evidence](../tests/rendering/corpus/candidate-evidence.md) | Blocked: immutable ECR digest and owner approval outstanding; Trivy SBOM/SARIF has no high/critical finding |

Changing any release, digest, profile, or output format creates a new matrix row. Existing derivatives retain their original provenance and are never relabelled.

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
| Approval | Owner name, UTC timestamp, and explicit statement: “Approve this exact digest for new `rendering-v1` derivatives.” |

Any change to the digest, profile, output encoding, or darktable release invalidates this record and requires a new matrix row and approval.
