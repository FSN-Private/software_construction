# Rendering Validation Quickstart

## Local model checks

1. Build and run `tests/unit/Platform.Api.Foundation.Tests`.
2. Verify an identical current rendition request produces one key and one pending attempt; a completed request becomes an exact cache hit.
3. Verify malformed/missing source, resource limits, output validation failure, and retry expose only safe codes and never create a derivative.
4. Verify a historical request without a feature-006 resolver returns `version_resolution_unavailable`, with no current-edit fallback.

## Integration checks

With the approved PostgreSQL/MinIO test services, run the rendering integration suite. It must verify atomic rendition/provenance publication, private object prefixes, owner isolation, duplicate coalescing, retry attempt numbering, stale-attempt recovery, and queue pause/drain behavior.

The deployed worker receives only `Rendering__QueueUrl`, immutable source/derivative bucket names, an ephemeral workspace, region, and the API Gateway callback base URL. It uses its ECS task role for SQS, S3, and SigV4 callbacks; it never receives database or owner credentials.

## Trusted native-renderer gate

Before any renderer release is approved, an approved Linux runner must provide the pinned darktable artifact and execute the corpus:

1. Record the renderer artifact digest, SBOM, license, vulnerability result, profile version, and compatibility approver.
2. Render one RAW current XMP plus three distinct historical XMP revisions at every configured rendition.
3. Compare the generated 512px PFM decoded-pixel output with the committed `tests/rendering/corpus/reference/` fixture for each state, retain the JPEG provenance hashes, and exercise malformed/unsupported/time-limited cases and workspace cleanup.
4. Run `node tests/rendering/performance/model-capacity.mjs` against retained approved-image evidence. It records the required single-concurrency worker count for the 100,000-image freshness target; do not treat it as cache-hit or request-start evidence.

No local model test or container build substitutes for this gate.

## Non-production HTTP acceptance

The protected non-production environment must define `AWS_NONPROD_RENDERING_ACCEPTANCE_FIXTURE` as JSON with `libraryId`, `assetId`, and `editId` for a retained owner-authorized RAW/current-XMP fixture. The deployment pauses intake, admits the exact digest only for that fixture owner, requires one uncached request to start within 2 seconds and complete within 30 seconds, measures 20 private-locator cache-hit samples with p95 at or below 500 ms, then activates normal intake. On a failure it rejects the candidate and restores the preceding ECS services before any normal request can use the candidate digest.
