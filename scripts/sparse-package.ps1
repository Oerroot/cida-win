# Build a sparse identity package from an explicit manifest/assets allowlist.
# Private keys remain non-exportable in CurrentUser\My, outside the repository.
param(
    [string]$ExternalLocation = (Split-Path -Parent $PSScriptRoot),
    [string]$CertificateThumbprint,
    [switch]$Register,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Remove) {
    Get-AppxPackage Cida.Win | Remove-AppxPackage
    return
}
$kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
$kitVersion = (Get-ChildItem -LiteralPath $kits | Where-Object Name -match '^\d' | Sort-Object Name -Descending | Select-Object -First 1).Name
if (-not $kitVersion) { throw 'Windows SDK tools are required to build the identity package.' }
$makeappx = Join-Path $kits "$kitVersion\x64\makeappx.exe"
$signtool = Join-Path $kits "$kitVersion\x64\signtool.exe"
$sparseDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\windows\Cida.Desktop\SparsePackage'))
$buildDir = Join-Path $sparseDir 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$package = Join-Path $buildDir 'CidaWinIdentity.msix'
$publicCertificate = Join-Path $buildDir 'CidaIdentity.cer'
$signingDir = Join-Path $env:LOCALAPPDATA 'Cida\Signing'
$thumbprintPath = Join-Path $signingDir 'dev-thumbprint.txt'
if (-not $CertificateThumbprint) {
    if (Test-Path -LiteralPath $thumbprintPath) {
        $CertificateThumbprint = (Get-Content -LiteralPath $thumbprintPath -Raw).Trim()
    }
    if (-not $CertificateThumbprint -or -not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint")) {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=Cida Windows' `
            -KeyUsage DigitalSignature -FriendlyName 'Cida identity development (non-exportable)' `
            -KeyExportPolicy NonExportable -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $CertificateThumbprint = $certificate.Thumbprint
        New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
        Set-Content -LiteralPath $thumbprintPath -Value $CertificateThumbprint
    }
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if ($certificate.Subject -ne 'CN=Cida Windows' -or -not $certificate.HasPrivateKey) {
    throw 'The signing certificate must have subject CN=Cida Windows and a private key.'
}
Export-Certificate -Cert $certificate -FilePath $publicCertificate -Force | Out-Null
$mapping = Join-Path $buildDir 'identity-files.txt'
@"
[Files]
"$(Join-Path $sparseDir 'AppxManifest.xml')" "AppxManifest.xml"
"$(Join-Path $sparseDir 'Assets\logo.png')" "Assets\logo.png"
"@ | Set-Content -LiteralPath $mapping -Encoding utf8
try {
    & $makeappx pack /o /f $mapping /nv /p $package
    if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
    & $signtool sign /fd SHA256 /s My /sha1 $CertificateThumbprint $package
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
} finally {
    Remove-Item -LiteralPath $mapping -ErrorAction SilentlyContinue
}
if ($Register) {
    Import-Certificate -FilePath $publicCertificate -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
    Add-AppxPackage -Path $package -ExternalLocation $ExternalLocation
}
Write-Host "Identity package built: $package (registration requested: $Register)"
