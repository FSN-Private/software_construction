# Corpus provenance

| Input | Source | License | SHA-256 |
|---|---|---|---|
| `IMG_9406.CR2` | raw.pixls.us `Canon/EOS 1000D/IMG_9406.CR2` | CC0-1.0 | `08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c` |

`current.xmp` and `historical-v1.xmp` through `historical-v3.xmp` are project-authored darktable sidecars. They vary only the exposure operation by +1.0, -1.0, -0.5, and +0.5 EV respectively. This keeps each historical state intelligible while making all four renders visibly distinct.

The native script accepts only an explicit renderer image reference through `DARKTABLE_IMAGE`. A local 5.6.1 candidate rendered the four distinct outputs on 2026-09-23, but those outputs are preliminary: they must be reproduced with an approved registry digest on the trusted Linux runner before becoming compatibility evidence.
