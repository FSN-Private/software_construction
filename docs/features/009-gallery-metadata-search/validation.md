# Validation evidence

Date: 2026-09-25. Branch: `feature/009-gallery-metadata-search`; base: `origin/master` after feature-008 squash merge. All work is in the root checkout.

| Check | Result |
|---|---|
| API build | `dotnet build apps/api/Platform.Api.csproj --no-restore -v:q` passed with 0 warnings and 0 errors. |
| Embedded migrations and PostgreSQL/S3 | `INGEST_INTEGRATION=1 GALLERY_CAPACITY=1` foundation suite passed migration 017 rehearsal, ingest corpus, lifecycle, owner denial, gallery browse/privacy/edit and idempotency, exact preview provenance mismatch, Windows two-device delivery/local conflict/explicit web commit, and source recovery. |
| Web | `npm run build`, `npm run test`, and `npm run test:accessibility` passed after gallery UI work. Pinned Prettier passes on every changed web file. The global Windows worktree format check reports unchanged files due line endings. |
| 100,000-asset query | Disposable PostgreSQL/S3 environment on Windows: filtered 40-card server page p95 **503.6 ms** after five warmups and 25 measured runs. Synthetic projection has 100,000 active assets. It does not measure browser load or cached thumbnail paint. |
| darktable XMP read/render | Local `platform/darktable-renderer:5.6.1-runtime-candidate` ran `darktable-cli` against a JPEG with gallery-patched XMP; export succeeded (21,288-byte JPEG). The XMP SHA-256 stayed unchanged and retained the edited title and unrelated `history_modversion`. The synthetic DNG ingest fixture itself could not be decoded by rawspeed, so the supported JPEG fixture was used for the renderer check. |
| Current browser and cached-thumbnail timing | Pending environment capable of current Chrome, Edge, and Firefox with authenticated rendering fixtures. Firefox and current Chrome are unavailable in the local workspace. |
| Dependency | MetadataExtractor 2.9.3/XmpCore locked; `dotnet list apps/api/Platform.Api.csproj package --vulnerable --include-transitive` reported no vulnerable packages from NuGet with network access. |
| API/contract/format | `npm run lint`, `npm run test:contract`, `npm run test:backend` (including API startup/correlation), and `dotnet format apps/api/Platform.Api.csproj --verify-no-changes --no-restore` passed. |

The capacity harness uses synthetic active assets and projection rows in the disposable integration schema. It measures the API query and serialization path; it does not claim the spec's end-user two-second page or 500 ms cached-thumbnail thresholds until browser measurements are run. The hosted Windows Cloud Files/darktable job also remains a separate compatibility gate.
