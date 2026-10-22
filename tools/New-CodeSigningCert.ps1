# Creates a local code-signing certificate for LaunchFlow builds.
# Self-signed certs quiet some Windows warnings on YOUR PC only — they do NOT
# establish SmartScreen reputation for other users. Buy a real Authenticode
# certificate (or use an EV cert / Azure Trusted Signing) for public releases.
#
# Usage (elevated PowerShell recommended for CurrentUser\My store):
#   .\tools\New-CodeSigningCert.ps1
#   .\tools\New-CodeSigningCert.ps1 -Subject "CN=Your Company" -OutPfx .\certs\launchflow-codesign.pfx
#
# Then either:
#   $env:LAUNCHFLOW_SIGN_PFX = (Resolve-Path .\certs\launchflow-codesign.pfx).Path
#   $env:LAUNCHFLOW_SIGN_PFX_PASSWORD = '...'
# or:
#   $env:LAUNCHFLOW_SIGN_THUMBPRINT = '<SHA1 from this script>'
#
# See CODE-SIGNING.md.
[CmdletBinding()]
param(
    [string]$Subject = 'CN=LaunchFlow Local Code Signing',
    [string]$OutPfx = '',
    [securestring]$Password,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser',
    [int]$ValidYears = 3
)
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Certificate creation requires Windows (New-SelfSignedCertificate).'
}

if (-not $OutPfx) {
    $certsDir = Join-Path $PSScriptRoot '..\certs'
    if (-not (Test-Path -LiteralPath $certsDir)) {
        New-Item -ItemType Directory -Path $certsDir | Out-Null
    }
    $OutPfx = Join-Path $certsDir 'launchflow-codesign.pfx'
}

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'PFX password (remember this; set LAUNCHFLOW_SIGN_PFX_PASSWORD)'
}

$notAfter = (Get-Date).AddYears($ValidYears)
Write-Host "Creating code-signing certificate: $Subject" -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\$StoreLocation\My" `
    -NotAfter $notAfter `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

Export-PfxCertificate -Cert $cert -FilePath $OutPfx -Password $Password | Out-Null

$thumb = $cert.Thumbprint
Write-Host ''
Write-Host 'Certificate ready.' -ForegroundColor Green
Write-Host "  Subject:     $($cert.Subject)"
Write-Host "  Thumbprint:  $thumb"
Write-Host "  PFX:         $((Resolve-Path -LiteralPath $OutPfx).Path)"
Write-Host "  Valid until: $($cert.NotAfter.ToString('u'))"
Write-Host ''
Write-Host 'For the next BUILD-INSTALLER.ps1 run in this shell:' -ForegroundColor Cyan
Write-Host "  `$env:LAUNCHFLOW_SIGN_PFX = '$((Resolve-Path -LiteralPath $OutPfx).Path)'"
Write-Host "  `$env:LAUNCHFLOW_SIGN_PFX_PASSWORD = '<the password you entered>'"
Write-Host 'Or use the store:'
Write-Host "  `$env:LAUNCHFLOW_SIGN_THUMBPRINT = '$thumb'"
Write-Host ''
Write-Host 'Self-signed = local testing only. Public downloads need a purchased Authenticode cert.' -ForegroundColor Yellow
Write-Host 'See CODE-SIGNING.md for GitHub Actions secrets and SmartScreen notes.'
