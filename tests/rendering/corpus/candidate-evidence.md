# Candidate native corpus evidence

**Date**: 2026-09-23
**Runner**: configured Ubuntu 24.04 trusted Linux runner
**Renderer**: local `platform/darktable-renderer:5.6.1-candidate`, manifest `sha256:01b582e0f7ac9446c7911007db985dccb5d11e058cfee1f6dfa5a304a59ce362`

`run-native.sh` verified the CR2 checksum, used the renderer's non-root UID against a disposable writable workspace, and produced four distinct 512px JPEG outputs.

| XMP state | XMP SHA-256 | Output SHA-256 | Bytes |
|---|---|---|---:|
| historical v1 | `11981740e80e6cae6d3e6210e2907ad0644599663b3d315f7a90dacf6838915c` | `7d08e3c36914cdbf9be96d3be5c2b0950cb0a604083328ec3869a7081fa26cad` | 89226 |
| historical v2 | `6c4d8f37fcb9648ac6ce4533552abbb1426c9ae221e799aa2e99a98c5d7db271` | `4b461a9483602810eec45cb95d711a2042ad55c3483b866fae06963a55ec02eb` | 89226 |
| historical v3 | `d5a223df26bd7a88ea69d14e631cb6946787f59d63a1b46421a64e6f2074b324` | `15fd2f9f469ea34eac0c36b9e97831e72437b487351efed3fc93f9b5000ae74f` | 89226 |
| current | `f5d91b0ac0a55b6ccc5e96d19967c414f0a355c695f9f69b55dfa985f62b85cb` | `f78f51451bb667af159ee43069fca7dbad1d155b163dbba1d31dcea9bc3a1c33` | 89226 |

This is **not compatibility approval evidence**. The candidate is not registry-published and does not yet have a completed SBOM/CVE report. Reproduce the corpus with the approved ECR digest, retain the resulting provenance, and obtain owner approval before accepting reference derivatives.
