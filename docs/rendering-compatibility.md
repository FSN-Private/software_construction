# Rendering compatibility matrix

No renderer image or profile is approved until this record contains a reviewed immutable artifact digest, SBOM, license result, vulnerability result, corpus evidence, and named owner approval.

| Matrix | darktable release | Artifact digest | Profile | Corpus evidence | Status |
|---|---|---|---|---|---|
| `rendering-v1` | 5.6.1 | Candidate local manifest `sha256:01b582e0f7ac9446c7911007db985dccb5d11e058cfee1f6dfa5a304a59ce362` (not registry-published) | `render-v1` | Pending trusted-runner corpus | Blocked: SBOM/CVE evidence and owner approval outstanding |

Changing any release, digest, profile, or output format creates a new matrix row. Existing derivatives retain their original provenance and are never relabelled.

## Approval checklist

1. Verify the release artifact comes from the documented upstream source and record its immutable digest and license.
2. Generate an SBOM and record the vulnerability scan result; critical/high findings block approval.
3. Run the current RAW plus three historical XMP corpus on the trusted Linux runner and retain checksums, dimensions, and provenance.
4. Verify malformed, missing, unsupported, timeout, and workspace-cleanup outcomes expose only safe codes.
5. Obtain the library owner’s explicit approval before changing the `approved` compatibility state.
