[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($env:WINDOWS_SYNC_TRUSTED_HOST -ne '1') { throw 'This runner is not marked as the resettable trusted Windows sync host.' }
$required = @('WINDOWS_SYNC_TEST_ROOT', 'WINDOWS_SYNC_STATE', 'WINDOWS_SYNC_SMOKE_IMAGE', 'WINDOWS_SYNC_SMOKE_XMP', 'WINDOWS_SYNC_IMAGE_SHA256', 'WINDOWS_SYNC_XMP_SHA256', 'WINDOWS_SYNC_DARKTABLE_CLI', 'PLATFORM_SYNC_API', 'PLATFORM_SYNC_LIBRARY', 'PLATFORM_SYNC_DEVICE', 'PLATFORM_SYNC_TOKEN')
foreach ($name in $required) { if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "$name is required." } }
if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'The trusted integration runner must use Windows 11.' }
if (-not (Test-Path -LiteralPath $env:WINDOWS_SYNC_TEST_ROOT -PathType Container)) { throw 'WINDOWS_SYNC_TEST_ROOT does not exist.' }
if (-not (Test-Path -LiteralPath $env:WINDOWS_SYNC_DARKTABLE_CLI -PathType Leaf)) { throw 'WINDOWS_SYNC_DARKTABLE_CLI does not exist.' }

$root = (Resolve-Path $env:WINDOWS_SYNC_TEST_ROOT).Path
$volume = Get-Volume -DriveLetter ([System.IO.Path]::GetPathRoot($root)).Substring(0, 1)
if ($volume.FileSystemType -ne 'NTFS') { throw 'Cloud Files integration requires an NTFS test volume.' }
dotnet run --project tests/windows-sync/Platform.WindowsSync.Tests.csproj --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Windows sync integration checks failed.' }

$env:PLATFORM_SYNC_ROOT = $root
$env:PLATFORM_SYNC_STATE = [System.IO.Path]::GetFullPath($env:WINDOWS_SYNC_STATE)
$env:PLATFORM_SYNC_BRANCH = if ([string]::IsNullOrWhiteSpace($env:PLATFORM_SYNC_BRANCH)) { 'main' } else { $env:PLATFORM_SYNC_BRANCH }
dotnet run --project apps/windows-sync/Platform.WindowsSync.csproj --no-restore -- setup
if ($LASTEXITCODE -ne 0) { throw 'Windows sync trusted device setup failed.' }

$provider = Start-Process dotnet -ArgumentList @('run', '--project', 'apps/windows-sync/Platform.WindowsSync.csproj', '--no-restore', '--', 'run') -PassThru -WindowStyle Hidden
try {
    $image = [System.IO.Path]::GetFullPath((Join-Path $root $env:WINDOWS_SYNC_SMOKE_IMAGE))
    $xmp = [System.IO.Path]::GetFullPath((Join-Path $root $env:WINDOWS_SYNC_SMOKE_XMP))
    foreach ($path in @($image, $xmp)) { if (-not $path.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'A smoke path escaped the trusted sync root.' } }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((!((Test-Path -LiteralPath $image) -and (Test-Path -LiteralPath $xmp))) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not ((Test-Path -LiteralPath $image) -and (Test-Path -LiteralPath $xmp))) { throw 'Trusted smoke placeholders were not populated.' }
    if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant() -ne $env:WINDOWS_SYNC_IMAGE_SHA256.ToLowerInvariant()) { throw 'Hydrated image digest did not match.' }
    if ((Get-FileHash -LiteralPath $xmp -Algorithm SHA256).Hash.ToLowerInvariant() -ne $env:WINDOWS_SYNC_XMP_SHA256.ToLowerInvariant()) { throw 'Hydrated XMP digest did not match.' }
    $render = Join-Path $root '.trusted-darktable-render.jpg'
    & $env:WINDOWS_SYNC_DARKTABLE_CLI $image $xmp $render
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $render -PathType Leaf)) { throw 'darktable could not read the hydrated image/XMP pair.' }
    Remove-Item -LiteralPath $render -Force
    dotnet run --project apps/windows-sync/Platform.WindowsSync.csproj --no-restore -- status
    if ($LASTEXITCODE -ne 0) { throw 'Windows sync status failed after trusted hydration.' }
}
finally {
    if (-not $provider.HasExited) { Stop-Process -Id $provider.Id -Force }
}
