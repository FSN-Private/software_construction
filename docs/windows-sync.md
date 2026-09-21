# Windows Cloud Files synchronization

Platform Windows Sync exposes one library branch as a Windows 11 Cloud Files sync root. Explorer and darktable see ordinary paths, while unpinned originals remain placeholders until opened. Local XMP changes are durable working changes; the provider never creates a commit automatically.

## Supported environment

- Windows 11 x64 or ARM64 on NTFS, with current OS updates.
- darktable 5.6.x or 5.4.x. Enable XMP sidecars, write sidecars after edits, and check for updated sidecars on startup. Import the sync root as an existing folder.
- A unique device ID and one branch per installation. Do not point two installations at the same local root or state directory.

Only library originals and their XMP sidecars belong in the sync root. Never put darktable `library.db`, `data.db`, configuration, presets, styles, caches, or backups in it. Those files are explicitly outside this feature.

## Install and configure

Release packages must be built for both architectures and signed by the approved publisher. A local unsigned package is only for isolated validation:

```powershell
./apps/windows-sync/Installer/package.ps1 -Architecture x64 -CertificateThumbprint '<approved thumbprint>'
./apps/windows-sync/Installer/package.ps1 -Architecture arm64 -CertificateThumbprint '<approved thumbprint>'
```

Set the following user-scoped values before first setup. Supply `PLATFORM_SYNC_TOKEN` only to `setup`; it is then protected with current-user Windows DPAPI and is not stored in placeholders or the journal.

```powershell
$env:PLATFORM_SYNC_API='https://api.example/'
$env:PLATFORM_SYNC_ROOT='D:\Platform Library'
$env:PLATFORM_SYNC_STATE="$env:LOCALAPPDATA\Platform\WindowsSync"
$env:PLATFORM_SYNC_LIBRARY='<library id>'
$env:PLATFORM_SYNC_DEVICE='<unique device id>'
$env:PLATFORM_SYNC_BRANCH='main'
$env:PLATFORM_SYNC_TOKEN='<short-lived setup token>'
Platform.WindowsSync.exe setup
Remove-Item Env:PLATFORM_SYNC_TOKEN
Platform.WindowsSync.exe run
```

Run the provider at user sign-in under the same Windows account. `status` reports branch, health, item-state counts, and conflicts without exposing paths. Explorer pin/unpin state is recursive. A dirty, pending, pinned, or conflicted file is never automatically dehydrated.

## Owner controls

- `Platform.WindowsSync.exe status` shows accessible text for every state; all actions have keyboard equivalents in the companion status model.
- `Platform.WindowsSync.exe revoke` revokes remote access and pauses the local device. It deliberately retains local bytes.
- Set `PLATFORM_SYNC_EXPORT`, then run `Platform.WindowsSync.exe export` to copy retained local files before cleanup.
- Uninstalling the package does not delete the sync root, journal, credential, conflict copies, or checkout stashes. After export and verification, the owner may remove those explicitly.

Diagnostics are opt-in. Logs and metrics may contain operation IDs, reason codes, counts, durations, cursor lag, and byte totals. They must never contain tokens, filenames, paths, owner secrets, image/XMP bytes, or geolocation. See [the Windows sync runbook](runbooks/windows-sync-operations.md) for recovery procedures.
