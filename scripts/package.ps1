# Build a self-contained GUI + console CLI release; each run uses a clean, unique staging directory.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Channel = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot ('artifacts\publish\' + [guid]::NewGuid().ToString('N'))
$packDir = Join-Path $repoRoot "artifacts\release\$Version"
foreach ($project in @('Cida.Desktop', 'Cida.Cli')) {
    dotnet publish (Join-Path $repoRoot "windows\$project\$project.csproj") `
        -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}
# Check nested MSIX files too: filtering loose PFX files is not sufficient.
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object Extension -in @('.pfx', '.p12', '.key')) {
    throw 'Private signing material found in publish output.'
}
$identity = Join-Path $publishDir 'SparsePackage\build\CidaWinIdentity.msix'
$archive = [IO.Compression.ZipFile]::OpenRead($identity)
try {
    if ($archive.Entries | Where-Object FullName -match '(?i)\.(pfx|p12|key|msix)$') {
        throw 'Private signing material or recursive package found inside identity package.'
    }
} finally { $archive.Dispose() }
vpk pack --packId Cida --packVersion $Version --packDir $publishDir --mainExe Cida.exe `
    --packTitle '辞达 Cida' --runtime win-x64 --channel $Channel --outputDir $packDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Write-Host "Release in $packDir; publish staging: $publishDir"
