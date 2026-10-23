# LaunchFlow - Windows installer builder. Uses the version in LaunchFlow.csproj.
# Checks .NET 8 SDK and Inno Setup. With explicit consent, installs missing
# build tools through the official Windows Package Manager (WinGet) catalog.
# This script builds the distributable installer; end users only need the .exe.
# Optional Authenticode: see CODE-SIGNING.md and tools\Sign-LaunchFlow.ps1.
[CmdletBinding()]
param(
    [string]$InnoCompiler = '',
    [switch]$NoAutoInstall,
    [switch]$Yes,
    [switch]$LaunchSetup
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
. (Join-Path $PSScriptRoot 'tools\Sign-LaunchFlow.ps1')

# Reject inconsistent application and setup versions before packaging.
[xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'LaunchFlow\LaunchFlow.csproj')
$projectVersion = [string](@($manifest.Project.PropertyGroup.Version | Where-Object { $_ })[0])
$issText = Get-Content (Join-Path $PSScriptRoot 'installer\LaunchFlow.iss') -Raw
$expected = '(?m)^#define AppVersion "' + [regex]::Escape($projectVersion) + '"\s*$'
if ($projectVersion -notmatch '^\d+\.\d+\.\d+$' -or $issText -notmatch $expected) {
    throw 'LaunchFlow application and Windows Setup versions are inconsistent.'
}


if ($env:OS -ne 'Windows_NT') {
    throw 'LaunchFlow installer builds require Windows 10 or Windows 11 (x64).'
}

function Refresh-ProcessPath {
    $parts = @(
        [Environment]::GetEnvironmentVariable('Path', 'Machine'),
        [Environment]::GetEnvironmentVariable('Path', 'User'),
        $env:PATH
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $env:PATH = $parts -join ';'
}

function Find-DotNetExe {
    $paths = [System.Collections.Generic.List[string]]::new()
    $found = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($found -and $found.Source) { $paths.Add($found.Source) }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ($root) {
            $paths.Add((Join-Path $root 'dotnet\dotnet.exe'))
            $paths.Add((Join-Path $root 'Microsoft\dotnet\dotnet.exe'))
        }
    }
    $fallback = $null
    foreach ($path in ($paths | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if (-not $fallback) { $fallback = $path }
        # Prefer an actual SDK installation if multiple dotnet.exe paths exist.
        if (Has-DotNet8Sdk -DotNetPath $path) { return $path }
    }
    return $fallback
}

function Has-DotNet8Sdk {
    param([string]$DotNetPath)
    if (-not $DotNetPath) { return $false }
    try {
        $installed = @(& $DotNetPath --list-sdks 2>$null)
        # A runtime alone does NOT contain the SDK required to build LaunchFlow.
        return [bool](@($installed | Where-Object { $_ -match '^8\.\d+\.\d+\s' }).Count -gt 0)
    }
    catch { return $false }
}

function Find-InnoCompiler {
    param([string]$ExplicitPath)
    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Custom Inno Setup compiler not found: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    if ($env:LAUNCHFLOW_ISCC -and (Test-Path -LiteralPath $env:LAUNCHFLOW_ISCC -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:LAUNCHFLOW_ISCC).Path
    }
    $candidates = [System.Collections.Generic.List[string]]::new()
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) { $candidates.Add($onPath.Source) }
    $roots = @(
        [Environment]::GetFolderPath('ProgramFilesX86'),
        [Environment]::GetFolderPath('ProgramFiles'),
        $env:LOCALAPPDATA,
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' })
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -Unique
    foreach ($root in $roots) {
        foreach ($version in @('6','7')) {
            $candidates.Add((Join-Path $root "Inno Setup $version\ISCC.exe"))
        }
        foreach ($directory in (Get-ChildItem -LiteralPath $root -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue)) {
            $candidates.Add((Join-Path $directory.FullName 'ISCC.exe'))
        }
    }
    foreach ($regKey in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $regKey)) { continue }
        foreach ($item in (Get-ChildItem -LiteralPath $regKey -ErrorAction SilentlyContinue)) {
            if ($item.PSChildName -notlike 'Inno Setup*') { continue }
            $props = Get-ItemProperty -LiteralPath $item.PSPath -ErrorAction SilentlyContinue
            if ($props -and $props.InstallLocation) {
                $candidates.Add((Join-Path $props.InstallLocation 'ISCC.exe'))
            }
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-WinGet {
    $command = Get-Command 'winget.exe' -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { return $command.Source }
    if ($env:LOCALAPPDATA) {
        $shim = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
        if (Test-Path -LiteralPath $shim -PathType Leaf) { return $shim }
    }
    return $null
}

function Install-FromWinGet {
    param([string]$WinGet, [string]$PackageId)
    Write-Host "Installing $PackageId from the official WinGet package catalog..." -ForegroundColor Cyan
    # WinGet checks each package against the manifest's installer hash.
    # It may display a Windows UAC prompt. No security controls are bypassed.
    & $WinGet install --id $PackageId --exact --source winget --accept-package-agreements --accept-source-agreements --silent
    if ($LASTEXITCODE -ne 0) {
        throw "WinGet could not install $PackageId (exit code $LASTEXITCODE). Check the WinGet output above, the network connection, and Windows permissions."
    }
    Refresh-ProcessPath
}

Write-Host ''
Write-Host '===========================================' -ForegroundColor Cyan
Write-Host '  LaunchFlow v$projectVersion - Installer Builder' -ForegroundColor Cyan
Write-Host '===========================================' -ForegroundColor Cyan
Write-Host 'Checking tools required to BUILD the installer...'
Refresh-ProcessPath
$dotnet = Find-DotNetExe
$hasSdk = Has-DotNet8Sdk -DotNetPath $dotnet
$inno = Find-InnoCompiler -ExplicitPath $InnoCompiler
$missing = @()
if (-not $hasSdk) { $missing += '.NET 8 SDK (Microsoft.DotNet.SDK.8)' }
if (-not $inno) { $missing += 'Inno Setup (JRSoftware.InnoSetup)' }

if ($hasSdk) { Write-Host '[OK] .NET 8 SDK found.' -ForegroundColor Green }
else { Write-Host '[MISSING] .NET 8 SDK' -ForegroundColor Yellow }
if ($inno) { Write-Host "[OK] Inno Setup found: $inno" -ForegroundColor Green }
else { Write-Host '[MISSING] Inno Setup compiler' -ForegroundColor Yellow }

$signToolPath = Get-LaunchFlowSignTool
if (Test-LaunchFlowSigningConfigured) {
    if ($signToolPath) {
        Write-Host "[OK] Authenticode configured (signtool: $signToolPath)" -ForegroundColor Green
    }
    else {
        Write-Host '[WARN] Signing env set but signtool.exe missing — install Windows SDK Signing Tools.' -ForegroundColor Yellow
    }
}
else {
    Write-Host '[INFO] No signing cert — setup will be unsigned. See CODE-SIGNING.md.' -ForegroundColor DarkGray
}

if ($missing.Count -gt 0) {
    if ($NoAutoInstall) {
        throw "Missing build tools: $($missing -join ', '). Auto-install disabled with -NoAutoInstall."
    }
    $winget = Find-WinGet
    if (-not $winget) {
        throw @'
A required build tool is missing and WinGet (Windows Package Manager) is unavailable.
Install Microsoft's App Installer / WinGet from https://aka.ms/getwinget and rerun
BUILD-INSTALLER.cmd. On company-managed computers, ask your administrator.
'@
    }
    Write-Host ''
    Write-Host 'Build tools missing (needed ONLY on this PC to create the setup .exe):' -ForegroundColor Yellow
    foreach ($item in $missing) { Write-Host "  - $item" }
    Write-Host 'Installing from the official WinGet catalog (one-time on the builder PC).'
    Write-Host 'Windows may show a UAC / admin prompt — that is normal.'
    if (-not $Yes) {
        $answer = Read-Host 'Install the missing tools now? [Y/N]'
        if ($answer -notmatch '^(?i:y|yes)$') {
            throw 'Installation declined. No missing developer tools were installed.'
        }
    }
    else {
        Write-Host 'Auto-accepted (-Yes). Continuing…' -ForegroundColor Cyan
    }
    if (-not $hasSdk) {
        Install-FromWinGet -WinGet $winget -PackageId 'Microsoft.DotNet.SDK.8'
        $dotnet = Find-DotNetExe
        if (-not (Has-DotNet8Sdk -DotNetPath $dotnet)) {
            throw '.NET SDK installation finished but .NET 8 SDK could not be detected. Close this window and rerun the CMD builder.'
        }
        Write-Host '[OK] .NET 8 SDK installed.' -ForegroundColor Green
    }
    if (-not $inno) {
        Install-FromWinGet -WinGet $winget -PackageId 'JRSoftware.InnoSetup'
        $inno = Find-InnoCompiler -ExplicitPath $InnoCompiler
        if (-not $inno) {
            throw 'Inno Setup installation finished but ISCC.exe was not found. Close this window and rerun; or pass -InnoCompiler with the ISCC.exe path.'
        }
        Write-Host '[OK] Inno Setup installed.' -ForegroundColor Green
    }
}

if (-not $dotnet -or -not (Has-DotNet8Sdk -DotNetPath $dotnet)) {
    throw '.NET 8 SDK is still missing; cannot build LaunchFlow.'
}
if (-not $inno) { throw 'Inno Setup is still missing; cannot build LaunchFlow installer.' }

Write-Host ''
Write-Host 'Publishing LaunchFlow for Windows x64 (self-contained, ReadyToRun)...'
$publishDir = Join-Path $PSScriptRoot 'publish'
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
& $dotnet publish (Join-Path $PSScriptRoot 'LaunchFlow\LaunchFlow.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed. Check build errors above.' }

# Optional Authenticode (SmartScreen). See CODE-SIGNING.md.
#   LAUNCHFLOW_SIGN_PFX + LAUNCHFLOW_SIGN_PFX_PASSWORD
#   LAUNCHFLOW_SIGN_THUMBPRINT  or  LAUNCHFLOW_SIGN_SUBJECT
Write-Host 'Signing published app binaries (if a certificate is configured)...'
$appExe = Join-Path $publishDir 'LaunchFlow.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Publish succeeded but LaunchFlow.exe was not found in $publishDir"
}
Invoke-LaunchFlowSign -PathToSign $appExe | Out-Null

Write-Host 'Creating LaunchFlow Windows setup executable...'
$iss = Join-Path $PSScriptRoot 'installer\LaunchFlow.iss'
$innoArgs = [System.Collections.Generic.List[string]]::new()
$innoSign = Get-LaunchFlowInnoSignToolDefinition
if ($innoSign) {
    # Enables #ifdef LfSign → SignTool=lfsign in LaunchFlow.iss; $f is Inno's file placeholder.
    $innoArgs.Add('/DLfSign=1')
    $innoArgs.Add("/Slfsign=$innoSign")
    Write-Host 'Inno SignTool (lfsign) enabled for setup + uninstaller.' -ForegroundColor Cyan
}
else {
    Write-Host 'Inno SignTool skipped (no cert or signtool) — unsigned setup is OK for local use.' -ForegroundColor DarkGray
}
$innoArgs.Add($iss)
& $inno @innoArgs
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed. Check errors above.' }

$installer = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'dist') -Filter 'LaunchFlow-Setup-*.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $installer) {
    throw "Inno Setup returned successfully but no LaunchFlow-Setup-*.exe was found in dist\"
}
# Fallback if Inno did not sign (no /Slfsign=): sign the outer setup.exe here.
Invoke-LaunchFlowSign -PathToSign $installer -QuietIfUnsigned | Out-Null

Write-Host ''
Write-Host '===========================================' -ForegroundColor Green
Write-Host "  APP INSTALLER READY" -ForegroundColor Green
Write-Host "  $installer" -ForegroundColor Green
Write-Host '===========================================' -ForegroundColor Green
Write-Host ''
Write-Host 'THIS is the file to install / share:' -ForegroundColor Cyan
Write-Host "  $(Split-Path $installer -Leaf)" -ForegroundColor Cyan
Write-Host 'Double-click that setup .exe. It does NOT ask for .NET SDK or Inno Setup.'
Write-Host 'Do not send BUILD-INSTALLER to other people — only the setup .exe in dist\.'
if ($LaunchSetup) {
    Write-Host 'Opening the app installer…'
    Start-Process -FilePath $installer
}
