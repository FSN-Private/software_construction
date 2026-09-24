# Darktable 5.6.1 runtime-candidate supply-chain evidence

**Generated**: 2026-09-24 on the configured Ubuntu 24.04 trusted runner
**Local image**: `platform/darktable-renderer:5.6.1-runtime-candidate` / `sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb`
**Scope**: local pre-publication evidence and a digest-qualified scan of the published non-production ECR image; neither is compatibility approval.

The Dockerfile verifies the official darktable 5.6.1 archive SHA-256 before building. Trivy 0.74.0 was downloaded from its official release only after its archive SHA-256 matched `2ae6fe3ee734b7fdf11335663e18c75ea12dccc76062f09f164a3b0f8be4371a`.

| Evidence | SHA-256 | Result |
|---|---|---|
| `darktable-5.6.1-runtime-candidate.cdx.json` | `8b8d0eb28ef2cf84657aca0ce36493ed641820d2433a3b8ff167602168807890` | CycloneDX 1.7 SBOM with 294 components |
| `darktable-5.6.1-runtime-candidate.sarif` | `6e4421e56c5d05e88f915b9b9c8a522d44feb2a0c0918c3ae7e38dab646b2b7d` | 0 critical, 0 high, 48 medium, and 18 low unique findings |
| `darktable-5.6.1-ecr.cdx.json` | `7164a479a6feb811fa2e8f47f49ba56b9f1202fbfb1d48b1e1e581f474ce78e5` | CycloneDX 1.7 SBOM scanned from the published ECR digest, with 294 components |
| `darktable-5.6.1-ecr.sarif` | `ceb04af6e6042a87e75b3b3e9c5b5ae51a194b5718e63c7cdc25931f56227a38` | 0 critical, 0 high, 48 medium, and 18 low unique findings scanned from the published ECR digest |

The security gate permits no unresolved critical or high finding. Fredrik Snårbacka accepted the remaining 48 medium and 18 low findings for the exact digest's non-production use at `2026-09-23T17:32:00Z`. Production use is prohibited until a replacement digest eliminates all 66 accepted findings and a digest-qualified scan proves that result.

Reproduce from the repository root after building the exact local tag:

```bash
TRIVY_CACHE_DIR=/tmp/trivy-008-cache /path/to/trivy image --scanners vuln --format cyclonedx --output docs/features/008-darktable-rendering/evidence/darktable-5.6.1-runtime-candidate.cdx.json platform/darktable-renderer:5.6.1-runtime-candidate
TRIVY_CACHE_DIR=/tmp/trivy-008-cache /path/to/trivy image --scanners vuln --format sarif --output docs/features/008-darktable-rendering/evidence/darktable-5.6.1-runtime-candidate.sarif platform/darktable-renderer:5.6.1-runtime-candidate
```

The image is published as `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker@sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb`. Its ECR-digest corpus evidence is [recorded with the corpus](../../../../tests/rendering/corpus/ecr-evidence.md). It is approved for non-production derivatives only; production is prohibited by [ADR-0009](../../../architecture/decisions/0009-production-renderer-vulnerability-gate.md).

## Hardened renderer evidence

The pending non-production renderer is `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker@sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b`.
It disables unused OpenEXR support after the previous image’s four high-severity
OpenEXR findings. Its digest-qualified Trivy 0.74.0 evidence is:

| Evidence | SHA-256 | Result |
|---|---|---|
| `darktable-5.6.1-hardened-ecr.cdx.json` | `d46a65eb5695012d6ce62bbfbc8247945f5a4e93ea9b615583037e5afc5ee3fb` | CycloneDX SBOM (817,028 bytes) |
| `darktable-5.6.1-hardened-ecr.sarif` | `9076faf4f29657da5d0f845d7e17fdc47c9987f685d2dcb649d2ef37e356552f` | 0 critical, 0 high, 5 medium findings |

The owner accepted the five medium findings for controlled non-production validation.
The digest remains `pending` and cannot serve derivatives until T026 retains
trusted-Linux corpus and HTTP acceptance evidence; production remains blocked.
