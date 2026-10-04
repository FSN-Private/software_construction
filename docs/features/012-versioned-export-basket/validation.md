# Feature 012 implementation evidence

Approved design and tasks: 2026-10-04. Branch feature/012-versioned-export-basket; base 2680c6a. Planning milestone 36e8347.

- Pinned .NET 10.0.400, Node 24.20.0, npm 11.19.0 available.
- `dotnet restore tests/exports/Platform.Exports.Tests.csproj`: passed; existing dependencies only, vulnerability metadata retrieved.
- `dotnet run --project tests/exports/Platform.Exports.Tests.csproj --no-restore`: passed contract/name/manifest/hash/metadata/purge-scope checks.
- `EXPORT_INTEGRATION=1 dotnet run --project tests/exports/Platform.Exports.Tests.csproj --no-restore`: passed local PostgreSQL migrations and persistent baskets/selection/authorization/revision/hidden/working/quota/no-hold fixtures; MinIO connectivity passed.
- `dotnet build apps/api/Platform.Api.csproj --no-restore --nologo`: passed, zero warnings/errors.
- Local Docker contains feature 008 hardened renderer image; exact approved digest/corpus reproduction still required for T010/T023.

Evidence is incremental. Application transport, full render/preparation, browser, production, feature 011 authoritative purge and remaining acceptance checks are not certified by these foundation checks.

## Preparation/transport implementation checkpoint (2026-10-04)

- Real PostgreSQL/MinIO integration passes immutable original ZIP publication, all duplicate output bytes, manifest/failures membership, package SHA-256, exact native ranges, one-use exchange, CSRF/replay denial, secure scoped cookies and session revocation.
- Fifty concurrent submission retries produce one request; changed input conflicts; ten retained requests and gallery-index table absence are exercised.
- Consumer fixtures exercise write admission, idempotent tombstones, late object success, generation fencing, deletion of settled versions, preservation of shared renderer objects, unknown outcomes withholding acknowledgment, and acknowledgment only after explicit verified fixture settlement. These are consumer fixtures, not authoritative feature 011 evidence.
- `node tests/performance/exports/run.mjs`: passed after batching eligibility queries. Local 20-basket/250-item explicit-duplicate fixture measured p95 open 52.3 ms, update 129.0 ms, submit 261.7 ms. Prior round exceeded update budget (3514.7 ms); the query change addresses that measured failure. These are server fixture timings, not real-user usability evidence.
- `npm run test:backend`: passed foundation, version-control, release-manifest and API correlation regressions.
- Chrome and Edge export and gallery checks passed. Firefox launch and standalone checkbox checks pass, but application workflow checks time out at checkbox focus/check; this remains unresolved and acceptance is not claimed.
- Default and opt-in infrastructure synthesis succeeded; disabled-template IAM assertions were updated for the narrowly owned `exports/*` write/delete prefix. Opt-in deployed >10 MB/GiB transfer, network, timeout and memory evidence remains unavailable: no deployment is authorized.
- MinIO fixture overrides AWS KMS headers because local MinIO does not provide AWS KMS. Production multipart requests require AWS KMS. Local tests do not certify real AWS KMS enforcement.
- Interim independent review is underway. Locally incomplete work includes verifiable recovery of unknown/abandoned admitted writers, full fault/render/browser/telemetry coverage and acceptance evidence. Do not attribute these gaps solely to the separate T029 dependency.

## Review remediation and browser/corpus checkpoint

- Alternate-model interim review of `2680c6a..ea4dde9` returned three blockers; findings/dispositions are preserved in [implementation-review.md](reviews/implementation-review.md). Completion review remains pending.
- Real HTTP JWT middleware checks pass anonymous basket/manifest/cancel denial, invalid-token denial and attributable authenticated non-owner denial audits. Dedicated-origin CORS uses exact web-origin admission, credentials and explicit range/checksum header exposure; its HTTP preflight fixture is included in the latest runner.
- Durable workspace admission precedes every local media write. Local live-writer and OS-proven terminated-process fixtures exercise production cleanup; a successful actual multipart completion with a durable provider receipt/version exercises recovery without the settlement callback. Unknown storage calls and foreign volumes remain draining. Additional fault/deployed recovery evidence remains open under T014/T030.
- Firefox 155.0: 41 export/gallery/frontend tests passed, one live-reference test skipped, using temporary diagnostic `accessibility.force_disabled=1`. Default accessibility-enabled Windows runs reproducibly hang on checkbox focus. The committed opt-in environment switch preserves default behavior; this automation workaround is explicitly not assistive-technology acceptance.
- Web source tests, accessibility structure tests, formatting, typecheck and production build passed. Chrome/Edge had passed the affected export/gallery suite before the configured-limit follow-up; rerun evidence is required after that change.
- Native local image `sha256:a3d57603d490e8fda9c73ad732fc978ff6ecf5dde61f4780f6b9699f6844ff1b`: `tests/exports/RenderCorpus.ps1` passed all sixteen renders (four XMP selections, JPEG/PFM at 512/2048), all eight exact reference PFM hashes and all eight real JPEG retain/private/idempotent/injected-metadata/distinct-XMP checks. Artifacts: ignored `artifacts/exports-corpus-a6c1e99ab018488697c037e1b3988313`. This local corpus does not establish approved registry promotion or trusted Linux admission.
- `node tests/infrastructure/exports/transport-contract.mjs` passed opt-in private-origin/routing/authentication/cache/logging/capacity/alert contracts; default template assertions and infrastructure typecheck passed. CI contract checks passed after adding the feature runners. No deployment or deployed transfer/memory evidence exists.
