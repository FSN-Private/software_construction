# Implementation Plan: Versioned Export Basket

**Branch**: feature/012-versioned-export-basket | **Date**: 2026-10-03 | **Spec**: [spec.md](spec.md)
**Status**: Approved by maintainer in chat on 2026-10-04; implementation in progress. Feature 011 end-to-end purge evidence is an explicit completion dependency.
**Base**: fetched origin/master at 2680c6a; initial divergence 0 behind / 0 ahead.
**Execution source**: [tasks.md](tasks.md). No push, merge or deployment authorized.

## Summary

Persist owner-private baskets and immutable export snapshots in PostgreSQL. Prepare versioned ZIP packages in private encrypted object storage from exact immutable source/XMP object versions. Reuse feature 008's isolated renderer, compatibility gate and provenance with an explicit pinned-input path, rather than resolving current at execution time. Extend the React gallery/history workspace. Use existing dependencies and .NET ZIP, JSON and hashing libraries; no new export service.

## Technical Context

Repository-pinned .NET 10, C#, TypeScript/React 19, Npgsql and AWS S3/SQS clients. Linux API/render workers and Windows-compatible output names. Add migration 021 after existing 020. Use existing executable assertion harness conventions, PostgreSQL/S3 integration tests and Playwright. Targets: p95 basket interaction <=2 seconds and snapshot acknowledgment <=3 seconds at 250 items; at least 20 saved baskets and 10 retained requests/basket. Submission never waits for rendering.

## Constitution Check

Matching branch and clean worktree verified before planning; requirements approved in chat on 2026-10-03. All FR/AC/NFR/SC items must map to numbered tasks. Additive schema, coherent milestone commits, convergence and alternate-model review required. Maintainer approved the revised design/task set on 2026-10-04. No constitution exceptions.

## Project Structure

- packages/contracts/Exports/ExportContracts.cs: versioned presets, identities, statuses, manifest and filename invariants.
- apps/api/Features/Exports/: baskets, eligibility, durable preparation worker, packaging, download and operator controls.
- apps/api/Database/Migrations/021_export_baskets.sql: owner-scoped basket/snapshot/request/result/package/audit/lease persistence.
- apps/api/Features/Rendering/, packages/contracts/Rendering/, apps/render-worker/: pinned inputs and declared export metadata profiles.
- apps/web/src/ExportBasket.tsx, export-api.ts, export-messages.ts, App.tsx, Gallery.tsx, ImageHistory.tsx: browser integration.
- tests/exports/, tests/integration/exports/, tests/performance/exports/, apps/web/tests/browser/exports.spec.ts: acceptance evidence.
- packages/infrastructure/: derivative-prefix permissions/lifecycle, export alarms and a dedicated CloudFront/private-ALB large-download route to the existing API task.
- docs/operations/export-basket.md and affected READMEs: owner/operator guidance.

Design details: [research](research.md), [data model](data-model.md), [API contract](contracts/export-api.md), [validation guide](quickstart.md). ADR-0004 and ADR-0014 accepted with this plan on 2026-10-04.

## Selection and lifecycle

Resolve current/branch/tag/commit in a transaction consistent with feature 006. Store asset/edit, exact original checksum/object version, commit and XMP checksum/object version, safe basename and version/ref labels. Reject mutable working selections with committed_version_required, as FR-005 permits. Duplicate selections remain distinct item IDs. Hidden selections require explicit advanced/show-hidden context checked against lineage state. Basket/export identities create or remove no lifecycle hold.

Eligibility uses immutable content integrity and existing lifecycle/recovery holds, including retained soft-deleted/displaced versions; do not restrict exports to active gallery rows. Recheck before rendering, publication and download. Keep pinned identity visible after unavailability. Feature 005 supplies soft delete/restore, not permanent purge. Feature 011 owns authorized permanent purge and recovery-copy checks and is currently unmerged. Missing bytes mean unavailable/integrity error, never proof of permanent purge.

Feature 012 implements only the durable invalidation consumer in [purge contract](contracts/purge-invalidation.md). Feature 011 must atomically publish authorized owner/library/asset/edit/content-scoped tombstones under the shared lifecycle fencing lock before its physical deletion. Export selection/publication/download admission uses that same lock and checks tombstones; invalidation revokes grants and queues deletion of dependent export object versions. Feature 012 owns export-package/temporary-output cleanup and acknowledgment; feature 011 owns purge authority, original/XMP/recovery-copy deletion and the final deletion obligation. No public owner/operator endpoint may manufacture a tombstone. Consumer contract fixtures are distinct from feature 011 integration evidence.

T029 is an explicit completion/rollout gate: feature 012 may be developed and consumer-tested independently, but cannot be declared fully complete or enabled in production until feature 011's authoritative publisher, lifecycle/recovery-copy checks and real purge race/cleanup acceptance evidence are available. This is a known dependency, not a request to merge or implement 011 in this work.

## Presets and privacy

Versioned catalog: originals-v1 returns exact original bytes unchanged and warns that embedded metadata remains; jpeg-2048-private-v1 produces sRGB JPEG, maximum 2048-pixel edge, removes descriptive EXIF/XMP/IPTC including GPS/location/contact; jpeg-2048-retain-v1 retains supported descriptive metadata only with explicit owner confirmation bound to preset/submission. Existing gallery presets remain compatible. Manifest is mandatory per FR-017; no omit-manifest control.

Current worker CLI has no metadata policy. Enforce a bounded validated JPEG metadata filter using standard library or existing worker support, with encoded fixtures and approved real-render corpus. Never assume cached previews satisfy privacy. Unsupported profiles fail without substitution. Technical provenance stays in manifest. Original-only entries retain selection context but never claim applied XMP rendering. No new imaging dependency without supply-chain assessment.

Map both JPEG export presets to the existing approved Preview2048 renderer profile/matrix; apply metadata policy as a separate export-only versioned transform after verified rendering. Do not add competing profiles to the matrix's single-profile primary-key design or change existing renderer promotion. Record render provenance plus transform ID/version, input derivative SHA-256 and transformed output SHA-256 in export results/manifest; cache/publication identity includes transform version and treatment. Explicit pinned request/dispatch and redispatch use stored original/XMP object versions and captured approved compatibility, never current-active-source lookup. Changed worker execution requires a new immutable artifact and the existing renderer admission/promotion gate; it cannot inherit old artifact approval. No migration of compatibility primary keys is needed for this approach.

## Preparation and packaging

Transactionally copy basket pins, selected preset/consent and renderer compatibility into immutable snapshot rows. Unique owner/library/idempotency key and canonical payload fingerprint return one logical request; changed payload under the same key conflicts, even after basket edits. Serialize basket mutation versus snapshot capture. Database-leased hosted worker resumes requests; attempts/leases fence callbacks and publication. Explicit pinned rendering verifies source/XMP object versions and checksums and compares exact provenance on completion. Do not cancel shared rendering for unrelated consumers.

Initial preparation is sequential within configured existing rendering concurrency. ZIP via ZipArchive uses bounded private disk workspace; package completed outputs, manifest.json and failures.json describing every requested outcome. No successful items means visible terminal failure without an empty download. Windows-safe deterministic names sanitize reserved names, invalid characters, trailing spaces/dots and Unicode, bound basename length and append full item UUID. Verify length/hash/provenance during packaging. Record package SHA-256 separately from ZIP; never put a self-referential ZIP hash in its own manifest.

Every export-owned storage write registers a durable intent and unique destination before admission, including temporary output, transformed output, ZIP and multipart/retry writes. Invalidation closes admission and fences publication, but cleanup cannot acknowledge until all admitted storage operations are proven quiescent and all intent-owned versions/multipart remnants have been reconciled and removed. Lost callbacks/unknown upload outcomes remain draining; lease expiry or timeout alone never proves cleanup. Shared feature 008 renditions retain their separate lifecycle owner. T030 implements this invariant and the paused-upload/late-success/worker-loss tests in the purge contract.

Proposed configurable defaults: 250 items, 10 GiB final package, <=30-minute item render ceiling within existing worker policy, two-hour total preparation, 24-hour download retention from completion. Warning acknowledgment before >=100 items or >=1 GiB estimated source bytes. Enforce actual bytes/time while preparing; precise resource-limit status rather than truncated success. At least 20 baskets/library and 10 retained requests/basket; reclaim eligible expired package capacity without losing required safe audit evidence.

## Security, operations and recovery

Owner/library checks on every object access, uniform cross-owner/nonexistent denial, private/no-store responses. Metadata/control requests retain the existing HTTP API Gateway. Large downloads use a separate HTTPS CloudFront distribution with an internal ALB VPC origin targeting the existing API task; API Gateway is absent from the media response path. CloudFront caching and error caching are disabled, private/no-store is enforced, and grants/cookies are forwarded to the API on every request. Limit listener paths to download-session exchange and package GET/HEAD; reject all other routes. Origin security groups restrict CloudFront-to-ALB and ALB-to-API traffic. Existing public API still denies package body responses. See [ADR-0014](../../architecture/decisions/0014-export-download-and-purge-boundaries.md).

The owner bearer-authenticates a short-lived grant creation call. To use native Chrome/Edge/Firefox disk downloads without buffering a GiB ZIP in JavaScript, the client submits a one-use <=60-second bootstrap code in a top-level form POST to the download origin. Exchange creates a Secure/HttpOnly/SameSite=Lax, request-path-scoped opaque cookie and redirects to a credential-free GET URL. The cookie identifies a server-side owner/request/session grant, expires <=5 minutes and never outlives the originating bearer expiry; requests check that session's revocation record, package state, expiry and tombstones. It grants no basket/manifest/operator permission. Only an explicitly configured HTTPS download origin is allowed; enforce expected web Origin on exchange and reject unsolicited cross-origin forms. Secrets stay out of URLs, referrers, telemetry and access logs. Sign-out revokes originating-session grants; replay/exchange/CSRF/foreign-request tests are required.

GET/HEAD supports single byte ranges, immutable package ETag, If-Range, Content-Range, Content-Length, Content-Disposition and separate SHA-256. Native browser save handles bytes without whole-file JS Blob assembly; manual interrupted/resumed native-download tests cover Chrome/Edge/Firefox. An admitted transfer may finish after its grant expiry, but every subsequent range request reauthorizes; an expired grant requires a fresh owner-issued exchange before retry/resume, while immutable package identity remains unchanged. Expiry/cancel/purge prevent every new transfer admission; bytes already delivered cannot be recalled. Use bounded copy buffers and backpressure; do not hash/download the entire package into RAM per range request. CloudFront read timeout and ALB idle timeout are 120 seconds; do not set a total-response-completion timeout that caps a progressing 10 GiB transfer. Validate >10 MB, multi-GiB memory/disk behavior and long-running/resumed streams through this actual path. Publish exact-origin control CORS policy (Authorization, Content-Type, Idempotency-Key, If-Match); for bearer-based diagnostic range clients expose ETag, Content-Range, Content-Length, Content-Disposition, X-Content-SHA256 and allow Range/If-Range. Native cookie navigation does not rely on cross-site fetch cookies.

Operator role may pause new preparation, drain/cancel pending work and view aggregate metrics only. Gallery-index outages must not affect saved baskets or unexpired packages. Lease recovery resolves to retryable/cancelled/terminal state within ten minutes. Audit allowlists actor/IDs/actions/reasons/counts/UTC; omit paths, descriptive labels, media, XMP, GPS and credentials. Metrics cover queue age, duration, item outcomes, bytes, render failures, downloads, expiry, cancellation and denials; sustained failure alerts within 15 minutes. Expiry/purge revokes access before physical cleanup; safe audit remains.

## User experience and validation

Add from filtered gallery and explicit history rows without losing URL/filter/branch/scroll/hidden-view context. Workspace basket view supports CRUD/default selection, clear/remove, labels/readiness/preview/output names, preset details/consent/warnings, progress, per-item errors, cancel/retry, manifest/checksum and download. Semantic forms/buttons, keyboard focus restoration, non-color status and live announcements; localizable message keys and Intl dates. Browser automation plus manual keyboard/assistive checks required for WCAG 2.2 AA.

Every milestone includes tests, affected docs, plan status and commit. Validate pinned toolchain, appropriate backend tests, real PostgreSQL/object-storage integration, browser regressions, format/typecheck/build and infrastructure checks. Real darktable provenance/metadata corpus, 100-item usability evidence, performance capacity and manual accessibility must be recorded; unavailable required evidence is a blocker, not a passing mock test.

## Migration, rollout and rollback

Add tables with capability disabled; deploy compatible API/worker/infrastructure, verify private object access, dedicated download origin and pause controls, enable UI only after acceptance including T029's feature 011 dependency. Rehearse migration and old API on new schema. Rollback disables submission and exchange, revokes grants and drains/cancels work while preserving schema/audit and expiry cleanup; disabled download distribution must never fall back to a public S3 URL. Do not drop immutable evidence. S3 lifecycle is a cleanup backstop, not authorization expiry. Account for added ALB/CloudFront cost in budget and nonproduction lifecycle controls. Production rollout is outside current authorization.

## Alternatives, risks and decisions

Existing renderer/hosted worker over new service; standard ZIP over custom format; committed-only selection over durable working snapshots; dedicated authenticated stream over presigned S3 URLs or a custom browser chunk assembler. CloudFront/private ALB adds running cost but avoids HTTP API payload limits and preserves Firefox/native-save compatibility. Metadata transforms remain separate from renderer profiles. Risks: purge races, metadata correctness, disk/quota exhaustion and unavailable renderer fixtures; tests and publication fencing address them. Feature 011 end-to-end purge evidence remains an explicit external completion prerequisite. Concrete design choices/ADR/tasks approved on 2026-10-04; no infrastructure deployment authorized.

## Milestones

M0 approve artifacts/ADR. M1 durable baskets/exact selection. M2 pinned rendering/verified packages. M3 cancellation/expiry/purge/operations. M4 complete browser workflow. M5 acceptance/runbooks/convergence/review. Commit each separately with task references; review remediation and final record get a dedicated checkpoint.
