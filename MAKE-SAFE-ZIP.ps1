# Use this script instead of compressing a developer's entire working folder.
# Source ZIP: Git-tracked files only (no user vault or build output).
# Portable ZIP: only the validated self-contained publish payload.
# Both formats intentionally contain ZERO user master passwords/vaults.
[CmdletBinding()]
param(
    [ValidateSet('Source', 'Portable')]
    [string]$Kind = 'Source',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
. (Join-Path $root 'BUILD-PACKAGING-HELPERS.ps1')
$staging = Join-Path ([IO.Path]::GetTempPath()) ('launchflow-safe-zip-' + [guid]::NewGuid().ToString('N'))
if (-not $OutputPath) {
    $OutputPath = Join-Path $root ("dist/LaunchFlow-v0.8.2-$($Kind.ToLowerInvariant())-clean.zip")
}
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output.StartsWith([IO.Path]::GetFullPath($staging), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output ZIP cannot be inside the staging directory.'
}
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    if ($Kind -eq 'Source') {
        $git = Get-Command git -ErrorAction Stop
        $files = @(& $git.Source -C $root ls-files --cached --full-name)
        if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) {
            throw 'Cannot enumerate Git-tracked source files. Refusing to ZIP the working directory.'
        }
        foreach ($item in $files) {
            if ([string]::IsNullOrWhiteSpace($item)) { continue }
            $relative = $item.Replace('\', '/')
            if ([IO.Path]::IsPathRooted($relative) -or
                $relative -match '(^|/)\.\.(/|$)' -or
                $relative -match '(^|/)\.git(/|$)') {
                throw "Invalid Git source path: $relative"
            }
            $source = Join-Path $root $relative
            $target = Join-Path $staging $relative
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Missing tracked source file: $relative"
            }
            $targetDir = Split-Path -Parent $target
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    }
    else {
        $publish = Join-Path $root 'publish'
        Assert-LaunchFlowPayload -PublishDirectory $publish
        Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $publish
        # Recurse including hidden Playwright .playwright/ files.
        Copy-Item -LiteralPath (Join-Path $publish '*') -Destination $staging -Recurse -Force
    }

    Assert-LaunchFlowNoUserData -PublishDirectory $staging
    [IO.Compression.ZipFile] | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $parent = Split-Path -Parent $output
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temp = "$output.pending-$([guid]::NewGuid().ToString('N'))"
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $staging, $temp, [IO.Compression.CompressionLevel]::Optimal, $false)
        # Inspect the *actual ZIP*, not only the staged directory.
        $zip = [IO.Compression.ZipFile]::OpenRead($temp)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName -match '(?i)(^|/)(profiles-v2|profiles-sealed|recovery)(/|$)' -or
                    $entry.Name -match '(?i)\.(db|sqlite|sqlite3)(-wal|-shm)?$' -or
                    $entry.Name -match '(?i)\.(launchflow|kdbx|profile\.aes|dpapi)$' -or
                    $entry.Name -match '(?i)^(hello-unlock\.bin|\.env(?:\..+)?)$') {
                    throw 'ZIP unexpectedly contains encrypted user data. No distributable created.'
                }
            }
        }
        finally { $zip.Dispose() }
        Move-Item -LiteralPath $temp -Destination $output -Force
    }
    finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
    Write-Host "Clean $Kind ZIP created: $output"
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
