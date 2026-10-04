param([Parameter(Mandatory = $true)][string]$Image)
$ErrorActionPreference = 'Stop'
if ($Image -notmatch '^sha256:[0-9a-f]{64}$') { throw 'Use an immutable local image ID.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$corpus = Join-Path $repo 'tests/rendering/corpus'
$output = Join-Path $repo ('artifacts/exports-corpus-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$expected = ((Get-Content (Join-Path $corpus 'IMG_9406.CR2.sha256')) -split ' ')[0]
if ((Get-FileHash (Join-Path $corpus 'IMG_9406.CR2') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Corpus input checksum mismatch.' }
foreach ($name in 'historical-v1', 'historical-v2', 'historical-v3', 'current') {
    foreach ($size in 512, 2048) {
        $stem = if ($size -eq 512) { $name } else { "$name-$size" }
        foreach ($extension in 'jpg', 'pfm') {
            & docker run --rm --entrypoint /opt/darktable/bin/darktable-cli --volume "${corpus}:/input:ro" --volume "${output}:/output" $Image /input/IMG_9406.CR2 "/input/$name.xmp" "/output/$stem.$extension" --width $size --height $size --library :memory:
            if ($LASTEXITCODE -ne 0) { throw "Native render failed: $stem.$extension" }
        }
        if ((Get-FileHash (Join-Path $output "$stem.pfm")).Hash -ne (Get-FileHash (Join-Path $corpus "reference/$stem.pfm")).Hash) { throw "Reference pixels changed: $stem" }
    }
}
$env:EXPORT_JPEG_CORPUS = $output
Push-Location $repo
try { & dotnet run --project tests/exports/Platform.Exports.Tests.csproj; if ($LASTEXITCODE -ne 0) { throw 'Encoded JPEG export checks failed.' } }
finally { Pop-Location; Remove-Item Env:EXPORT_JPEG_CORPUS }
Write-Output "Local immutable renderer corpus passed; artifacts: $output. This does not certify registry promotion or Linux admission."
