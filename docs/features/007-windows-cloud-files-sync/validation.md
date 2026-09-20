# Validation: Windows Cloud Files synchronization

**Date**: 2026-09-20

## Local automated rehearsal

The locked .NET graph restores and builds with warnings as errors. The executable model tests cover opaque identity/path mapping, journal close/reopen and v1-to-v4 migration, cursor-expiry and terminal-cursor reconciliation, exact SHA-256 hydration and partial cleanup, recursive pin/disk failure, stable offline XMP replay exactly once, edit delete/restore routing with chained authoritative lifecycle versions, clean and dirty branch reconciliation, deterministic selected-branch path collisions, device revocation, accessible state text, five independent devices, and a 100,000-placeholder/no-content-transfer capacity run.

The final remediation run on 2026-09-20 measured 0.100 seconds p95 across five independent 1,000-placeholder first listings and 14.219 seconds for the complete 100,000-placeholder capacity enumeration, with zero content bytes downloaded. A follow-up poll made one empty page request from the terminal cursor rather than re-enumerating the namespace. These model results establish the implementation budget; native Explorer/darktable timing remains a trusted-host release gate.

Run from the repository root:

```powershell
dotnet restore tests/windows-sync/Platform.WindowsSync.Tests.csproj --locked-mode
dotnet build tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-restore
dotnet run --project tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-build
node tests/infrastructure/ci-contract/validate-ci.mjs
node tests/integration/api-correlation.mjs
```

The journal upgrade/rollback rehearsal creates schema v1, upgrades it in place to schema v4, closes it, reopens it, and verifies device/pending state including durable asset/edit lifecycle and checkout versions. The schema changes are expand-only. The package and prior binary may share the root only while both understand the active journal/protocol version. The server migration runner applies migrations 007 and 008 transactionally and checksum-pins all embedded migrations. Migration 008 adds the owner-scoped lifecycle revision used by opaque namespace cursors; deployment and rollback controls are in the Windows sync runbook and ADR-0008.

The remediation also adds an isolated PostgreSQL integration check to the existing ingest integration job. It registers two devices, enumerates a real asset/edit namespace, verifies lifecycle metadata, updates the device cursor only after the Npgsql reader is disposed, expires both devices on lifecycle-only revisions, verifies authoritative move/edit-delete/edit-restore/asset-delete/asset-restore convergence, and reuses the terminal cursor on an idle poll. The local Docker daemon was unavailable on 2026-09-20, so this database-backed check is compiled locally and remains assigned to the existing CI PostgreSQL/MinIO job. The unconfigured API liveness/correlation test passes, confirming that mapped Windows Sync routes no longer prevent startup when storage services are absent.

The final local checks passed: locked restore, Windows sync and API builds with zero warnings, Windows sync executable checks (1,000-placeholder p95 0.236 s; 100,000-placeholder enumeration 18.484 s; zero downloaded bytes), API foundation and version-control non-database checks, release-manifest checks, API correlation, CI contract validation, `git diff --check`, and the transitive NuGet vulnerability audit. No vulnerable package was reported.

## Trusted Windows 11 release gate

Dispatch `.github/workflows/windows-sync-e2e.yml` only through the protected `windows-sync-e2e` environment. The resettable x64 host must set `WINDOWS_SYNC_TRUSTED_HOST=1` and use an NTFS `WINDOWS_SYNC_TEST_ROOT`. Repeat on ARM64 before distribution. Record:

- Explorer and darktable 5.6.x/5.4.x open/hydration start latency and exact bytes after cancellation/restart.
- Native recursive pin/unpin and status overlays under disk-full, file-lock, antivirus, and network faults.
- OS restart with offline edits, exactly-once reconnect, two-device 24-hour branch activity, web-change conflict, narrator/keyboard/focus checks, and crash recovery.
- Signed x64/ARM64 install, in-place upgrade, compatible rollback, revoke/export/uninstall, signature verification, and retained-data confirmation.

The trusted-host, ARM64, usability, 24-hour reliability, and production-signing evidence cannot be fabricated locally. They remain required before a release or deployment; the workflow is intentionally unavailable to untrusted pull requests and no distribution is authorized by feature implementation alone.

`makeappx.exe` and `signtool.exe` are not installed in the local implementation environment, so no MSIX was produced or signed here. The packaging script fails closed without those SDK tools and, unless unsigned validation is explicitly selected, an approved certificate thumbprint; the signed artifact rehearsal remains part of the protected release gate above.

## Convergence result

The post-implementation convergence pass traced all 20 functional requirements, 10 acceptance criteria, 16 non-functional requirements, five measurable outcomes, the data model, contract, ADR, and constitution gates to code, tests, documentation, or an explicitly protected release-only exercise. Its original three implementation gaps remain closed. Pre-push review Round 1 then found and remediated namespace-reader lifetime, terminal-cursor, branch-removal, authoritative lifecycle-version, edit-route, checkout-contract, and HTTP request-lifetime gaps. Broader regression validation also found and fixed unconfigured API startup. No additional unbuilt in-scope task remains.

## Rollback

Stop the provider, retain a copy of its state directory, reinstall the last protocol-v1 signed package, and run `status` before enabling synchronization. If the prior binary cannot read the journal, stop and restore the pre-upgrade state copy; do not discard or acknowledge operations. For uninstall, follow [the operations runbook](../../runbooks/windows-sync-operations.md), export first, and leave local data untouched until the owner confirms cleanup.
