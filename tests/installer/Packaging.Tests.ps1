[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../BUILD-PACKAGING-HELPERS.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('LaunchFlow-PackagingTests-' + [guid]::NewGuid().ToString('N'))
$passed = 0
$failed = 0
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed++; Write-Host "FAIL ${Name}: $_" }
}
function Expect-Failure([scriptblock]$Action) {
    $thrown = $false
    try { & $Action } catch { $thrown = $true }
    if (-not $thrown) { throw 'Expected packaging to fail closed.' }
}
try {
    Test-Case 'build-only does not require release authorization' {
        Assert-LaunchFlowDraftRelease -Requested $false
    }
    Test-Case 'accepts exact version and commit bound draft authorization' {
        $sha = 'a' * 40
        Assert-LaunchFlowDraftRelease -Requested $true -Version '0.7.11' -Commit $sha -Confirmation "DRAFT-v0.7.11@$sha" -WindowsEvidence 'https://example.invalid/windows-test-record'
    }
    foreach ($bad in @(
        @{ Confirmation = ''; Evidence = '' },
        @{ Confirmation = 'DRAFT-v0.7.10@' + ('a' * 40); Evidence = 'https://example.invalid/record' },
        @{ Confirmation = 'DRAFT-v0.7.11@' + ('b' * 40); Evidence = 'https://example.invalid/record' },
        @{ Confirmation = 'DRAFT-v0.7.11@' + ('a' * 40); Evidence = '' }
    )) {
        Test-Case "rejects unbound draft release authorization $($bad.Confirmation)" {
            Expect-Failure { Assert-LaunchFlowDraftRelease -Requested $true -Version '0.7.11' -Commit ('a' * 40) -Confirmation $bad.Confirmation -WindowsEvidence $bad.Evidence }
        }
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Test-Case 'exact artifact path ignores newer stale installer' {
        $dist = New-Item -ItemType Directory -Path (Join-Path $root 'dist') -Force
        Set-Content -LiteralPath (Join-Path $dist 'LaunchFlow-Setup-v99.0.0-x64.exe') -Value 'synthetic stale artifact'
        $actual = Get-LaunchFlowSetupPath -Root $root -Version '0.7.11'
        if ($actual -ne (Join-Path $root 'dist/LaunchFlow-Setup-v0.7.11-x64.exe')) { throw 'Selected wrong artifact.' }
        if (Test-Path -LiteralPath $actual) { throw 'Fell back to stale setup.' }
    }
    foreach ($version in @('00.7.11', 'v0.7.11', "0.7.11`n", '0.7', '0.7.11.0', '../setup', '2147483648.7.11')) {
        Test-Case "rejects invalid artifact version $version" { Expect-Failure { Get-LaunchFlowSetupPath -Root $root -Version $version } }
    }
    # Tiny synthetic files exercise the assertions, not a deployable Windows payload.
    $payload = Join-Path $root 'payload'
    $required = @(
        'LaunchFlow.exe', 'LaunchFlow.dll', 'LaunchFlow.deps.json', 'LaunchFlow.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'WindowsBase.dll', 'Microsoft.Playwright.dll', 'e_sqlite3.dll',
        'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        '.playwright/node/win32_x64/node.exe', '.playwright/node/LICENSE',
        '.playwright/package/cli.js', '.playwright/package/package.json', '.playwright/package/browsers.json',
        '.playwright/package/lib/coreBundle.js', '.playwright/package/lib/utilsBundle.js',
        '.playwright/package/lib/serverRegistry.js', '.playwright/package/lib/bootstrap.js',
        '.playwright/package/lib/package.js', '.playwright/package/lib/utilsBundle.js.LICENSE',
        '.playwright/package/lib/serverRegistry.js.LICENSE', '.playwright/package/LICENSE', '.playwright/package/NOTICE'
    )
    foreach ($relative in $required) {
        $path = Join-Path $payload $relative
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        Set-Content -LiteralPath $path -Value 'synthetic fixture'
    }
    $config = '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.33"},{"name":"Microsoft.WindowsDesktop.App","version":"8.0.33"}]}}'
    Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.runtimeconfig.json') -Value $config
    Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.deps.json') -Value '{"runtimeTarget":{"name":".NETCoreApp,Version=v8.0/win-x64"}}'
    Test-Case 'accepts complete self-contained fixture' { Assert-LaunchFlowPayload -PublishDirectory $payload }
    foreach ($relative in $required) {
        Test-Case "rejects missing $relative" {
            $path = Join-Path $payload $relative
            Move-Item -LiteralPath $path -Destination "$path.saved"
            try { Expect-Failure { Assert-LaunchFlowPayload -PublishDirectory $payload } }
            finally { Move-Item -LiteralPath "$path.saved" -Destination $path }
        }
    }
    Test-Case 'rejects malformed bundled runtime version' {
        Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.runtimeconfig.json') -Value ($config.Replace('8.0.33', '8.not-a-version'))
        try { Expect-Failure { Assert-LaunchFlowPayload -PublishDirectory $payload } }
        finally { Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.runtimeconfig.json') -Value $config }
    }
    . (Join-Path $PSScriptRoot 'Builder.Tests.ps1')
    Test-LaunchFlowBuilder -PayloadDirectory $payload -TestRoot $root
    . (Join-Path $PSScriptRoot 'Workflow.Tests.ps1')
    Test-LaunchFlowDraftWorkflow -TestRoot $root
    Test-Case 'rejects framework-dependent runtime config' {
        Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.runtimeconfig.json') -Value '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"8.0.33"}}}'
        try { Expect-Failure { Assert-LaunchFlowPayload -PublishDirectory $payload } }
        finally { Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.runtimeconfig.json') -Value $config }
    }
    Test-Case 'rejects incorrect publish architecture' {
        Set-Content -LiteralPath (Join-Path $payload 'LaunchFlow.deps.json') -Value '{"runtimeTarget":{"name":".NETCoreApp,Version=v8.0/linux-x64"}}'
        Expect-Failure { Assert-LaunchFlowPayload -PublishDirectory $payload }
    }
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
@{ total = $passed + $failed; passed = $passed; failed = $failed } | ConvertTo-Json -Compress
if ($failed) { exit 1 }
