# Validation: Windows Cloud Files synchronization

**Date**: 2026-09-15

## Local automated rehearsal

The locked .NET graph restores and builds with warnings as errors. The executable model tests cover opaque identity/path mapping, journal close/reopen, cursor-expiry reconciliation, exact SHA-256 hydration and partial cleanup, recursive pin/disk failure, stable offline XMP replay exactly once, lifecycle delete distinct from dehydration, dirty checkout preserve/block, remote divergence preservation, device revocation, accessible state text, five independent devices, and a 100,000-placeholder/no-content-transfer capacity run.

The final 2026-09-15 local run measured 0.175 seconds p95 across five independent 1,000-placeholder first listings and 19.635 seconds for the complete 100,000-placeholder capacity enumeration, with zero content bytes downloaded. These model results establish the implementation budget; native Explorer/darktable timing remains a trusted-host release gate.

Run from the repository root:

```powershell
dotnet restore tests/windows-sync/Platform.WindowsSync.Tests.csproj --locked-mode
dotnet build tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-restore
dotnet run --project tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-build
node tests/infrastructure/ci-contract/validate-ci.mjs
```

The journal upgrade/rollback rehearsal creates schema v1, upgrades it in place to schema v3, closes it, reopens it, and verifies device/pending state including the durable expected lifecycle/device versions. The schema changes are expand-only. The package and prior binary may share the root only while both understand the active journal/protocol version. The server migration runner applies migration 007 transactionally and checksum-pins all embedded migrations.

## Trusted Windows 11 release gate

Dispatch `.github/workflows/windows-sync-e2e.yml` only through the protected `windows-sync-e2e` environment. The resettable x64 host must set `WINDOWS_SYNC_TRUSTED_HOST=1` and use an NTFS `WINDOWS_SYNC_TEST_ROOT`. Repeat on ARM64 before distribution. Record:

- Explorer and darktable 5.6.x/5.4.x open/hydration start latency and exact bytes after cancellation/restart.
- Native recursive pin/unpin and status overlays under disk-full, file-lock, antivirus, and network faults.
- OS restart with offline edits, exactly-once reconnect, two-device 24-hour branch activity, web-change conflict, narrator/keyboard/focus checks, and crash recovery.
- Signed x64/ARM64 install, in-place upgrade, compatible rollback, revoke/export/uninstall, signature verification, and retained-data confirmation.

The trusted-host, ARM64, usability, 24-hour reliability, and production-signing evidence cannot be fabricated locally. They remain required before a release or deployment; the workflow is intentionally unavailable to untrusted pull requests and no distribution is authorized by feature implementation alone.

`makeappx.exe` and `signtool.exe` are not installed in the local implementation environment, so no MSIX was produced or signed here. The packaging script fails closed without those SDK tools and, unless unsigned validation is explicitly selected, an approved certificate thumbprint; the signed artifact rehearsal remains part of the protected release gate above.

## Convergence result

The post-implementation convergence pass traced all 20 functional requirements, 10 acceptance criteria, 16 non-functional requirements, five measurable outcomes, the data model, contract, ADR, and constitution gates to code, tests, documentation, or an explicitly protected release-only exercise. It found and remediated three implementation gaps before closure: immutable content-ID hydration, durable device/lifecycle versions, and in-place remote-revision reconciliation with dirty-state conflict preservation. No additional unbuilt in-scope task remains.

## Rollback

Stop the provider, retain a copy of its state directory, reinstall the last protocol-v1 signed package, and run `status` before enabling synchronization. If the prior binary cannot read the journal, stop and restore the pre-upgrade state copy; do not discard or acknowledge operations. For uninstall, follow [the operations runbook](../../runbooks/windows-sync-operations.md), export first, and leave local data untouched until the owner confirms cleanup.
