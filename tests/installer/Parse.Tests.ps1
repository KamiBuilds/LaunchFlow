[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$files = @('BUILD-INSTALLER.ps1', 'BUILD-UNIVERSAL-INSTALLER.ps1', 'BUILD-PACKAGING-HELPERS.ps1', 'tests/installer/Packaging.Tests.ps1', 'tests/installer/Builder.Tests.ps1', 'tests/installer/Workflow.Tests.ps1', 'tests/installer/Parse.Tests.ps1')
foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $file), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
    Write-Host "PASS parses $file"
}
. (Join-Path $PSScriptRoot 'Workflow.Tests.ps1')
$index = 0
foreach ($script in (Get-LaunchFlowWorkflowScripts)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
    $index++
    Write-Host "PASS parses Windows workflow script $index"
}
@{ files = $files.Count; workflow_scripts = $index; failed = 0 } | ConvertTo-Json -Compress
