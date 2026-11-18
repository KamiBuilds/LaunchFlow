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

# .NET Framework (Windows PowerShell 5.1, launched by BUILD-INSTALLER.cmd)
# lacks Path.GetRelativePath. Keep path containment and privacy checks available
# in both Windows PowerShell 5.1 and PowerShell 7+.
function Get-LaunchFlowRelativePath {
    param(
        [Parameter(Mandatory=$true)][string]$Root,
        [Parameter(Mandatory=$true)][string]$Path
    )
    $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $absolutePath = [IO.Path]::GetFullPath($Path)
    $prefix = $absoluteRoot + [IO.Path]::DirectorySeparatorChar
    $comparison = if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (-not $absolutePath.StartsWith($prefix, $comparison)) {
        throw "Path is outside the publish directory. Refusing unsafe packaging path: $Path"
    }
    return $absolutePath.Substring($prefix.Length).Replace('\', '/')
}

# Reject private user data in distributable payloads. Never delete it silently.
function Assert-LaunchFlowNoUserData {
    param([Parameter(Mandatory=$true)][string]$PublishDirectory)
    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
        throw 'Publish output directory is missing.'
    }
    foreach ($file in (Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Force)) {
        $relative = Get-LaunchFlowRelativePath -Root $PublishDirectory -Path $file.FullName
        $name = $file.Name
        if ($relative -match '(?i)(^|/)(profiles-v2|profiles-sealed|recovery)(/|$)' -or
            $name -match '(?i)^(launchflow|vault|credentials|passwords|secrets)\.(db|sqlite|sqlite3)(-wal|-shm)?$' -or
            $name -match '(?i)\.(launchflow|kdbx|profile\.aes|dpapi)$' -or
            $name -match '(?i)^(hello-unlock\.bin|startup-diagnostics\.log|crash\.log|credentials\.json|passwords\.json|secrets\.json|\.env(?:\..+)?)$') {
            throw "Publish payload contains private user data or a vault fixture ($relative). Refusing distribution."
        }
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
    Assert-LaunchFlowNoUserData -PublishDirectory $PublishDirectory
}

function Assert-LaunchFlowFirefoxBrowsers {
    param([string]$PublishDirectory)
    $root = Join-Path $PublishDirectory 'playwright-browsers'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw 'Publish output is missing playwright-browsers (Firefox automation payload).'
    }
    $firefox = @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'firefox.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 0 })
    if ($firefox.Count -eq 0) {
        throw 'Publish output playwright-browsers does not contain firefox.exe.'
    }
}

function Install-LaunchFlowFirefoxBrowsers {
    param([string]$PublishDirectory, [string]$DotNetPath)
    $browsers = Join-Path $PublishDirectory 'playwright-browsers'
    if (Test-Path -LiteralPath $browsers) {
        Remove-Item -LiteralPath $browsers -Recurse -Force
    }
    New-Item -ItemType Directory -Path $browsers | Out-Null
    $node = Join-Path $PublishDirectory '.playwright\node\win32_x64\node.exe'
    $cli = Join-Path $PublishDirectory '.playwright\package\cli.js'
    if (-not (Test-Path -LiteralPath $node -PathType Leaf) -or -not (Test-Path -LiteralPath $cli -PathType Leaf)) {
        throw 'Cannot install Firefox automation: Playwright node/cli is missing from publish output.'
    }
    Write-Host "Installing Playwright Firefox into $browsers ..."
    $previousPath = $env:PLAYWRIGHT_BROWSERS_PATH
    $previousSkip = $env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD
    try {
        $env:PLAYWRIGHT_BROWSERS_PATH = $browsers
        Remove-Item Env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD -ErrorAction SilentlyContinue
        & $node $cli install firefox
        if ($LASTEXITCODE -ne 0) { throw "Playwright Firefox install failed with exit code $LASTEXITCODE." }
    }
    finally {
        if ($null -eq $previousPath) { Remove-Item Env:PLAYWRIGHT_BROWSERS_PATH -ErrorAction SilentlyContinue }
        else { $env:PLAYWRIGHT_BROWSERS_PATH = $previousPath }
        if ($null -eq $previousSkip) { Remove-Item Env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD -ErrorAction SilentlyContinue }
        else { $env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD = $previousSkip }
    }
    Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $PublishDirectory
    Write-Host '[OK] Playwright Firefox packaged for Auto sign-in.' -ForegroundColor Green
}

# Inno Setup can hit MAX_PATH while scanning nested Playwright Firefox binaries.
# Stage a validated copy under a short temporary path; this is independent of
# any Firefox/Chrome/Edge installation on the build machine.
function New-LaunchFlowInnoStage {
    param([Parameter(Mandatory=$true)][string]$PublishDirectory)
    Assert-LaunchFlowPayload -PublishDirectory $PublishDirectory
    Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $PublishDirectory
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('lf-iscc-' + [guid]::NewGuid().ToString('N').Substring(0,12))
    if ($stage.Length -gt 140) {
        throw "Temporary directory is too long for Inno: $stage. Set TEMP/TMP to a short writable directory."
    }
    try {
        New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
        # Include dot-directories such as .playwright; never use a non-Force wildcard.
        foreach ($entry in (Get-ChildItem -LiteralPath $PublishDirectory -Force)) {
            Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse -Force -ErrorAction Stop
        }
        Assert-LaunchFlowPayload -PublishDirectory $stage
        Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $stage
        $longFile = Get-ChildItem -LiteralPath $stage -Recurse -File -Force |
            Where-Object { $_.FullName.Length -ge 240 } | Select-Object -First 1
        if ($longFile) {
            throw "Inno source path is still too long: $($longFile.FullName). Set TEMP/TMP to a shorter writable directory."
        }
        return $stage
    }
    catch {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        throw
    }
}
