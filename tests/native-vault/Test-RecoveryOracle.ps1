param([string]$SourceRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'Contracts.ps1')
. (Join-Path $SourceRoot 'Ownership.ps1')
. (Join-Path $SourceRoot 'Scenarios.ps1')
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
# Reviewer scenario boundary model; the real Invoke-Scenario and filesystem oracle run unchanged.
Add-Type -TypeDefinition 'namespace NativeUiDriver { public static class Native { public static int InputSize {get{return 40;}} public static bool InputDesktopAvailable(){return true;} } }'
function Assert-WindowsHost {}
function Import-Native {}
function Assert-FreshUser {}
function Assert-Files {}
function Start-Owned([string]$Path,[string]$Arguments='') {
    if($Arguments -ceq '--exit-for-install'){
        Require ($script:Report.phase -ceq 'RecoveryPendingExit') 'RecoveryPendingExitPhaseMissing'
        switch($script:Mutation){
            'pending-wal' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-wal'),'')}
            'pending-shm' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-shm'),'')}
            'pending-main' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'changed')}
        }
    }
    $p=[pscustomobject]@{Id=1;ExitCode=0;Waits=0}
    $p|Add-Member ScriptMethod WaitForExit {param($Timeout) $this.Waits++;return $true}
    $script:Processes+=@($p);return $p
}
function Check-App {}
function Count-Owned {return 0}
function Wait-OwnedExit {}
function Read-Package {return [pscustomobject]@{binding=$script:Binding;runtime=@()}}
function Wait-Window {return [pscustomobject]@{Handle=1}}
function Click-Button($Window,[string]$Name) {
    Require ($Name -ceq ('Cancel '+[char]0x2014+' exit without moving files')) 'WrongActualCancelLabel'
    switch($script:Mutation){
        'extra-file' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'unexpected-recovery-state'),'synthetic-owned-fixture')}
        'quarantine-dir' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'quarantine'))}
        'staging-file' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'recovery-stage'));[IO.File]::WriteAllText((Join-Path $script:DataRoot 'recovery-stage/copy'),'synthetic')}
        'main-hash' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'synthetic')}
        'wal' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-wal'),'')}
        'diagnostics' {foreach($n in @('startup-diagnostics.log','startup-diagnostics.log.previous')){[IO.File]::WriteAllText((Join-Path $script:DataRoot $n),'synthetic fixed diagnostic')}}
        'diagnostics-lookalike' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'startup-diagnostics.log.recovery'),'synthetic')}
        'diagnostics-directory' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'startup-diagnostics.log'))}
        'diagnostics-oversized' {[IO.File]::WriteAllBytes((Join-Path $script:DataRoot 'startup-diagnostics.log'),[byte[]]::new(262145))}
        'nested-diagnostics' {[void][IO.Directory]::CreateDirectory((Join-Path $script:DataRoot 'stage'));[IO.File]::WriteAllText((Join-Path $script:DataRoot 'stage/startup-diagnostics.log'),'synthetic')}
    }
}
$script:Binding=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
$root=Join-Path ([IO.Path]::GetTempPath()) ('native-recovery-'+[Guid]::NewGuid().ToString('N'))
try {
    foreach($script:Mutation in @('healthy','extra-file','diagnostics','quarantine-dir','staging-file','main-hash','wal','diagnostics-lookalike','diagnostics-directory','diagnostics-oversized','nested-diagnostics','pending-wal','pending-shm','pending-main')) {
        $Case='recovery_cancel';$PackageRoot='synthetic-unused';$SourceSha=('a'*40)
        $ResultRoot=Join-Path $root $script:Mutation;[void][IO.Directory]::CreateDirectory((Join-Path $ResultRoot 'private'))
        $script:DataRoot=Join-Path $ResultRoot 'data';$script:Exe='synthetic-unused';$script:Installer='synthetic-unused';$script:Report=New-Report $Case
        $refused=$false;$script:Processes=@()
        try{Invoke-Scenario}catch{if($_.Exception.Message -cne 'RecoveryChanged'){throw};$refused=$true}
        $shouldPass=$script:Mutation -cin @('healthy','diagnostics')
        if(-not $refused){$script:Report.phase='Complete';$script:Report.completed=$true;$script:Report.category='None';$script:Report.reason='None';$script:Report.remaining=0}
        $projected=Project-Report $script:Report
        Require ($projected.accepted -eq $shouldPass) ('RecoveryOracleIncorrect-'+$script:Mutation)
        if($script:Mutation -like 'pending-*'){
            Require ($script:Report.milestones.RecoveryPreserved -and -not $script:Report.milestones.RecoveryPendingPreserved) ('PendingPreservationMilestoneIncorrect-'+$script:Mutation)
        }else{
            Require ($script:Report.milestones.RecoveryPreserved -eq $shouldPass) ('PreservationMilestoneIncorrect-'+$script:Mutation)
        }
        if($shouldPass){
            Require ($script:Processes.Count -eq 4 -and $script:Processes[2].Waits -eq 1 -and $script:Processes[3].Waits -eq 1) 'RecoveryPrimaryOrHelperNotWaited'
            foreach($m in @('RecoveryPendingExit','RecoveryPendingPreserved')){
                Require ($script:Report.milestones.Contains($m) -and $script:Report.milestones[$m]) ('MissingRecoveryStartupMilestone-'+$m)
                $script:Report.milestones[$m]=$false;Require (-not (Project-Report $script:Report).accepted) ('OptionalRecoveryStartupMilestone-'+$m);$script:Report.milestones[$m]=$true
            }
        }
        'PASS actual scenario modeled-boundary recovery oracle: '+$script:Mutation
    }
}finally{if([IO.Directory]::Exists($root)){[IO.Directory]::Delete($root,$true)}}
