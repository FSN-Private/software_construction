# ECR-digest native corpus evidence

**Date**: 2026-09-24
**Runner**: configured Ubuntu 24.04 trusted Linux runner
**Renderer**: `344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-nonprod-render-worker@sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb`

The runner pulled the exact ECR digest and `run-native.sh` verified the CC0 CR2 checksum before rendering all four XMP states as the renderer's non-root UID in a disposable workspace. Each output is a non-empty 512px JPEG; the four output hashes are distinct. Darktable embeds run-specific metadata in JPEG output, so the recorded hashes prove this exact run's provenance and distinct states rather than serving as a byte-for-byte cross-run comparator.

| XMP state | XMP SHA-256 | Output SHA-256 | Bytes |
|---|---|---|---:|
| historical v1 | `11981740e80e6cae6d3e6210e2907ad0644599663b3d315f7a90dacf6838915c` | `db50fefadf03a385f642d43d5e2ce342ce22884fdcbac55ce779cb003767ca4d` | 89226 |
| historical v2 | `6c4d8f37fcb9648ac6ce4533552abbb1426c9ae221e799aa2e99a98c5d7db271` | `35fd43093524a3c3d01bdc9864e380fc92fed213fe1b01e3bc861a44fc92bee2` | 89226 |
| historical v3 | `d5a223df26bd7a88ea69d14e631cb6946787f59d63a1b46421a64e6f2074b324` | `3eda2fc66eeaa3d9773306a1e84f84dcea783ccea4288d67abd65a3914d65a6e` | 89226 |
| current | `f5d91b0ac0a55b6ccc5e96d19967c414f0a355c695f9f69b55dfa985f62b85cb` | `00f5dd61687330cbd4874e0293dd3054a26c107047bc9ff8fe78e30c735d2e0f` | 89226 |

This satisfies the digest-qualified native evidence prerequisite. It does not itself approve the release; the owner must explicitly accept the recorded 48 medium and 18 low findings for this exact digest.
