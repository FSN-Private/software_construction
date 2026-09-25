# Candidate native corpus evidence

**Date**: 2026-09-24
**Runner**: configured Ubuntu 24.04 trusted Linux runner
**Renderer**: local `platform/darktable-renderer:5.6.1-runtime-candidate`, image `sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb`

`run-native.sh` verified the CR2 checksum, used the renderer's non-root UID against a disposable writable workspace, and produced four distinct 512px JPEG outputs.

| XMP state | XMP SHA-256 | Output SHA-256 | Bytes |
|---|---|---|---:|
| historical v1 | `11981740e80e6cae6d3e6210e2907ad0644599663b3d315f7a90dacf6838915c` | `d11c77875c9ef1865543fb3500027e276a86e576b6ba6982b7ab0c799481d0d2` | 89226 |
| historical v2 | `6c4d8f37fcb9648ac6ce4533552abbb1426c9ae221e799aa2e99a98c5d7db271` | `6842ff34bcaf55510929335b0c2a9887ff79d44752382d706d8b0cdfbcac2df3` | 89226 |
| historical v3 | `d5a223df26bd7a88ea69d14e631cb6946787f59d63a1b46421a64e6f2074b324` | `d1e1cc2341f1c3aa188e2899d0bd4462bd57313d2a555af1f4a9c4d4040e2ca7` | 89226 |
| current | `f5d91b0ac0a55b6ccc5e96d19967c414f0a355c695f9f69b55dfa985f62b85cb` | `9a24af59077301521628fb7022ae6e2bc5d834f8814ca81e2903817eabc054b5` | 89226 |

This is **not compatibility approval evidence**. The candidate is not registry-published. Its retained SBOM/CVE evidence has no high or critical finding, but records 48 medium and 18 low findings that require owner acceptance. The XMP fixtures were subsequently corrected to alter the effective exposure parameter; the table is retained only as historical candidate provenance and cannot validate the current fixtures. Reproduce the corpus with the approved ECR digest, retain the resulting provenance, and obtain owner approval before accepting reference derivatives.
