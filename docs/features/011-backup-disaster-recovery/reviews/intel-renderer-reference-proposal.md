# Exact Intel pixel-reference extension

Status: **PROPOSED — owner compatibility approval required before implementation**.
Scope: Feature 011 T048/T055; Feature 008 FR-015/FR-016 and AC-008/AC-009.
This is a maintenance proposal for the existing 5.6.1/render-v1 corpus.

## Decision requested

Approve the eight exact Intel candidate SHA-256 values below as additional
accepted outputs for their respective corpus fixtures, alongside the existing
approved references, for controlled non-production qualification. Keep the
existing references. Accept no other hash and introduce no numerical tolerance.
The owner must explicitly approve this reference extension; prior approval of
the AMD reference bytes does not approve these additional bytes.

This removes the demonstrated single-reference qualification blocker. It does
not establish live cache performance, successful deployment or production
eligibility. All existing security, native, latency, ingest, activation and
rollback gates still apply.

## Reproduced cause of the gate failure

[Controlled run 39439825637](https://github.com/FSN-Private/software_construction/actions/runs/39439825637)
executes the same verified release39397523975/source
`acc789ca0a7f16fc140b97a69418a42d84909dbd` in separate hosted jobs.
All samples use image configuration
`sha256:683e2ba097ecd79b3a01472f8b30f7d77db3f9499efda31907f16cea0c6446ea`.

- Samples1 and3, separate AMD EPYC7763 runners: baseline, repeat and libc-optimization-disabled corpus
  comparisons all pass the eight existing references.
- Samples2 and4, separate Intel Xeon6973P-C runners: baseline, repeat and
  libc-optimization-disabled first comparisons all return the same mismatching
  hash seen in actual deployment39433716049. The complete eight-candidate
  manifests from these independent jobs are byte-identical.
- The same release archive locally matches all eight approved references on an
  AMD Ryzen4700U. One CPU/twoGiB/one thread, immutable source bytes, fixed XMPs,
  non-root execution and read-only root are retained in the controlled captures.

The current gate assumes one pixel result across these platforms. The measured
outputs are platform-dependent and repeatable in these samples. The precise
machine instruction responsible is not isolated. Neither arbitrary within-host
nondeterminism nor an image-content regression is established by these results.
The [upstream cross-machine discussion](https://github.com/darktable-org/darktable/issues/22589)
also reports AMD/Intel output differences; it is context, not our approval evidence.

## Exact proposed additional outputs

| Fixture | Proposed additional SHA-256 |
|---|---|
| historical-v1.pfm | `1d370508d24a73c406cced938ed83b7bd64ee0207d61dc16458bcb5d49b58ed6` |
| historical-v1-2048.pfm | `5bb313a7ebb52a014b59daf776db017cdd5454a3d46438a7c844e29136ab9725` |
| historical-v2.pfm | `5fdbd9a6876e4c7be421a41dad6bb77e2cac891d124040ad2354061b4b132258` |
| historical-v2-2048.pfm | `b164269968cbeb6313f4d322c35be25ef2dd08221500ddcf98782d2a563156d4` |
| historical-v3.pfm | `bf130f76d388dc4df9a6a7266dd95168287a97c44fb5df15205466d5d95ea72d` |
| historical-v3-2048.pfm | `f6eafc0970f6ef7475deb554713aadeb0bfad9e59fff4d3c00b0fb2057fe8e4e` |
| current.pfm | `b6ac548990e1e547e8f86c07016342f4adc4c81beae931dfbec63e7e16c9cbca` |
| current-2048.pfm | `daad2bf1886242bda1d12b7b62943a5111235ada957b005b209b247813533a7c` |

The [unaltered candidate manifest](../evidence/intel-renderer-candidate-2026-10-20/candidate-manifest.json)
binds filenames, lengths, exact XMP hashes, source revision, image identity and
CPU. Its SHA-256 is
`43171fcfbdee04acd2ce1629234f612a46e2ae320eb53020fa191f333ed77107`.
Original RAW bytes remain the CC0 Canon fixture with SHA-256
`08b0843a7968315b68bd3c71a96c952ebf275d5b5eba4b6094ae8b103522584c`.
No source, XMP, exposure parameter, darktable version or runtime image changes.

Full public-corpus PFMs are retained in run39439825637 artifacts12272769820
(sample2, archive digest
`sha256:1dd9cba305da0d509c32727f7e58ace9850e09c9274352f81fa86ae490f415bd`)
and12272024918(sample4, archive digest
`sha256:7e26667c79c0402ec5e4dd5adb94a882ae3af8a3d19a7d20e69de4c2efc3d059`).
Retention is14days. The small source/hash/analysis records and display previews
are retained in the repository; no duplicate143MB PFM set is proposed for Git.

## Pixel assessment and review previews

All eight candidates have the exact approved dimensions/byte lengths and finite
pixels. Four exposure states remain distinct. The
[full numerical analysis](../evidence/intel-renderer-candidate-2026-10-20/pixel-analysis.json)
records every differing-float count, range, maximum/mean absolute difference and
RMS difference. Maximum absolute differences range from0.0000429153 to0.0014837980
across the eight files. These measurements do not define an acceptance tolerance.

Display-only comparisons apply the same sRGB display mapping to both PFM arrays;
left is the existing approved output, right is the proposed Intel output. These
PNGs are review aids, not exact JPEG qualification evidence or replacements for
the hashed PFMs:

- [Exposure -1EV](../evidence/intel-renderer-candidate-2026-10-20/historical-v1-comparison.png)
- [Exposure -0.5EV](../evidence/intel-renderer-candidate-2026-10-20/historical-v2-comparison.png)
- [Exposure +0.5EV](../evidence/intel-renderer-candidate-2026-10-20/historical-v3-comparison.png)
- [Exposure +1EV](../evidence/intel-renderer-candidate-2026-10-20/current-comparison.png)

## Implementation and verification if approved

T055 will keep the original exact file comparison and permit only a matching
fixture-specific additional approved SHA-256 after that comparison differs.
Finite/dimension/length checks still precede acceptance. An output matching
neither reference remains a failure, including on a new CPU. No environment
variable, learned baseline, retry, percentile, pixel exclusion or tolerance can
approve another result. Failure diagnostics must identify both allowed hashes.

Tests must accept the existing and exact proposed candidates and reject an
unknown fixture, one-bit mutation, malformed/truncated/wrong-size/NaN/infinity
PFM. Run the full unchanged corpus on sampled AMD and Intel runners, then
require independent review, current-source CI and trusted release verification,
fresh exact-artifact security and real rendering/ingest acceptance before any
activation or fixture configuration. The2s/30s/500ms criteria remain unchanged.

No database migration, runtime dependency, privilege, owner/library/provenance,
cache identity, user interface, localization or accessibility change is needed.
The existing controlled-nonprod46medium/26low risk acceptance is unchanged;
new findings still require assessment and critical/high findings still block.
Production remains independently blocked. Reverting the hash extension restores
the former stricter gate without modifying source data or completed derivatives.

Alternative: retain one global reference and change the renderer's arithmetic
to force cross-platform equality. That changes native runtime behavior, needs
new numerical/performance validation and may itself require new reference bytes.
It is a broader change than acknowledging the two measured exact outputs.

## Approval

Pending owner decision. No additional reference is active and no implementation
of T055 is authorized by this document alone.
