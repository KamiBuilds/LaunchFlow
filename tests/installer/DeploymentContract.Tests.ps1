[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$iss = Get-Content -LiteralPath (Join-Path $root 'installer/LaunchFlow.iss') -Raw

function Require-Contract([bool]$ok, [string]$description) {
    if (-not $ok) { throw "Deployment source contract failed: $description" }
    Write-Host "PASS source contract: $description"
}

$setupStart = $iss.IndexOf('function InitializeSetup(): Boolean;')
$setupEnd = $iss.IndexOf('procedure InitializeWizard;')
$reportStart = $iss.IndexOf('procedure ReportSetupFailure(')
Require-Contract ($setupStart -gt 0 -and $setupEnd -gt $setupStart -and $reportStart -gt 0 -and $reportStart -lt $setupStart) 'setup failure handler and installer entry point are present'
$setupBody = $iss.Substring($setupStart, $setupEnd - $setupStart)
$report = $iss.Substring($reportStart, $setupStart - $reportStart)
Require-Contract ([regex]::IsMatch($report, 'if\s+not\s+WizardSilent\s+then\s*MsgBox\s*\(')) 'setup error dialog runs only interactively'
Require-Contract (-not [regex]::IsMatch($setupBody, '\bMsgBox\s*\(')) 'installer abort paths contain no unconditional custom dialogs'
Require-Contract (([regex]::Matches($setupBody, '\bReportSetupFailure\s*\(')).Count -eq 3) 'all three installer shutdown failures use the silent-safe handler'
Require-Contract ($setupBody.Contains('--exit-for-install') -and $setupBody.Contains("CheckForMutexes('LaunchFlow.Windows.SingleInstance')") -and $setupBody.Contains('Result := False;')) 'unsafe running-app replacement remains blocked'

$uninstallStart = $iss.IndexOf('function InitializeUninstall(): Boolean;')
$uninstallEnd = $iss.IndexOf('procedure CurUninstallStepChanged(')
Require-Contract ($uninstallStart -gt $setupEnd -and $uninstallEnd -gt $uninstallStart) 'uninstall code is present'
$uninstall = $iss.Substring($uninstallStart, $uninstallEnd - $uninstallStart)
$mutex = $uninstall.IndexOf("CheckForMutexes('LaunchFlow.Windows.SingleInstance')")
$silent = $uninstall.IndexOf('if UninstallSilent then')
$prompt = $uninstall.IndexOf('Answer := MsgBox(')
Require-Contract ($mutex -ge 0 -and $silent -gt $mutex -and $prompt -gt $silent) 'live-app refusal and silent-preserve path precede destructive prompt'
Require-Contract ([regex]::IsMatch($uninstall, 'WipeUserData\s*:=\s*False;') -and [regex]::IsMatch($uninstall, 'if\s+UninstallSilent\s+then\s*begin[\s\S]*?Exit;\s*end;')) 'unattended uninstall exits before user-data deletion consent'
Require-Contract ([regex]::IsMatch($uninstall, 'if\s+not\s+UninstallSilent\s+then\s*MsgBox\s*\(')) 'running-app uninstall error is not displayed unattended'
Require-Contract ($uninstall.Contains('WipeUserData := (Answer = IDYES);') -and $iss.Contains('(CurUninstallStep = usPostUninstall) and WipeUserData')) 'interactive data purge still requires affirmative user choice'
Require-Contract ($iss.Contains('PrivilegesRequired=lowest') -and $iss.Contains('DefaultDirName={localappdata}\Programs\LaunchFlow') -and -not $iss.Contains('PrivilegesRequiredOverridesAllowed=')) 'current-user install scope remains unchanged'
Write-Host 'Source-level checks only: native silent install/uninstall, cross-session and policy behavior remain unverified.'
