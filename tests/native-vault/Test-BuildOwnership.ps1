param([string]$SourceRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
$builder=[IO.File]::ReadAllText((Join-Path $SourceRoot 'Build-Candidate.ps1'))
Require ($builder.Contains('Invoke-OwnedBuild') -and -not $builder.Contains('[Diagnostics.Process]::Start(')) 'OriginalBuilderHasNoTreeOwner'
Require ($builder.Contains('2400000')) 'OriginalFixedDeadlineChanged'
# Source-level regression: not a Windows orphan reproduction.
$native=[IO.File]::ReadAllText((Join-Path $SourceRoot 'OwnedBuild.cs'))
Require ($native.Contains('new IntPtr(0x2000D)') -and $native.Contains('Marshal.WriteIntPtr(jobs, job)')) 'CreationTimeJobListMissing'
Require ($native.Contains('InitializeProcThreadAttributeList(attributes, 2,') -and $native.Contains('new IntPtr(0x20002)')) 'CreationAttributesIncomplete'
$create=$native.IndexOf('Check(CreateProcess(')
$membership=$native.IndexOf('Check(IsProcessInJob(')
$resume=$native.IndexOf('Check(ResumeThread(')
Require ($native.IndexOf('Check(SetInformationJobObject(') -lt $create -and $native.IndexOf('new IntPtr(0x2000D)') -lt $create) 'JobNotConfiguredBeforeCreation'
Require ($create -ge 0 -and $membership -gt $create -and $resume -gt $membership -and $native.Contains('Check(inJob);')) 'ChildJobMembershipNotVerifiedBeforeResume'
Require (-not $native.Contains('AssignProcessToJobObject')) 'PostCreationAssignmentStillRequired'
Require ($native.IndexOf('DeleteProcThreadAttributeList(attributes)') -lt $native.IndexOf('Marshal.FreeHGlobal(jobs)')) 'JobAttributeLifetimeTooShort'
'PASS source-level creation-time job assignment contract (not native execution)'
# Execute the native preflight's real imports without invoking Windows APIs.
$parseTokens=$null;$parseErrors=$null
$preflightAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'Test-BuildJob.ps1'),[ref]$parseTokens,[ref]$parseErrors)
Require ($parseErrors.Count -eq 0) 'NativeBuildPreflightParseFailed'
$imports=@($preflightAst.EndBlock.Statements|Where-Object {$_.Extent.Text -cmatch '^\. \(Join-Path \$PSScriptRoot '})
Require ($imports.Count -ge 2) 'NativeBuildPreflightImportsMissing'
& {
    param($DependencyRoot,$Imports)
    foreach($statement in $Imports){. ([scriptblock]::Create($statement.Extent.Text.Replace('$PSScriptRoot','$DependencyRoot')))}
    Require ($null -ne (Get-Command Quote-Argument -ErrorAction SilentlyContinue)) 'NativeBuildPreflightQuoteHelperMissing'
    Require ((Quote-Argument 'synthetic path') -ceq '"synthetic path"') 'NativeBuildPreflightQuoteHelperInvalid'
} $SourceRoot $imports
'PASS native preflight actual dependency imports resolve its quoting helper'
. (Join-Path $SourceRoot 'Contracts.ps1')
. (Join-Path $SourceRoot 'BuildOwnership.ps1')
Import-BuildOwnership
Require ($null -ne ('NativeUiDriver.OwnedBuild' -as [type])) 'BuildOwnershipCompileFailed'
Require ([NativeUiDriver.OwnedBuild]::LayoutValid()) 'NativeBuildX64LayoutRejected'
'PASS original builder routes through owned tree with unchanged fixed deadline; build WinAPI helper compiled'
if($env:OS -ne 'Windows_NT'){'SKIP actual Windows suspended-launch Job Object child/grandchild preflight (not Windows)';return}
& (Join-Path $SourceRoot 'Test-BuildJob.ps1')
