#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('preflight','valid_keyboard','valid_unicode','short_correction','owned_wrong','recovery_cancel','valid_clipboard')][string]$Case,
    [Parameter(Mandatory=$true)][string]$ResultRoot,
    [string]$PackageRoot='', [string]$SourceSha='',
    [ValidateSet('Parent','Supervisor','Worker','Cleanup')][string]$Role='Parent'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
. (Join-Path $PSScriptRoot 'Ownership.ps1')
. (Join-Path $PSScriptRoot 'Scenarios.ps1')
$script:Report=New-Report $Case
$script:Ledger=@();$script:Owner=$null;$script:App=$null;$script:OwnResult=$false
$script:Exe='';$script:Installer='';$script:DataRoot=''
function Start-Host([string]$ChildRole) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $args='-NoLogo -NoProfile -NonInteractive -Mta -ExecutionPolicy Bypass -File '+(Quote-Argument $PSCommandPath)+' -Case '+$Case+' -ResultRoot '+(Quote-Argument $ResultRoot)+' -Role '+$ChildRole
    if($PackageRoot){$args+=' -PackageRoot '+(Quote-Argument $PackageRoot)+' -SourceSha '+$SourceSha}
    $start.Arguments=$args
    $child=[Diagnostics.Process]::Start($start)
    $null=$child.StandardOutput.ReadToEndAsync();$null=$child.StandardError.ReadToEndAsync()
    return $child
}
function Read-State {
    $r=Read-BoundedJson (Join-Path $ResultRoot 'private/record.json')
    $null=Project-Report $r
    if($r.case -cne $Case){throw 'ManifestRejected'}
    return $r
}
function Stop-Host([Diagnostics.Process]$Child) {
    # This handle came from Process.Start; no process-name/global kill.
    if(-not $Child.HasExited){$Child.Kill();if(-not $Child.WaitForExit(5000)){throw 'CleanupIncomplete'}}
}
function Stop-RegisteredWorker {
    $path=Join-Path $ResultRoot 'private/worker.json'
    if(-not [IO.File]::Exists($path)){return}
    $identity=Read-BoundedJson $path
    Assert-Keys $identity @('pid','startTicks','session','path')
    if(-not (Test-Integer $identity.pid) -or $identity.pid -lt 1 -or -not (Test-Integer $identity.session) -or $identity.startTicks -isnot [string] -or $identity.startTicks -cnotmatch '^[0-9]{15,20}$'){throw 'ManifestRejected'}
    $workerHost=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
    $expected=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    if($null -ne $workerHost){$null=$workerHost.Handle}
    if($null -ne $workerHost -and $workerHost.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) -ceq $identity.startTicks -and $workerHost.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and $identity.session -eq $workerHost.SessionId -and $identity.path -ieq $expected -and $workerHost.Path -ieq $expected){Stop-Host $workerHost}
}
function Invoke-Supervisor {
    $child=Start-Host 'Worker';$timer=[Diagnostics.Stopwatch]::StartNew();$phaseTimer=[Diagnostics.Stopwatch]::StartNew();$previous=''
    $timeout=$false
    try {
        while(-not $child.HasExited) {
            Start-Sleep -Milliseconds 200
            $r=Read-State
            if($r.phase -cne $previous){$previous=$r.phase;$phaseTimer.Restart()}
            $budget=if($r.phase -cin @('FirstExit','FinalExit','SetupPendingExit','UnlockPendingExit','RecoveryPendingExit')){270}elseif($r.phase -ceq 'InstallBinding' -or $r.phase -ceq 'FreshUser'){210}else{100}
            if($phaseTimer.Elapsed.TotalSeconds -gt $budget -or $timer.Elapsed.TotalSeconds -gt 900){$timeout=$true;Stop-Host $child;break}
        }
        $script:Report=Read-State
        if($timeout){$script:Report.category='Timeout';$script:Report.reason='Deadline';$script:Report.completed=$false}
        elseif($child.ExitCode -ne 0 -and $script:Report.completed){$script:Report.category='Harness';$script:Report.reason='WorkerFailed';$script:Report.completed=$false}
        elseif(-not $script:Report.completed -and $script:Report.reason -ceq 'None'){$script:Report.category='Harness';$script:Report.reason='WorkerFailed'}
        if($Case -ceq 'preflight' -and $child.HasExited -and $child.ExitCode -eq 0 -and $script:Report.completed){$script:Report.milestones.NestedHost=$true}
    } finally {Stop-Host $child}
    Save-State
}
function Invoke-Worker {
    try {
        Invoke-Scenario
        $script:Report.remaining=Count-Owned
        if($script:Report.remaining -ne 0){throw 'CleanupIncomplete'}
        $script:Report.phase='Complete';$script:Report.category='None';$script:Report.reason='None';$script:Report.completed=$true
    } catch {
        if($script:Report.category -cne 'Application') {
            $script:Report.reason=Safe-Reason $_
            $script:Report.category=if($script:Report.reason -ceq 'Deadline'){'Timeout'}else{'Harness'}
        }
        $script:Report.completed=$false
    } finally {
        $script:Owner=$null
        try {
            if($null -ne ('NativeUiDriver.Native' -as [type])){[NativeUiDriver.Native]::ClearClipboardFinally()}
        }catch{$script:Report.completed=$false;$script:Report.category='Harness';$script:Report.reason='ClipboardClearFailed'}
        try{$script:Report.remaining=Count-Owned}catch{$script:Report.remaining=-1;$script:Report.completed=$false}
        Save-State
        $Error.Clear()
    }
}
function Invoke-CleanupHost {
    try {$script:Report=Read-State} catch {$script:Report=New-Report $Case;$script:Report.reason='WorkerFailed'}
    try {
        $script:Report.remaining=Cleanup-Owned
        if($Case -cin @('preflight','valid_clipboard')){Import-Native;[NativeUiDriver.Native]::ClearClipboardAfterWatchdog()}
    }catch{$script:Report.remaining=-1}
    Save-State
}
$code=1
try {
    Assert-WindowsHost
    $ResultRoot=[IO.Path]::GetFullPath($ResultRoot);Assert-NoReparse $ResultRoot
    if($Case -cne 'preflight') {
        if($SourceSha -cnotmatch '^[0-9a-f]{40}$'){throw 'BindingMismatch'}
        $PackageRoot=[IO.Path]::GetFullPath($PackageRoot);Assert-NoReparse $PackageRoot
        $script:DataRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LaunchFlow'
        $script:Exe=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/LaunchFlow/LaunchFlow.exe'
        $script:Installer=Join-Path $PackageRoot 'Installer.exe'
        foreach($protected in @($PackageRoot,([IO.Path]::GetDirectoryName($script:Exe)),$script:DataRoot)){
            if(($ResultRoot+[IO.Path]::DirectorySeparatorChar).StartsWith(($protected.TrimEnd('\')+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)){throw 'PathRejected'}
        }
    }
    $script:LedgerPath=Join-Path $ResultRoot 'private/processes.json'
    if($Role -ceq 'Parent') {
        if(Test-Path -LiteralPath $ResultRoot){throw 'PathRejected'}
        [void][IO.Directory]::CreateDirectory((Join-Path $ResultRoot 'private'))
        [void][IO.Directory]::CreateDirectory((Join-Path $ResultRoot 'public'))
        $script:OwnResult=$true
        Save-State
        $supervisor=Start-Host 'Supervisor'
        try {
            if(-not $supervisor.WaitForExit(960000)) {
                Stop-Host $supervisor
                # Worker host is separately registered before UI work, and must also be stopped.
                Stop-RegisteredWorker
                $script:Report=Read-State;$script:Report.category='Timeout';$script:Report.reason='Deadline';$script:Report.completed=$false;Save-State
            }
        } finally {Stop-Host $supervisor;Stop-RegisteredWorker}
        $script:Report=Read-State
        if($supervisor.ExitCode -ne 0 -and $script:Report.completed){$script:Report.completed=$false;$script:Report.category='Harness';$script:Report.reason='WorkerFailed';Save-State}
        if(-not $script:Report.completed -or (Count-Owned) -ne 0) {
            $cleanup=Start-Host 'Cleanup'
            try {if(-not $cleanup.WaitForExit(90000)){Stop-Host $cleanup}}finally{Stop-Host $cleanup}
            $script:Report=Read-State
        }
        $script:Report.remaining=Count-Owned
        if($script:Report.remaining -ne 0){$script:Report.completed=$false}
        $safe=Project-Report $script:Report
        Write-Json (Join-Path $ResultRoot 'public/result.json') $safe
        $code=if($safe.accepted){0}else{1}
        Write-Host ('native-vault: '+$(if($safe.accepted){'PASS'}else{'FAIL'})+' '+$Case+' phase='+$safe.phase+' '+$safe.category+' '+$safe.reason)
    } elseif($Role -ceq 'Supervisor') {Invoke-Supervisor;$code=0}
    elseif($Role -ceq 'Cleanup'){Invoke-CleanupHost;$code=0}
    else {
        Write-Json (Join-Path $ResultRoot 'private/worker.json') (Get-Identity ([Diagnostics.Process]::GetCurrentProcess()) (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'))
        Invoke-Worker;$code=if($script:Report.completed){0}else{1}
    }
} catch {
    # Never print host error records, raw UI trees, clipboard data or source-bearing exceptions.
    try {
        $script:Report.completed=$false;$script:Report.category='Harness';$script:Report.reason=Safe-Reason $_;$script:Report.remaining=-1
        if($Role -ceq 'Parent' -and $script:OwnResult) {
            try {Stop-RegisteredWorker;$cleanup=Start-Host 'Cleanup';try{if(-not $cleanup.WaitForExit(90000)){Stop-Host $cleanup}}finally{Stop-Host $cleanup};$script:Report.remaining=Count-Owned} catch {$script:Report.remaining=-1}
        }
        if($Role -ceq 'Parent' -and $script:OwnResult){Write-Json (Join-Path $ResultRoot 'public/result.json') (Project-Report $script:Report)}
    }catch{}
    if($Role -ceq 'Parent'){Write-Host 'native-vault: FAIL harness boundary'}
    $code=1
}finally{$script:Owner=$null;$Error.Clear()}
exit $code
