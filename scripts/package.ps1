# Packs a release with Velopack (https://github.com/velopack/velopack):
# a portable installer .exe with delta updates, no MSIX required.
# Prereq: dotnet tool install -g vpk
# Usage: scripts/package.ps1 -Version 0.1.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Channel = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish\Cida"
$packDir = Join-Path $repoRoot "artifacts\release"

dotnet publish (Join-Path $repoRoot "windows\Cida.Desktop\Cida.Desktop.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

vpk pack `
    --packId Cida `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Cida.exe `
    --packTitle "辞达 Cida" `
    --channel $Channel `
    --outputDir $packDir

Write-Host "Release in $packDir"
