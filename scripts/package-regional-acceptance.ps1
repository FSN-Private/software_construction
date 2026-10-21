[CmdletBinding()]
param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '../artifacts/release/regional-acceptance-client.zip' }
Add-Type -AssemblyName System.IO.Compression
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundleEntries = [ordered]@{
    'handler.mjs' = 'scripts/regional-acceptance/handler.mjs'
    'contract.mjs' = 'scripts/regional-acceptance/contract.mjs'
    'smoke-rendering.mjs' = 'scripts/smoke-rendering.mjs'
    'smoke-ingest.mjs' = 'scripts/smoke-ingest.mjs'
    'sample.png' = 'tests/fixtures/ingest/sample.png'
}
$bundleStream = [IO.MemoryStream]::new()
$archive = [IO.Compression.ZipArchive]::new($bundleStream, [IO.Compression.ZipArchiveMode]::Create, $true)
try {
    foreach ($entryName in $bundleEntries.Keys) {
        $sourcePath = Join-Path $repoRoot $bundleEntries[$entryName]
        $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        if ($entryName.EndsWith('.mjs')) {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes([IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n"))
        } else { $bytes = [IO.File]::ReadAllBytes($sourcePath) }
        $entryStream = $entry.Open()
        try { $entryStream.Write($bytes, 0, $bytes.Length) } finally { $entryStream.Dispose() }
    }
} finally { $archive.Dispose() }
try {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
    [IO.File]::WriteAllBytes($resolvedOutput, $bundleStream.ToArray())
} finally { $bundleStream.Dispose() }
Write-Output 'Regional acceptance client packaged.'
