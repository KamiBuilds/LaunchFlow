# Build this universal installer ONCE. It always fetches the newest published release afterward.
# Requires .NET 8 SDK on the developer's Windows PC. End users do not need any SDK.
param([string]$Repository = "")
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
. (Join-Path $PSScriptRoot 'tools\Sign-LaunchFlow.ps1')

$sourceFile = Join-Path $PSScriptRoot 'bootstrapper\release-source.txt'
if (-not $Repository) {
  if (Test-Path $sourceFile) {
    $previous = (Get-Content $sourceFile -Raw).Trim()
    if ($previous -notmatch '^SET-YOUR-') { $Repository = $previous }
  }
}
if (-not $Repository) {
  Write-Host 'The universal installer needs your PUBLIC GitHub repository.' -ForegroundColor Cyan
  Write-Host 'Example: myaccount/LaunchFlow'
  $Repository = Read-Host 'GitHub repository (OWNER/REPO)'
}
if ($Repository -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9_.-]*/[a-zA-Z0-9][a-zA-Z0-9_.-]*\z' -or $Repository -match '^SET-YOUR-') {
  throw 'Invalid repository. Use OWNER/REPO, for example myaccount/LaunchFlow.'
}

Set-Content -Path $sourceFile -Value $Repository -Encoding utf8
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
  throw '.NET 8 SDK is required ONLY to create the universal installer once. Install .NET 8 SDK on your developer PC, or build it with the provided GitHub Actions workflow.'
}
$installedSdk = @(& dotnet --list-sdks) -join "`n"
if ($LASTEXITCODE -ne 0 -or $installedSdk -notmatch '(?m)^8\.') {
  throw '.NET 8 SDK is required for this one-time developer build. End-user PCs will not need it.'
}

$publish = Join-Path $PSScriptRoot 'dist\universal-publish'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null
$destination = Join-Path $PSScriptRoot 'dist\LaunchFlow-Universal-Installer.exe'
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
Write-Host "Building a self-contained LaunchFlow universal installer for $Repository..." -ForegroundColor Cyan
& dotnet publish '.\bootstrapper\LaunchFlow.UniversalInstaller.csproj' `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Universal installer compilation failed.' }
$exe = Join-Path $publish 'LaunchFlow.UniversalInstaller.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or (Get-Item -LiteralPath $exe).Length -eq 0) { throw 'Published universal installer was not found.' }
Copy-Item -LiteralPath $exe -Destination $destination -Force
Invoke-LaunchFlowSign -PathToSign $destination -QuietIfUnsigned | Out-Null
Write-Host "`nUNIVERSAL INSTALLER READY: $destination" -ForegroundColor Green
Write-Host 'Share this exact EXE; keep a copy of the versioned setup for offline installation. It checks the newest published release on every launch.'
Write-Host 'Important: FIRST publish versioned LaunchFlow setup files to that GitHub repository.' -ForegroundColor Yellow
