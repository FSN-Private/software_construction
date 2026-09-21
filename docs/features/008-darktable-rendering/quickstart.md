# Rendering Validation Quickstart

## Local model checks

1. Build and run `tests/unit/Platform.Api.Foundation.Tests`.
2. Verify an identical current rendition request produces one key and one pending attempt; a completed request becomes an exact cache hit.
3. Verify malformed/missing source, resource limits, output validation failure, and retry expose only safe codes and never create a derivative.
4. Verify a historical request without a feature-006 resolver returns `version_resolution_unavailable`, with no current-edit fallback.

## Integration checks

With the approved PostgreSQL/MinIO test services, run the rendering integration suite. It must verify atomic rendition/provenance publication, private object prefixes, owner isolation, duplicate coalescing, retry attempt numbering, stale-attempt recovery, and queue pause/drain behavior.

## Trusted native-renderer gate

Before any renderer release is approved, an approved Linux runner must provide the pinned darktable artifact and execute the corpus:

1. Record the renderer artifact digest, SBOM, license, vulnerability result, profile version, and compatibility approver.
2. Render one RAW current XMP plus three distinct historical XMP revisions at every configured rendition.
3. Compare approved outputs and provenance; exercise malformed/unsupported/time-limited cases and workspace cleanup.
4. Measure cached and uncached targets and the 100,000-image thumbnail freshness model.

No local model test or container build substitutes for this gate.
