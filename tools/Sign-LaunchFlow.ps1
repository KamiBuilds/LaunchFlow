# Shared Authenticode helpers for LaunchFlow.
# Dot-source from BUILD-INSTALLER.ps1:
#   . (Join-Path $PSScriptRoot 'tools\Sign-LaunchFlow.ps1')
#
# Env (any one signing mode):
#   LAUNCHFLOW_SIGN_PFX + LAUNCHFLOW_SIGN_PFX_PASSWORD
#   LAUNCHFLOW_SIGN_THUMBPRINT   (cert already in CurrentUser\My or LocalMachine\My)
#   LAUNCHFLOW_SIGN_SUBJECT      (CN=... substring match in CurrentUser\My)
# Optional:
#   LAUNCHFLOW_SIGN_TIMESTAMP_URL  (default DigiCert)

function Get-LaunchFlowSignTool {
    if ($env:LAUNCHFLOW_SIGNTOOL -and (Test-Path -LiteralPath $env:LAUNCHFLOW_SIGNTOOL -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:LAUNCHFLOW_SIGNTOOL).Path
    }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $found = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match '\\x64$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
        if ($found) { return $found }
    }
    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) { return $onPath.Source }
    return $null
}

function Test-LaunchFlowSigningConfigured {
    if ($env:LAUNCHFLOW_SIGN_THUMBPRINT) { return $true }
    if ($env:LAUNCHFLOW_SIGN_SUBJECT) { return $true }
    if ($env:LAUNCHFLOW_SIGN_PFX -and (Test-Path -LiteralPath $env:LAUNCHFLOW_SIGN_PFX -PathType Leaf)) { return $true }
    return $false
}

function Get-LaunchFlowTimestampUrl {
    if ($env:LAUNCHFLOW_SIGN_TIMESTAMP_URL) { return $env:LAUNCHFLOW_SIGN_TIMESTAMP_URL }
    return 'http://timestamp.digicert.com'
}

# Returns an argument list (string[]) for: & signtool @args
# Does NOT include the path-to-sign; caller appends it (or $f for Inno).
function Get-LaunchFlowSignToolArgs {
    $tr = Get-LaunchFlowTimestampUrl
    $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $tr)
    if ($env:LAUNCHFLOW_SIGN_THUMBPRINT) {
        $args += @('/sha1', $env:LAUNCHFLOW_SIGN_THUMBPRINT)
        return $args
    }
    if ($env:LAUNCHFLOW_SIGN_SUBJECT) {
        $args += @('/n', $env:LAUNCHFLOW_SIGN_SUBJECT)
        return $args
    }
    if ($env:LAUNCHFLOW_SIGN_PFX -and (Test-Path -LiteralPath $env:LAUNCHFLOW_SIGN_PFX -PathType Leaf)) {
        $args += @('/f', $env:LAUNCHFLOW_SIGN_PFX)
        if ($env:LAUNCHFLOW_SIGN_PFX_PASSWORD) {
            $args += @('/p', $env:LAUNCHFLOW_SIGN_PFX_PASSWORD)
        }
        return $args
    }
    return $null
}

# Builds the Inno Setup /Slfsign= value. Inno replaces $f with the file path.
# Example: /Slfsign="C:\...\signtool.exe sign /fd SHA256 ... $f"
function Get-LaunchFlowInnoSignToolDefinition {
    $signtool = Get-LaunchFlowSignTool
    $signArgs = Get-LaunchFlowSignToolArgs
    if (-not $signtool -or -not $signArgs) { return $null }
    # Quote paths that may contain spaces; keep $f as Inno's placeholder.
    $quotedTool = '"' + $signtool + '"'
    $parts = foreach ($a in $signArgs) {
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '""') + '"' } else { $a }
    }
    return ($quotedTool + ' ' + ($parts -join ' ') + ' $f')
}

function Invoke-LaunchFlowSign {
    param(
        [Parameter(Mandatory)][string]$PathToSign,
        [switch]$QuietIfUnsigned
    )
    if (-not (Test-Path -LiteralPath $PathToSign -PathType Leaf)) {
        throw "File to sign not found: $PathToSign"
    }
    $signtool = Get-LaunchFlowSignTool
    if (-not $signtool) {
        if (-not $QuietIfUnsigned) {
            Write-Host 'signtool.exe not found (install Windows SDK Signing Tools); skipping Authenticode.' -ForegroundColor Yellow
        }
        return $false
    }
    $signArgs = Get-LaunchFlowSignToolArgs
    if (-not $signArgs) {
        if (-not $QuietIfUnsigned) {
            Write-Host 'No signing cert configured — setup will be unsigned (SmartScreen may warn). See CODE-SIGNING.md.' -ForegroundColor Yellow
        }
        return $false
    }
    & $signtool @signArgs $PathToSign
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $PathToSign (exit $LASTEXITCODE)" }
    Write-Host "Signed: $PathToSign" -ForegroundColor Green
    return $true
}
