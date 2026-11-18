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
    Test-Case 'clean source archive contains tracked files and no private vault' {
        $safeZip = Join-Path $root 'safe-source.zip'
        & (Join-Path $PSScriptRoot '../../MAKE-SAFE-ZIP.ps1') -Kind Source -OutputPath $safeZip
        if (-not (Test-Path -LiteralPath $safeZip -PathType Leaf)) {
            throw 'Share-ZIP creator did not produce an archive.'
        }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($safeZip)
        try {
            $entries = @($archive.Entries | ForEach-Object FullName)
            if ($entries -notcontains 'LaunchFlow/LaunchFlow.csproj' -or
                $entries -notcontains 'MAKE-SAFE-ZIP.ps1') {
                throw 'Source archive did not contain complete tracked source.'
            }
            foreach ($entry in $entries) {
                if ($entry -match '(?i)(^|/)(profiles-v2|profiles-sealed|recovery)(/|$)' -or
                    $entry -match '(?i)\.(db|db-wal|db-shm|launchflow|kdbx)$') {
                    throw "Private vault or credential backup appeared in source ZIP: $entry"
                }
            }
        }
        finally { $archive.Dispose() }
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
    # No portable archive or setup may accidentally redistribute a developer's
    # vault, saved credentials, recovery copy or browser session.
    foreach ($relative in @(
        'launchflow.db',
        'launchflow.db-wal',
        'subfolder/launchflow.db',
        'backup.launchflow',
        'someone.kdbx',
        'hello-unlock.bin',
        'profiles-v2/site/profile.json',
        'profiles-sealed/site.profile.aes',
        'recovery/vault.db',
        'startup-diagnostics.log',
        '.env'
    )) {
        Test-Case "rejects packaged private user data $relative" {
            $bad = Join-Path $payload $relative
            New-Item -ItemType Directory -Path (Split-Path $bad -Parent) -Force | Out-Null
            Set-Content -LiteralPath $bad -Value 'synthetic-private-fixture'
            try { Expect-Failure { Assert-LaunchFlowPayload -PublishDirectory $payload } }
            finally { Remove-Item -LiteralPath $bad -Force }
        }
    }
    Test-Case 'clean fixture remains distributable after private-file rejections' {
        Assert-LaunchFlowPayload -PublishDirectory $payload
    }
    Test-Case 'Inno staging preserves Firefox nested DLLs and hidden Playwright files' {
        $firefox = Join-Path $payload 'playwright-browsers/firefox-1543/firefox'
        $layout = Join-Path $firefox 'installation_dir_layout'
        New-Item -ItemType Directory -Path $layout -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $firefox 'firefox.exe') -Value 'synthetic Firefox exe'
        Set-Content -LiteralPath (Join-Path $layout 'VersionedInstallationDirLayout.dll') -Value 'synthetic Firefox DLL'
        $stage = $null
        try {
            $stage = New-LaunchFlowInnoStage -PublishDirectory $payload
            foreach ($relative in @(
                '.playwright/node/win32_x64/node.exe',
                'playwright-browsers/firefox-1543/firefox/firefox.exe',
                'playwright-browsers/firefox-1543/firefox/installation_dir_layout/VersionedInstallationDirLayout.dll'
            )) {
                if (-not (Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf)) {
                    throw "Short Inno stage dropped $relative"
                }
            }
        }
        finally {
            if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
            Remove-Item -LiteralPath (Join-Path $payload 'playwright-browsers') -Recurse -Force
        }
    }
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
