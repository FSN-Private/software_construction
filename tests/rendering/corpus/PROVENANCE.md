# Corpus provenance

The owner approved [eight additional exact Intel output hashes](../../../docs/features/011-backup-disaster-recovery/reviews/intel-renderer-reference-proposal.md)
on2026-10-20 after controlled same-image AMD/Intel comparisons. The original
PFMs remain the primary references. `reference/intel-reference-hashes.json`
contains only those eight additional per-fixture hashes, without tolerance.
`reference/intel-historical-v1.pfm.gz` losslessly stores the exact512px public
Canon/XMP candidate from run39439825637/sample2 for executable acceptance and
corruption regression tests; decompressed SHA-256 is
`1d370508d24a73c406cced938ed83b7bd64ee0207d61dc16458bcb5d49b58ed6`.
Its source image remains CC0-1.0 and project-authored historical-v1 XMP; the
candidate manifest and approval retain the source/image/XMP/output bindings.

| Input | Source | License | SHA-256 |
|---|---|---|---|
| `IMG_9406.CR2` | raw.pixls.us `Canon/EOS 1000D/IMG_9406.CR2` | CC0-1.0 | `08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c` |
| `Pentax-K10D.dng` | [raw.pixls.us sample 2768](https://raw.pixls.us/getfile.php/2768/nice/Pentax%20-%20K10D%20-%2012bit%2012bit%20uncompressed%20%283%3A2%29.DNG), Pentax K10D, 12-bit uncompressed | CC0-1.0 | `e7ea961edfea725ad2753f261d20da5e6addd5d641e5bcbc373999710f27941f` |

`current.xmp` and `historical-v1.xmp` through `historical-v3.xmp` are project-authored darktable sidecars. They vary only the exposure operation by +1.0, -1.0, -0.5, and +0.5 EV respectively. This keeps each historical state intelligible while making all four renders visibly distinct.

The nonprod fixture bootstrap uses the genuine DNG because existing ingest rejects CR2, while the existing ingest iPhone DNG uses a predictor unsupported by the hardened renderer. The provider repository metadata on 2026-10-15 marks sample 2768 CC0-1.0 and publishes the SHA-256 above; the downloaded 16,913,175 bytes match it. The helper adapts the four authored sidecars' DerivedFrom name to the logical `sample.dng` upload and adds a neutral-exposure fifth state. It pins the DNG before any upload. Both real ingest/publication and native renderer checks are required; none of this replaces deployed HTTP acceptance or the separate original CR2 compatibility corpus.

The native script accepts only an explicit renderer image reference through `DARKTABLE_IMAGE`. A local 5.6.1 candidate rendered the four distinct outputs on 2026-09-23, but those outputs are preliminary: they must be reproduced with an approved registry digest on the trusted Linux runner before becoming compatibility evidence.

2026-10-16 correction: original sidecars misencoded legacy exposure-v5 mode/black/exposure fields, and both historical-v3 PFMs contained onlyNaNs. The corrected sidecars encode manual mode0,black0 and exposureEV-1/-0.5/+0.5/+1. The owner explicitly approved the [portable reference/risk proposal](../../../docs/features/011-backup-disaster-recovery/reviews/portable-renderer-reference-proposal.md) and its exact eight finite PFM hashes at2026-10-16T11:29:25Z. Upstream BINARY_PACKAGE_BUILD=ON avoids build-host-only CPU targeting; version/archive/source RAW/license remain unchanged. The local patched image f26249d6 reproduces all eight outputs and scans0critical/high, with explicit controllednonprod acceptance of46medium/26low. This reference decision does not confer trusted-Linux, registry or deployed qualification; those remain required before activation.
