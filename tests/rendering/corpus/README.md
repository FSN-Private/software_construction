# Darktable native rendering corpus

This corpus uses one CC0 Canon EOS 1000D CR2 original and four project-authored darktable XMP states: the current state and three historical revisions. The original is downloaded only by the corpus setup from raw.pixls.us and is accepted only when its published SHA-256 is `08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c`.

The revisions deliberately vary only the darktable exposure operation so that their visual differences are observable and their provenance remains easy to audit. `reference/*.pfm` contains the hardened candidate digest's 512px decoded-pixel references: PFM excludes JPEG's run-specific metadata, so `run-native.sh` fails on a pixel regression while still retaining JPEG output/provenance. Native evidence is incomplete until the approved renderer digest, XMP/output hashes, dimensions, and trusted-runner run identifier are recorded alongside the generated reference derivatives.
