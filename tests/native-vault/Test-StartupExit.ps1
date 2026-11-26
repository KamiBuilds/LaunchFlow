param([string]$SourceRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'Contracts.ps1')
. (Join-Path $SourceRoot 'Ownership.ps1')
. (Join-Path $SourceRoot 'Scenarios.ps1')
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
# Execute actual scenario, report, full-exit and whole-root oracles against owned boundary adapters.
Add-Type -TypeDefinition 'namespace NativeUiDriver { public static class Native { public static int InputSize {get{return 40;}} public static bool InputDesktopAvailable(){return true;} } }'
function Assert-WindowsHost {}
function Import-Native {}
function Assert-FreshUser {}
function Assert-Files { $script:PayloadChecks++ }
function Read-Package {return [pscustomobject]@{binding=$script:Binding;runtime=@()}}
function Check-App {}
function Count-Owned {if($script:Mutation -ceq 'residual' -and $script:Report.phase -ceq 'UnlockPendingExit'){return 1};return 0}
function New-Process([bool]$Helper,[bool]$Installer) {
    $p=[pscustomobject]@{Helper=$Helper;Installer=$Installer;ExitCode=0;Waits=0}
    $p|Add-Member ScriptMethod WaitForExit {param($Timeout)
        $this.Waits++
        if($this.Helper -and $script:Mutation -ceq 'helper-timeout'){return $false}
        if(-not $this.Helper -and -not $this.Installer -and $script:Mutation -ceq 'primary-timeout'){return $false}
        return $true
    }
    if($Helper -and $script:Mutation -ceq 'helper-exit'){$p.ExitCode=7}
    if(-not $Helper -and -not $Installer -and $script:Mutation -ceq 'primary-exit'){$p.ExitCode=8}
    $script:Processes+=@($p);return $p
}
function Start-Owned([string]$Path,[string]$Arguments='') {
    if($Arguments -ceq '--exit-for-install' -and $script:Processes.Count -eq 1){
        Require (-not [IO.Directory]::Exists($script:DataRoot)) 'NoInstanceRootWasPrecreated'
    }
    if($Path -ceq $script:Exe){
        [void][IO.Directory]::CreateDirectory($script:DataRoot)
        if($Arguments -ceq '--exit-for-install' -and $script:Processes.Count -eq 1){[IO.File]::WriteAllText((Join-Path $script:DataRoot 'startup-diagnostics.log'),'fixed synthetic diagnostic')}
    }
    $helper=$Arguments -ceq '--exit-for-install'
    if($helper -and $script:Report.phase -ceq 'UnlockPendingExit'){
        switch($script:Mutation){
            'main-changed' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'changed')}
            'wal-added' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-wal'),'')}
            'shm-added' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db-shm'),'')}
            'unknown-added' {[IO.File]::WriteAllText((Join-Path $script:DataRoot 'unclassified'),'fixture')}
        }
    }
    if($helper -and $script:Mutation -ceq 'noinstance-created' -and $script:Processes.Count -eq 1){[IO.File]::WriteAllText((Join-Path $script:DataRoot 'launchflow.db'),'unexpected')}
    if($helper){
        $expected=@{3='SetupPendingExit';7='UnlockPendingExit'}
        if($expected.ContainsKey($script:Processes.Count)){Require ($script:Report.phase -ceq $expected[$script:Processes.Count]) 'PendingExitPhaseMissing'}
    }
    return (New-Process $helper ($Path -ceq $script:Installer))
}
function Wait-OwnedExit($Process,[int]$Seconds=240) {
    Require ($Process.WaitForExit($Seconds*1000)) 'ExitFailed'
    Require ($Process.ExitCode -eq 0) 'ExitFailed'
}
function Wait-Window([string]$Kind){return [pscustomobject]@{Handle=1;Kind=$Kind}}
function Fill-Field {}
function Dismiss-Dialog {}
function Click-Button($Window,[string]$Name) {
    if($Name -ceq 'Create vault & continue'){[IO.File]::WriteAllBytes((Join-Path $script:DataRoot 'launchflow.db'),[byte[]](1,2,3,4))}
}
function Wait-Main([string]$Milestone,[string]$Previous){Mark $Milestone}
$script:Binding=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
$root=Join-Path ([IO.Path]::GetTempPath()) ('native-startup-exit-'+[Guid]::NewGuid().ToString('N'))
try {
    foreach($script:Mutation in @('healthy','main-changed','wal-added','shm-added','unknown-added','helper-exit','primary-exit','helper-timeout','primary-timeout','residual','noinstance-created')) {
        $Case='owned_wrong';$PackageRoot='synthetic-unused';$SourceSha=('a'*40)
        $ResultRoot=Join-Path $root $script:Mutation;[void][IO.Directory]::CreateDirectory((Join-Path $ResultRoot 'private'))
        $script:DataRoot=Join-Path $ResultRoot 'data';$script:Exe='synthetic-app';$script:Installer='synthetic-installer';$script:Report=New-Report $Case
        $script:Processes=@();$script:PayloadChecks=0;$refused=$false
        try{Invoke-Scenario}catch{if($_.Exception.Message -cnotin @('ExitFailed','Deadline','RecoveryChanged')){throw};$refused=$true}
        $healthy=$script:Mutation -ceq 'healthy'
        Require ($refused -ne $healthy) ('StartupControlSensitivity-'+$script:Mutation)
        if($healthy){
            foreach($m in @('NoInstanceExit','SetupPendingExit','SetupPendingPreserved','UnlockPendingExit','UnlockPendingPreserved')){Require ($script:Report.milestones.Contains($m) -and $script:Report.milestones[$m]) ('MissingStartupMilestone-'+$m)}
            Require ($script:Processes.Count -eq 10 -and @($script:Processes|Where-Object {$_.Waits -ne 1}).Count -eq 0) 'NotEveryPrimaryAndHelperWaited'
            Require ($script:PayloadChecks -eq 2) 'FullPayloadGuardOmitted'
            $script:Report.phase='Complete';$script:Report.completed=$true;$script:Report.category='None';$script:Report.reason='None';$script:Report.remaining=0
            Require (Project-Report $script:Report).accepted 'HealthyStartupScenarioNotAccepted'
            foreach($m in @('NoInstanceExit','SetupPendingExit','SetupPendingPreserved','UnlockPendingExit','UnlockPendingPreserved')){
                $script:Report.milestones.$m=$false;Require (-not (Project-Report $script:Report).accepted) ('OptionalStartupMilestone-'+$m);$script:Report.milestones.$m=$true
            }
        }
        if($script:Mutation -ceq 'primary-timeout'){
            Require ($script:Processes[-1].Helper -and $script:Processes[-1].Waits -eq 1) 'PrimaryTimeoutSkippedHelperWait'
        }
        'PASS actual scenario modeled-boundary startup exit: '+$script:Mutation
    }
}finally{if([IO.Directory]::Exists($root)){[IO.Directory]::Delete($root,$true)}}
# Keep model types and function overrides out of the production native host.
$hostPath=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
foreach($gate in @('Supervisor','Root','Worker')){
    $arguments=@('-NoLogo','-NoProfile','-NonInteractive')
    if($env:OS -ceq 'Windows_NT'){$arguments+=@('-Mta')}
    $arguments+=@('-File',(Join-Path $PSScriptRoot 'Test-StartupContracts.ps1'),'-SourceRoot',$SourceRoot,'-Gate',$gate)
    & $hostPath @arguments
    if($LASTEXITCODE -ne 0){throw 'StartupContractGateFailed'}
}
