param([string]$SourceRoot=$PSScriptRoot,[ValidateSet('Supervisor','Root','Worker')][string]$Gate='Supervisor')
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'Contracts.ps1')
. (Join-Path $SourceRoot 'Ownership.ps1')
. (Join-Path $SourceRoot 'Scenarios.ps1')
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
function Source-Ast([string]$Name){
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot $Name),[ref]$tokens,[ref]$errors)
    Require ($errors.Count -eq 0) 'SourceParseFailed'
    return $ast
}
$hostAst=Source-Ast 'Test-NativeVault.ps1'
if($Gate -ceq 'Supervisor'){
    # Only the clock boundary is adapted; execute the complete actual supervisor.
    Add-Type -TypeDefinition @'
using System;
namespace StartupExitModel {
    public sealed class Clock {
        public static int Seconds;
        int started;
        public static Clock StartNew(){return new Clock {started=Seconds};}
        public TimeSpan Elapsed {get{return TimeSpan.FromSeconds(Seconds-started);}}
        public void Restart(){started=Seconds;}
    }
}
'@
    $definition=$hostAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-Supervisor'},$true)
    $adapted=$definition.Extent.Text.Replace('[Diagnostics.Stopwatch]::StartNew()','[StartupExitModel.Clock]::StartNew()')
    Invoke-Expression $adapted
    $budget=$hostAst.Find({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$budget'},$true)
    $budgetMatches=$true
    foreach($phaseName in @('SetupPendingExit','UnlockPendingExit','RecoveryPendingExit','FirstExit','FinalExit')){
        $r=[pscustomobject]@{phase=$phaseName}
        $budgetMatches=$budgetMatches -and ((Invoke-Expression $budget.Right.Extent.Text) -eq 270)
    }
    function Start-Host {return $script:Child}
    function Read-State {return $script:Report}
    function Save-State {}
    function Check-App {}
    function Count-Owned {return 0}
    function Mark([string]$Name){$script:Report.milestones.$Name=$true}
    function Start-Owned {return $script:Helper}
    $script:Exe='synthetic-app'
    function Stop-Host($Child){if(-not $Child.HasExited){$script:Stops++;$Child.HasExited=$true}}
    function Start-Sleep {
        [StartupExitModel.Clock]::Seconds+=5
        if($script:Mode -cne 'stuck' -and [StartupExitModel.Clock]::Seconds -ge 240 -and -not $script:Child.HasExited){
            try{
                Full-Exit 'FirstExit'
                $script:Report.phase='Complete';$script:Report.category='None';$script:Report.reason='None';$script:Report.completed=$true;$script:Report.remaining=0
            }catch{
                Require ($_.Exception.Message -ceq 'Deadline') 'UnexpectedExitFailure'
                $script:Report.category='Timeout';$script:Report.reason='Deadline'
            }
            $script:Child.HasExited=$true
        }
    }
    foreach($phaseName in @('SetupPendingExit','UnlockPendingExit','RecoveryPendingExit')){
        foreach($script:Mode in @('healthy','primary-timeout','helper-timeout','stuck')){
            [StartupExitModel.Clock]::Seconds=0;$script:Stops=0
            $Case='owned_wrong';$script:Report=New-Report $Case;$script:Report.phase=$phaseName
            $script:Child=[pscustomobject]@{HasExited=$false;ExitCode=0}
            $script:App=[pscustomobject]@{Waits=0;ExitCode=0}
            $script:Helper=[pscustomobject]@{Waits=0;ExitCode=0}
            $script:App|Add-Member ScriptMethod WaitForExit {param($Timeout)
                Require ($Timeout -eq 240000) 'PrimaryWaitWeakened';$this.Waits++
                return $script:Mode -cne 'primary-timeout'
            }
            $script:Helper|Add-Member ScriptMethod WaitForExit {param($Timeout)
                Require ($Timeout -eq 15000) 'HelperWaitWeakened';$this.Waits++
                [StartupExitModel.Clock]::Seconds+=15
                return $script:Mode -cne 'helper-timeout'
            }
            Invoke-Supervisor
            if($script:Mode -ceq 'stuck'){
                Require ($script:Stops -eq 1 -and $script:Report.category -ceq 'Timeout' -and $script:Report.reason -ceq 'Deadline') 'SupervisorDidNotBoundStuckWorker'
                Require ($script:App.Waits -eq 0 -and $script:Helper.Waits -eq 0) 'StuckWorkerUnexpectedWait'
            }else{
                Require ($script:Stops -eq 0 -and $script:App.Waits -eq 1 -and $script:Helper.Waits -eq 1) 'SupervisorInterruptedExitWaits'
                Require ($script:Report.completed -eq ($script:Mode -ceq 'healthy')) 'SupervisorCompletionIncorrect'
                if($script:Mode -cne 'healthy'){Require ($script:Report.category -ceq 'Timeout' -and $script:Report.reason -ceq 'Deadline') 'TimeoutCategoryIncorrect'}
            }
            'PASS startup-supervisor '+$phaseName+' '+$script:Mode
        }
    }
    Require $budgetMatches 'PendingExitBudgetIncorrect'
}
if($Gate -ceq 'Root'){
    $script:Exe='synthetic-app'
    function Count-Owned {return 0}
    function Save-State {}
    function Wait-OwnedExit($Process,[int]$Seconds){Require ($Process.ExitCode -eq 0 -and $Process.WaitForExit($Seconds*1000)) 'ExitFailed'}
    function Start-Owned {
        $script:RootExistedAtEntry=[IO.Directory]::Exists($script:DataRoot)
        if($script:Mode -cnotin @('absent','empty','deleted-root')){[void][IO.Directory]::CreateDirectory($script:DataRoot)}
        switch($script:Mode){
            'diagnostics' {foreach($name in @('startup-diagnostics.log','startup-diagnostics.log.previous')){[IO.File]::WriteAllText((Join-Path $script:DataRoot $name),'fixed synthetic diagnostic')}}
            'diagnostic-limit' {[IO.File]::WriteAllBytes((Join-Path $script:DataRoot 'startup-diagnostics.log'),[byte[]]::new(262144))}
            'diagnostic-oversized' {[IO.File]::WriteAllBytes((Join-Path $script:DataRoot 'startup-diagnostics.log'),[byte[]]::new(262145))}
            'db' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'fixture')}
            'wal' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-wal'),'')}
            'shm' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-shm'),'')}
            'unknown-file' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'unclassified'),'fixture')}
            'unknown-directory' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'unclassified'))}
            'root-file' {[IO.Directory]::Delete($script:DataRoot);[IO.File]::WriteAllText($script:DataRoot,'fixture')}
            'root-reparse' {
                [IO.Directory]::Delete($script:DataRoot)
                $kind=if($env:OS -ceq 'Windows_NT'){'Junction'}else{'SymbolicLink'}
                $null=New-Item -ItemType $kind -Path $script:DataRoot -Target $script:Outside -ErrorAction Stop
            }
            'diagnostic-directory' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'startup-diagnostics.log'))}
            'diagnostic-lookalike' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'startup-diagnostics.log.extra'),'fixture')}
            'nested-diagnostic' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'stage'));[IO.File]::WriteAllText((Join-Path $script:DataRoot 'stage/startup-diagnostics.log'),'fixture')}
            'reparse' {
                $kind=if($env:OS -ceq 'Windows_NT'){'Junction'}else{'SymbolicLink'}
                $null=New-Item -ItemType $kind -Path (Join-Path $script:DataRoot 'linked') -Target $script:Outside -ErrorAction Stop
            }
            'deleted-root' {[IO.Directory]::Delete($script:DataRoot)}
        }
        $process=[pscustomobject]@{ExitCode=0}
        $process|Add-Member ScriptMethod WaitForExit {param($Timeout) return $true}
        return $process
    }
    $scratch=Join-Path ([IO.Path]::GetTempPath()) ('startup-root-'+[Guid]::NewGuid().ToString('N'))
    try{
        $script:Outside=Join-Path $scratch 'outside';[void][IO.Directory]::CreateDirectory($script:Outside)
        foreach($script:Mode in @('absent','empty','diagnostics','diagnostic-limit','empty-created','diagnostic-oversized','db','wal','shm','unknown-file','unknown-directory','root-file','root-reparse','diagnostic-directory','diagnostic-lookalike','nested-diagnostic','reparse','deleted-root')){
            $script:DataRoot=Join-Path $scratch $script:Mode
            $initiallyExists=$script:Mode -cin @('empty','deleted-root')
            if($initiallyExists){[void][IO.Directory]::CreateDirectory($script:DataRoot)}
            $before=Snapshot-RecoveryRoot $script:DataRoot
            Require ($before.exists -is [bool] -and $before.exists -eq $initiallyExists -and $before.entries.Count -eq 0) 'RootExistenceNotRepresented'
            $script:Report=New-Report 'owned_wrong';$refused=$false;$script:RootExistedAtEntry=$null
            try{No-InstanceExit}catch{
                Require ((Safe-Reason $_) -cin @('RecoveryChanged','PathRejected')) 'UnexpectedRootFailure'
                $refused=$true
            }
            $shouldPass=$script:Mode -cin @('absent','empty','diagnostics','diagnostic-limit')
            Require ($script:RootExistedAtEntry -eq $initiallyExists) 'RootCreatedBeforeHelperEntry'
            Require ($refused -ne $shouldPass -and $script:Report.milestones.NoInstanceExit -eq $shouldPass) 'RootPreservationControlIncorrect'
            'PASS startup-root '+$script:Mode
        }
    }finally{if([IO.Directory]::Exists($scratch)){[IO.Directory]::Delete($scratch,$true)}}
}
if($Gate -ceq 'Worker'){
    # Import only the maintained process/input boundary adapters, never its test loop.
    $modelAst=Source-Ast 'Test-StartupExit.ps1'
    foreach($definition in $modelAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){
        Invoke-Expression $definition.Extent.Text
    }
    Add-Type -TypeDefinition 'namespace NativeUiDriver { public static class Native { public static int InputSize {get{return 40;}} public static bool InputDesktopAvailable(){return true;} public static void ClearClipboardFinally(){} } }'
    $worker=$hostAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-Worker'},$true)
    Invoke-Expression $worker.Extent.Text
    $script:Binding=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
    $script:ModelStart=${function:Start-Owned};$script:ActualDigest=${function:Get-FileDigest}
    function Start-Owned([string]$Path,[string]$Arguments=''){
        $p=& $script:ModelStart $Path $Arguments
        if($Arguments -ceq '--exit-for-install'){
            if($script:WorkerMode -ceq 'noinstance-mutation' -and $script:Processes.Count -eq 2){[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'fixture')}
            if($script:Processes.Count -eq 4){
                if($script:WorkerMode -ceq 'setup-mutation'){[IO.File]::WriteAllText((Join-Path $script:DataRoot 'unclassified'),'fixture')}
                if($script:WorkerMode -ceq 'io-failure'){$script:IoFault=$true}
                if($script:WorkerMode -ceq 'source-failure'){throw 'BindingMismatch'}
            }
        }
        return $p
    }
    function Check-App {if($script:WorkerMode -ceq 'ownership-failure' -and $script:Report.phase -ceq 'SetupPendingExit'){throw 'WindowIdentityLost'}}
    function Get-FileDigest([string]$Path){if($script:IoFault){throw [IO.IOException]::new('FixedSyntheticIoFailure')};return (& $script:ActualDigest $Path)}
    $scratch=Join-Path ([IO.Path]::GetTempPath()) ('startup-worker-'+[Guid]::NewGuid().ToString('N'))
    try{
        foreach($script:WorkerMode in @('healthy','noinstance-mutation','setup-mutation','unlock-mutation','io-failure','ownership-failure','source-failure')){
            $Case='owned_wrong';$PackageRoot='synthetic-unused';$SourceSha=('a'*40)
            $script:Mutation=if($script:WorkerMode -ceq 'unlock-mutation'){'main-changed'}else{'healthy'}
            $ResultRoot=Join-Path $scratch $script:WorkerMode;[void][IO.Directory]::CreateDirectory((Join-Path $ResultRoot 'private'))
            $script:DataRoot=Join-Path $ResultRoot 'data';$script:Exe='synthetic-app';$script:Installer='synthetic-installer';$script:Report=New-Report $Case
            $script:Processes=@();$script:PayloadChecks=0;$script:IoFault=$false
            Invoke-Worker
            $safe=Project-Report $script:Report
            if($script:WorkerMode -ceq 'healthy'){Require $safe.accepted 'HealthyWorkerNotAccepted'}else{
                Require (-not $safe.accepted) 'FailedWorkerAccepted'
                if($script:WorkerMode -like '*mutation'){
                    Require ($safe.category -ceq 'Application' -and $safe.reason -ceq 'RecoveryChanged') 'PreservationCategoryIncorrect'
                }else{
                    $reason=@{'io-failure'='OperationFailed';'ownership-failure'='WindowIdentityLost';'source-failure'='BindingMismatch'}[$script:WorkerMode]
                    Require ($safe.category -ceq 'Harness' -and $safe.reason -ceq $reason) 'HarnessFailureMisclassified'
                }
            }
            'PASS startup-worker '+$script:WorkerMode
        }
    }finally{if([IO.Directory]::Exists($scratch)){[IO.Directory]::Delete($scratch,$true)}}
}
