[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$Version = '1.0.0.0',
    [string]$Publisher = 'CN=Software Construction',
    [string]$CertificateThumbprint,
    [switch]$SkipSigning
)

$ErrorActionPreference = 'Stop'
$projectRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$project = Join-Path $projectRoot 'apps\windows-sync\Platform.WindowsSync.csproj'
$staging = Join-Path $projectRoot "artifacts\windows-sync\$Architecture\package"
$output = Join-Path $projectRoot "artifacts\windows-sync\Platform.WindowsSync-$Architecture.msix"
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\windows-sync'))
$resolvedStaging = [System.IO.Path]::GetFullPath($staging)
if (-not $resolvedStaging.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Package staging path escaped the Windows sync artifact directory.' }

if (-not $SkipSigning -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) { throw 'CertificateThumbprint is required unless -SkipSigning is explicitly selected for local validation.' }
$makeAppx = Get-Command makeappx.exe -ErrorAction Stop
$signTool = if ($SkipSigning) { $null } else { Get-Command signtool.exe -ErrorAction Stop }

if (Test-Path -LiteralPath $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
dotnet publish $project --configuration Release --runtime "win-$Architecture" --self-contained true --locked-mode --output $staging
if ($LASTEXITCODE -ne 0) { throw 'Windows sync publish failed.' }

$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'AppxManifest.xml')
$manifest = $manifest.Replace('__ARCHITECTURE__', $Architecture).Replace('__VERSION__', $Version).Replace('__PUBLISHER__', $Publisher)
$manifest | Set-Content -Encoding utf8 (Join-Path $staging 'AppxManifest.xml')

Add-Type -AssemblyName System.Drawing
$assets = New-Item -ItemType Directory -Path (Join-Path $staging 'Assets')
foreach ($entry in @{ 'StoreLogo.png' = 50; 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150 }.GetEnumerator()) {
    $bitmap = [System.Drawing.Bitmap]::new($entry.Value, $entry.Value)
    try { $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.Clear([System.Drawing.Color]::FromArgb(32, 82, 140)); $bitmap.Save((Join-Path $assets $entry.Key), [System.Drawing.Imaging.ImageFormat]::Png); $graphics.Dispose() } finally { $bitmap.Dispose() }
}

New-Item -ItemType Directory -Force -Path (Split-Path $output) | Out-Null
& $makeAppx.Source pack /o /d $staging /p $output
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }
if (-not $SkipSigning) {
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 $output
    if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
    & $signTool.Source verify /pa /all $output
    if ($LASTEXITCODE -ne 0) { throw 'Signature verification failed.' }
}
Get-FileHash -Algorithm SHA256 $output
