# Shared packaging assertions; no network or machine installation side effects.
function Get-LaunchFlowSetupPath {
    param([string]$Root, [string]$Version)
    $parsedVersion = $null
    if ($Version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z' -or
        -not [version]::TryParse($Version, [ref]$parsedVersion)) {
        throw 'Expected a canonical three-part LaunchFlow version.'
    }
    return Join-Path $Root "dist/LaunchFlow-Setup-v$Version-x64.exe"
}

function Assert-LaunchFlowDraftRelease {
    param([bool]$Requested, [string]$Version, [string]$Commit, [string]$Confirmation, [string]$WindowsEvidence)
    if (-not $Requested) { return }
    Get-LaunchFlowSetupPath -Root '.' -Version $Version | Out-Null
    if ($Commit -cnotmatch '\A[0-9a-f]{40}\z' -or $Confirmation -cne "DRAFT-v$Version@$Commit" -or
        [string]::IsNullOrWhiteSpace($WindowsEvidence)) {
        throw 'Draft release requires exact DRAFT-v<VERSION>@<COMMIT> confirmation and a Windows test evidence reference.'
    }
}

function Assert-LaunchFlowPayload {
    param([string]$PublishDirectory)
    $required = @(
        'LaunchFlow.exe', 'LaunchFlow.dll', 'LaunchFlow.deps.json', 'LaunchFlow.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'WindowsBase.dll', 'Microsoft.Playwright.dll',
        'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        '.playwright/node/win32_x64/node.exe', '.playwright/node/LICENSE',
        '.playwright/package/cli.js', '.playwright/package/package.json',
        '.playwright/package/browsers.json', '.playwright/package/lib/coreBundle.js',
        '.playwright/package/lib/utilsBundle.js', '.playwright/package/lib/serverRegistry.js',
        '.playwright/package/lib/bootstrap.js', '.playwright/package/lib/package.js',
        '.playwright/package/lib/utilsBundle.js.LICENSE', '.playwright/package/lib/serverRegistry.js.LICENSE',
        '.playwright/package/LICENSE', '.playwright/package/NOTICE'
    )
    foreach ($relative in $required) {
        $path = Join-Path $PublishDirectory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -Force).Length -eq 0) {
            throw "Publish payload is missing a required nonempty runtime file: $relative"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $PublishDirectory 'LaunchFlow.runtimeconfig.json') -Raw | ConvertFrom-Json
    $options = $runtime.runtimeOptions
    if ($options.PSObject.Properties.Name -contains 'framework' -or $options.PSObject.Properties.Name -contains 'frameworks') {
        throw 'Publish output still depends on a separately installed .NET runtime.'
    }
    if ($options.PSObject.Properties.Name -notcontains 'includedFrameworks') {
        throw 'Publish output does not declare its included .NET runtimes.'
    }
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $included = @($options.includedFrameworks | Where-Object { $_.name -ceq $framework })
        $frameworkVersion = $null
        if ($included.Count -ne 1 -or
            [string]$included[0].version -cnotmatch '\A8\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z' -or
            -not [version]::TryParse([string]$included[0].version, [ref]$frameworkVersion)) {
            throw "Publish output does not include the expected .NET 8 framework: $framework"
        }
    }
    $deps = Get-Content -LiteralPath (Join-Path $PublishDirectory 'LaunchFlow.deps.json') -Raw | ConvertFrom-Json
    if ($deps.runtimeTarget.name -cnotmatch '/win-x64\z') { throw 'Publish output is not Windows x64.' }
    $sqlite = @('e_sqlite3.dll', 'runtimes/win-x64/native/e_sqlite3.dll') |
        Where-Object { $p = Join-Path $PublishDirectory $_; (Test-Path -LiteralPath $p -PathType Leaf) -and (Get-Item -LiteralPath $p).Length -gt 0 }
    if (@($sqlite).Count -eq 0) { throw 'Publish output is missing the native SQLite runtime.' }
}
