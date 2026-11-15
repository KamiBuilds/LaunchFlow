param(
    [Parameter(Mandatory=$true)][string]$SetupPath,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows installer smoke test requires a real Windows runner.' }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$install = Join-Path $env:LOCALAPPDATA 'Programs\LaunchFlow'
$data = Join-Path $env:LOCALAPPDATA 'LaunchFlow'
if ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $data)) {
    throw 'Refusing installer smoke: this user already has application files or a vault directory.'
}
function Run-Installer([string]$file, [string]$operation) {
    $process = Start-Process -FilePath $file -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-'
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$operation exited with code $($process.ExitCode)."
    }
}
$exe = Join-Path $install 'LaunchFlow.exe'
$uninstaller = Join-Path $install 'unins000.exe'
$marker = Join-Path $data 'ci-disposable-retention-fixture.bin'
$syntheticBytes = [byte[]](0..31 | ForEach-Object { [byte](($_ * 7 + 31) % 256) })
Run-Installer $setup 'Silent per-user installation'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $uninstaller -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $install 'LaunchFlow.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $install '.playwright') -PathType Container)) {
    throw 'Installed application, uninstaller or runtime/driver content is missing.'
}
$firefoxRoot = Join-Path $install 'playwright-browsers'
$firefoxExe = @(Get-ChildItem -LiteralPath $firefoxRoot -Recurse -Filter 'firefox.exe' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Length -gt 0 })
if (-not (Test-Path -LiteralPath $firefoxRoot -PathType Container) -or $firefoxExe.Count -eq 0) {
    throw 'Installed payload is missing playwright-browsers Firefox automation (firefox.exe).'
}
if (-not $exe.StartsWith($env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installation escaped the intended per-user LocalAppData scope.'
}
$installedVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($installedVersion -and -not $installedVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
    throw "Installed executable reports unexpected product version $installedVersion."
}
$probe = Start-Process -FilePath $exe -ArgumentList @('--exit-for-install') -PassThru
if (-not $probe.WaitForExit(20000)) {
    $probe.Kill()
    throw 'Installed executable did not complete the no-vault installer-shutdown entrypoint.'
}
if ($probe.ExitCode -ne 0) {
    throw "Installed application shutdown probe exited with code $($probe.ExitCode)."
}
Write-Host 'PASS native Windows: silent per-user install, installed identity and no-vault executable startup path.'
if (Test-Path -LiteralPath $data) {
    $existing = @(Get-ChildItem -LiteralPath $data -Force | Where-Object { $_.Name -ne 'startup-diagnostics.log' })
    if ($existing.Count -gt 0) {
        throw 'Unexpected non-fixture user data appeared during the installed executable probe.'
    }
}
New-Item -Path $data -ItemType Directory -Force | Out-Null
[System.IO.File]::WriteAllBytes($marker, $syntheticBytes)
$before = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
Run-Installer $setup 'Silent repair install'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $before) {
    throw 'Repair reinstall replaced application data or did not preserve the executable.'
}
Write-Host 'PASS native Windows: same-version silent repair retains synthetic data.'
Run-Installer $uninstaller 'Silent uninstall'
if ((Test-Path -LiteralPath $exe) -or
    -not (Test-Path -LiteralPath $marker -PathType Leaf) -or
    (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $before) {
    throw 'Silent uninstall did not preserve data or failed to remove installed executable.'
}
Write-Host 'PASS native Windows: silent per-user uninstall removes program and preserves synthetic user data.'
exit 0
