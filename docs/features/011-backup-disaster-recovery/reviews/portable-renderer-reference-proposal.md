# Portable renderer reference and nonprod risk proposal

Status: **APPROVED FOR EXACT REFERENCE REPLACEMENT AND CONTROLLED NONPROD RISK ACCEPTANCE**. Runtime artifact qualification and active compatibility approval remain conditional on the gates below.

The corpus required square dimensions although the references preserve aspect ratio. Its authored exposure-v5 sidecars wrote EV values into mode/black fields; both historical-v3 references are entirelyNaN. The release renderer also used build-host CPU targeting and exits132 on local RAW export. The portable rebuild fixed startup but exposed stale runtime security findings. T040-T043 correct these defects; the new finite, meaningful output requires explicit reference approval.

Proposed decision: replace the eight corpus PFMs with the exact corrected candidate hashes below, and accept the documented46medium/26low findings solely for controlled non-production validation. Keep strict full-file pixel equality, structure/finite checks, immutable digest/source binding, and every release/HTTP/latency/ingest gate. No invalid-pixel, critical/high or production waiver.

Alternative: retain current references and keep deployment blocked pending another compatibility strategy. The current all-NaN reference images cannot qualify a corrected renderer.

The candidate source RAW remains the same CC0 Canon sample, SHA256 `08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c`. Manual mode0/black0 are fixed and only exposure varies(-1,-0.5,+0.5,+1EV); DNG acceptance adds true0EV. This corrects the documented intent, so changes from the old malformed fixtures are substantial rather than merely rounding noise.

| Candidate | Dimensions | Corrected XMP SHA256 | Proposed PFM SHA256 |
|---|---|---|---|
| historical-v1.pfm | 512 341 | `ef26ab13fa24119801441f9499178ae5d36ac14a27c72b6d6d151f5b195b1cef` | `9a36b8507a7e6c6186e9ea85b168eaeb57e1eb4faa6ed0aa416c18617dd45e80` |
| historical-v1-2048.pfm | 2048 1364 | `ef26ab13fa24119801441f9499178ae5d36ac14a27c72b6d6d151f5b195b1cef` | `c33280f598d2e168873e732d482f24f01538ac01851cfc60c9b835599f06fb4c` |
| historical-v2.pfm | 512 341 | `b441d5aa57cdf26b8061a376fd7d5f4a37cb1dd7893bd68b44c0d51648807d60` | `7055901d676f6e002bd40772cf8ffe90d0d03695c5e61211ee2caeafe8ba81a5` |
| historical-v2-2048.pfm | 2048 1364 | `b441d5aa57cdf26b8061a376fd7d5f4a37cb1dd7893bd68b44c0d51648807d60` | `be682c8c7489ecd7d96b503a1565491380d5c23a51dee2da5765cd220d5ae577` |
| historical-v3.pfm | 512 341 | `487df61f0f1bb2f2d152a645e664ff8ee22a1ddeb2cef57858a9739d3c5f6e08` | `63e003ebdb9a7bc6ab1be7e5a94a8db20fbd3aa8c560e3d273ca32a8816dc93f` |
| historical-v3-2048.pfm | 2048 1364 | `487df61f0f1bb2f2d152a645e664ff8ee22a1ddeb2cef57858a9739d3c5f6e08` | `1f62491de0d20a01cbb07d3e4f127e7468b5324696b2d6f17324bd6e53865b30` |
| current.pfm | 512 341 | `cd71530681a25f901f146021c9b3b324c6261c791ca1221fd510f8de17dba092` | `02ab5fad75c3da9e480aa81e346235b4e0c261e3eb4d58e09c773077e21292b5` |
| current-2048.pfm | 2048 1364 | `cd71530681a25f901f146021c9b3b324c6261c791ca1221fd510f8de17dba092` | `8da1d6cd35ed4a6e6c967810a690e3ed430f379c27f67f8137fa89a28998dbce` |

Patched local image: `platform-render-worker@sha256:f26249d63bbb3f93ac3cd59b700da359db28efcfc20554f615f2b381b4b39e1f`; config `sha256:a4297c48e60afc37f9f21dd4dd2c30d39c09bb886462f76d52536c176363edba`. Official darktable5.6.1 archive pin remains `e8b84ac98b0b689a244e4036c4b56394c1d58ce2d9abc05e0a060ef9f756dc36`, generic package-build flags verified. Same-Noble SDK10.0.402/runtime10.0.13 and OpenSSL3.0.13-0ubuntu3.16 remove the initial2critical/11high findings. No hostSDK/global, NuGetlock, sourcelicense or direct application dependency change.

Completed local validation: all eight finite/nondegenerate/distinct PFMs reproduce byte-for-byte with the patched image against proposed candidates; five exact DNG helper states render finite/distinct outputs with networknone/1CPU/2GiB; actual owner HTTP/PG/S3/worker fixture import/publication/version-control passes; malformed/truncated/oversized/NaN/infinity/one-byte corruption rejection passes; API/ingest/recovery container builds pass. These are local candidate checks: the unchanged approved-reference gate remains BLOCKED.

Fresh Trivy0.74.0/current2026-10-16 DB scan: **0critical,0high,46medium,26low unique renderer findings**. API/ingest scans also have0critical/high. Remaining renderer reports include memory-safety, denial-of-service and TLS/HTTP issues in bundled OS/media/network libraries; processing hostile inputs can expose such components. Trivy supplies no fixed package version for those remaining findings. Controlled test inputs and existing isolation limit exposure but do not prove non-exploitability. The earlier five-medium acceptance cannot authorize this changed set.

The [candidate manifest](../evidence/portable-renderer-candidate-2026-10-16/candidate-manifest.json) binds exact lower-severity CVE IDs, inputs, outputs, image identity, SBOM, complete vulnerability report and native logs by SHA256. Those72 findings need explicit owner acceptance for controllednonprod only. Any new finding requires assessment; critical/high findings always block. Source [Microsoft security release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) and MCR immutable manifests establish the patch provenance.

Review previews: [EV-1](../evidence/portable-renderer-candidate-2026-10-16/historical-v1.jpg), [EV-0.5](../evidence/portable-renderer-candidate-2026-10-16/historical-v2.jpg), [EV+0.5](../evidence/portable-renderer-candidate-2026-10-16/historical-v3.jpg), [EV+1](../evidence/portable-renderer-candidate-2026-10-16/current.jpg). All show the same meaningful landscape with ordered exposure changes; existing source/license is retained.

Approval would authorize adopting exactly these reference bytes and continuing the already-authorized push/nonprod rollout after final independent review. Current-head CI, trusted-Linux build/corpus evidence, digest-qualified security evidence for the exact release artifact with no additional/unaccepted findings, deployed five-fixture latency acceptance and ingest smoke must still pass before activation. Record the actual registry digest and evidence; a local Docker manifest alone is not an approved compatibility row. Preserve previous derivatives/provenance and reference history. Production and independent disaster-recovery qualification remain excluded.

Owner approval is required by feature008FR015/FR016 and `docs/rendering-compatibility.md` (approved references, digest-qualified evidence and named owner approval). Owner explicitly replied "Approve references and nonprod risk acceptance" at 2026-10-16T11:29:25.488123+00:00. The exact candidate PFM hashes are adopted; no push, cloud fixture creation, activation or production qualification is inferred from this approval.
