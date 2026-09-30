# Builds and registers the sparse identity package (packaging with external location),
# the route that makes Windows.Media.Ocr callable from the unpackaged Cida.exe.
# See https://learn.microsoft.com/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps

param(
    [string]$ExternalLocation = (Split-Path -Parent $PSScriptRoot),
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$kits = "C:\Program Files (x86)\Windows Kits\10\bin"
$version = (Get-ChildItem $kits | Sort-Object Name -Descending | Where-Object Name -match '^\d').Name | Select-Object -First 1
$makeappx = Join-Path $kits "$version\x64\makeappx.exe"
$signtool = Join-Path $kits "$version\x64\signtool.exe"

$sparseDir = Join-Path $PSScriptRoot "..\windows\Cida.Desktop\SparsePackage"
$buildDir = Join-Path $sparseDir "build"
$package = Join-Path $buildDir "CidaWinIdentity.msix"
$cert = Join-Path $buildDir "CidaIdentity.pfx"

if ($Remove) {
    powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-AppxPackage Cida.Win | Remove-AppxPackage"
    Write-Host "sparse package removed"
    exit 0
}

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

# 1. Pack the manifest (with assets) into an .msix; /nv skips validating referenced paths.
& $makeappx pack /o /d $sparseDir /nv /p $package
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

# 2. Self-signed cert whose subject matches the manifest publisher; trusted for dev.
if (-not (Test-Path $cert)) {
    $password = ConvertTo-SecureString -String "cida-dev" -Force -AsPlainText
    New-SelfSignedCertificate -Type Custom -Subject "CN=Cida Windows" `
        -KeyUsage DigitalSignature -FriendlyName "Cida identity dev cert" `
        -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") | Out-Null
    $dev = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq "CN=Cida Windows" | Select-Object -First 1
    Export-PfxCertificate -Cert $dev -FilePath $cert -Password $password | Out-Null
    # Trust the public half so Add-AppxPackage accepts the self-signed package.
    Export-Certificate -Cert $dev -FilePath (Join-Path $buildDir "CidaIdentity.cer") | Out-Null
    Import-Certificate -FilePath (Join-Path $buildDir "CidaIdentity.cer") `
        -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}
& $signtool sign /fd SHA256 /a /f $cert /p cida-dev $package
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

# 3. Register for the current user with the external location.
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `
    "Add-AppxPackage -Path '$package' -ExternalLocation '$ExternalLocation'"

Write-Host "sparse package registered for $ExternalLocation"
Write-Host "verify: Get-AppxPackage Cida.Win"
