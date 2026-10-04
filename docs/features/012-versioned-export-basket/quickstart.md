# Export validation guide

Run checks from the repository root. Exact results and outstanding gates are recorded in [validation.md](validation.md); local fixture success does not certify deployed transport or the feature 011 publisher.

1. npm run validate:toolchain; existing local PostgreSQL/S3 integration setup; approved darktable corpus; owner/operator/unauthorized fixtures.
2. dotnet run --project tests/exports/Platform.Exports.Tests.csproj: filename/manifest/consent/state/lease/checksum invariants.
3. node tests/integration/exports/run.mjs: durable CRUD, exact pinning, concurrent snapshot/idempotency, mixed outcomes, ZIP integrity, authorization, cancel/expiry/purge and worker restart. Label stub tests separately from mandatory approved real-render metadata/provenance evidence.
4. npm --prefix apps/web run test:browser -- exports.spec.ts plus gallery/history regressions; record keyboard/assistive WCAG 2.2 AA and under-two-minute 100-item usability evidence.
5. node tests/performance/exports/run.mjs: 20 baskets, 250 items, 10 retained requests; p95 basket <=2s, acknowledgment <=3s; gallery index outage preserves baskets/packages.
6. npm run validate, relevant backend/integration suites, web format/typecheck/build, infrastructure validation for infrastructure edits. Rehearse additive migration/old API rollback; scan logs for fixture private paths/GPS/contact/secrets.
7. Extract downloaded ZIP and independently verify package hash and every output hash/length. Move refs/hide/rewrite and prove no substitutions. Cancel/expire/purge then retry grants/ranges: deny.
8. Convergence, complete appended work, independent alternate-model review, record exact commands/results. No push/deployment authorization.

Windows native corpus: `tests/exports/RenderCorpus.ps1 -Image sha256:<existing-local-renderer-image-id>` reproduces all eight reference pixel outputs and exercises real encoded JPEG metadata transforms. Artifacts remain under ignored `artifacts/`. Trusted Linux registry admission remains a separate required check.

Opt-in transport contracts: `npm --prefix packages/infrastructure run synth:exports`, then `npm --prefix packages/infrastructure run assert:exports`. Both bootstrap and opt-in templates must pass; synthesis is read-only and does not deploy the fixed-cost ALB.

If Windows Firefox hangs on checkbox focus with its accessibility service enabled, `BROWSER_TEST_DISABLE_FIREFOX_ACCESSIBILITY=1` permits a diagnostic automation run. Record this condition with the result. It does not satisfy supported screen-reader/manual accessibility acceptance.

## Review remediation checks

- T027/T013/T020: exercise the deployed CloudFront/private-ALB path (not Gateway or localhost) with >10 MB and multi-GiB ZIPs, native Chrome/Edge/Firefox save and interrupted/resumed ranges, five-minute grant refresh, bootstrap replay/CSRF/session sign-out/revocation, cache denial, long-running transfer beyond 30 seconds and bounded memory. Inspect exact-origin CORS and checksum/range header exposure.
- T010: prove approved renderer profile is unchanged and export metadata transform version/treatment affects export cache identity and manifest provenance; worker redispatch uses stored original/XMP versions for retained/deleted pins.
- T028/T015: label trusted tombstone fixtures as consumer-contract tests; prove scope, lock ordering, publication/download fences and exact export-version cleanup acknowledgment without deleting shared renditions.
- T029: actual feature 011 authority/retention/recovery-copy purge integration is required for full AC-007/AC-013/NFR-016 evidence. It is currently blocked. Do not report full completion, convergence or production enablement using synthetic publisher evidence alone.
- T030: pause an export-owned write across tombstone/cleanup; allow late success, callback rejection/loss and worker death. Require cleanup to wait for proven writer settlement and removal of every version/multipart remnant, including unknown initiation outcomes. Lease expiry/timeout alone must not permit acknowledgment. Verify no media appears after acknowledgment; shared renderer objects remain outside export-owned cleanup.
