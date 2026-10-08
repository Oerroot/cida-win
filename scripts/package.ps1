# Self-contained x64 candidate packages; no certificates or private keys are installed.
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$')][string]$Version,
    [string]$Channel,
    [string]$CrtDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Channel) { $Channel = if ($Version.Contains('-')) { 'win-x64-preview' } else { 'win-x64' } }
$publishDir = Join-Path $repoRoot ('artifacts\publish\' + [guid]::NewGuid().ToString('N'))
$packDir = Join-Path $repoRoot "artifacts\release\$Version"
if ((Test-Path $packDir) -and (Get-ChildItem $packDir -File)) { throw "Existing release output preserved: $packDir. Choose a new version or archive it explicitly." }
foreach ($project in @('Cida.Desktop', 'Cida.Cli')) {
    dotnet publish (Join-Path $repoRoot "windows\$project\$project.csproj") -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}
# Tesseract/Leptonica require the Microsoft VC runtime. Use licensed VS redistributables,
# verify Microsoft signatures and copy them app-locally; never take DLLs from System32.
if (-not $CrtDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Visual Studio VC redistributables not found; supply -CrtDirectory.' }
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $redist = Join-Path $install 'VC\Redist\MSVC'
    $latest = Get-ChildItem $redist -Directory | Where-Object Name -match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $latest) { throw 'VC redistributable version missing.' }
    $CrtDirectory = Join-Path $latest.FullName 'x64\Microsoft.VC143.CRT'
}
$crtFiles = @(Get-ChildItem -LiteralPath $CrtDirectory -File -Filter '*.dll')
foreach ($required in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    if ($required -notin $crtFiles.Name) { throw "Missing VC runtime: $required" }
}
$runtimeManifest = foreach ($file in $crtFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') { throw "Untrusted VC redistributable: $($file.Name)" }
    Copy-Item -LiteralPath $file.FullName -Destination $publishDir
    [ordered]@{ file = $file.Name; version = $file.VersionInfo.FileVersion; sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); signer = $signature.SignerCertificate.Subject }
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE'), (Join-Path $repoRoot 'LICENSE') -Destination $publishDir
$runtimeManifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $publishDir 'vc-runtime-provenance.json') -Encoding UTF8
@("Cida Windows $Version", 'UNSIGNED RELEASE CANDIDATE / 未签名候选版', 'Installers and application executables have no trusted publisher signature.', 'Do not install any development certificates. OCR works offline without MSIX identity.', 'Source and verification: https://github.com/Oerroot/cida-win') | Set-Content (Join-Path $publishDir 'CANDIDATE.txt') -Encoding UTF8
if (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object Extension -in @('.pfx', '.p12', '.key', '.cer', '.msix')) { throw 'Signing/identity material found in candidate output.' }
foreach ($required in @('Cida.exe', 'Cida.Cli.exe', 'tessdata\eng.traineddata', 'tessdata\chi_sim.traineddata', 'tessdata\chi_tra.traineddata')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Incomplete output: $required" }
}
vpk pack --packId Cida --packVersion $Version --packDir $publishDir --mainExe Cida.exe --packTitle '辞达 Cida（候选版）' --icon (Join-Path $repoRoot 'windows\Cida.Desktop\Assets\Brand\Cida.ico') --runtime win-x64 --channel $Channel --outputDir $packDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Copy-Item -LiteralPath (Join-Path $publishDir 'CANDIDATE.txt') -Destination $packDir
Get-ChildItem $packDir -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content (Join-Path $packDir 'SHA256SUMS.txt') -Encoding UTF8
Write-Host "Candidate release: $packDir; staging: $publishDir"
