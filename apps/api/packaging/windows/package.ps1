[CmdletBinding()]
param(
  [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $repositoryRoot 'artifacts\windows'
}
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Platform.Api.csproj'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $output 'platform-api-win-x64'
$archive = Join-Path $output 'platform-api-win-x64.zip'

New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet restore $project --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) {
  throw 'Locked Windows restore failed.'
}
dotnet publish $project --configuration Release --runtime win-x64 --self-contained false --no-restore --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
  throw 'Windows package publish failed.'
}

if (Test-Path -LiteralPath $archive) {
  Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Output "Windows package created: $archive"
