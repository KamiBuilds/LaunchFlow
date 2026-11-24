#requires -Version 5.1
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess){throw 'HostRejected'}
. (Join-Path $PSScriptRoot 'Contracts.ps1')
. (Join-Path $PSScriptRoot 'Ownership.ps1')
. (Join-Path $PSScriptRoot 'BuildOwnership.ps1')
Import-BuildOwnership
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
$root=Join-Path ([IO.Path]::GetTempPath()) ('native-build-job-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
# The readiness signal and duplicated-handle receipt are private to this fixture owner.
$acl=[Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true,$false)
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
[IO.Directory]::SetAccessControl($root,$acl)
$fixture=Join-Path $root 'BuildTreeFixture.exe'
$source=@'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
public static class BuildTreeFixture {
 public static int Main(string[] args) {
  string role=args[0], root=args[1], mode=args[2];
  Process current=Process.GetCurrentProcess();
  File.WriteAllLines(Path.Combine(root,role+".identity"),new string[]{current.Id.ToString(),current.StartTime.ToUniversalTime().Ticks.ToString()});
  if(role=="sentinel"){Thread.Sleep(180000);return 0;}
  if(Environment.GetEnvironmentVariable("GIT_NATIVE_OWNED_PROBE")!=null)return 9;
  if(role!="grandchild"){
   ProcessStartInfo start=new ProcessStartInfo(current.MainModule.FileName);
   start.UseShellExecute=false;
   start.Arguments=(role=="root"?"child":"grandchild")+" \""+root+"\" "+mode;
   Process child=Process.Start(start);
   if(role=="root"&&mode=="rootexit")return 0;
   child.WaitForExit();return child.ExitCode;
  }
  Thread.Sleep(mode=="healthy"?200:60000);return 0;
 }
}
'@
Add-Type -TypeDefinition $source -OutputAssembly $fixture -OutputType ConsoleApplication | Out-Null
$creationProbeSource=@'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class BuildCreationFixture {
 public static int Observer;
 public static string Ready;
 public static void Hold(IntPtr child, uint pid) {
  long created=0, exited, kernel, user;
  Check(GetProcessId(child)==pid && GetProcessTimes(child,out created,out exited,out kernel,out user));
  IntPtr observer=OpenProcess(0x40,false,Observer), copy=IntPtr.Zero;
  Check(observer!=IntPtr.Zero);
  try { Check(DuplicateHandle(GetCurrentProcess(),child,observer,out copy,0x101001,false,0)); }
  finally { CloseHandle(observer); }
  // Publish only after the observer owns an exact non-inheritable child handle, never a job handle.
  File.WriteAllLines(Ready+".pending",new string[]{copy.ToInt64().ToString(),pid.ToString(),created.ToString()});
  File.Move(Ready+".pending",Ready);
  Thread.Sleep(60000);
  throw new InvalidOperationException("CreationProbeNotInterrupted");
 }
 public static void Validate(IntPtr child, uint pid, long created, string fixture) {
  long actual, exited, kernel, user; uint flags, session;
  Check(GetProcessId(child)==pid && GetProcessTimes(child,out actual,out exited,out kernel,out user) && actual==created);
  StringBuilder path=new StringBuilder(32768); uint length=(uint)path.Capacity;
  Check(QueryFullProcessImageName(child,0,path,ref length) && String.Equals(path.ToString(),fixture,StringComparison.OrdinalIgnoreCase));
  Check(ProcessIdToSessionId(pid,out session) && session==(uint)Process.GetCurrentProcess().SessionId);
  Check(GetHandleInformation(child,out flags) && (flags&1)==0);
 }
 public static bool Gone(IntPtr child, uint timeout) { uint status=WaitForSingleObject(child,timeout); Check(status==0 || status==258); return status==0; }
 public static void Cleanup(IntPtr child) { if(!Gone(child,0)){Check(TerminateProcess(child,1));Check(Gone(child,5000));} }
 public static void Close(IntPtr child) { Check(CloseHandle(child)); }
 private static void Check(bool value) { if(!value)throw new InvalidOperationException("CreationFixtureRejected"); }
 [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
 [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern uint GetProcessId(IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ProcessIdToSessionId(uint pid,out uint session);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetHandleInformation(IntPtr handle,out uint flags);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateProcess(IntPtr process,uint exit);
 [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
'@
Add-Type -TypeDefinition $creationProbeSource | Out-Null
$creationSource=Join-Path $root 'BuildCreationFixture.cs'
[IO.File]::WriteAllText($creationSource,$creationProbeSource)
function Assert-TreeGone([string]$Directory) {
    foreach($role in @('root','child','grandchild')){
        $path=Join-Path $Directory ($role+'.identity')
        Require ([IO.File]::Exists($path)) ('NativeFixtureDidNotReach-'+$role)
        $parts=[IO.File]::ReadAllLines($path);Require ($parts.Length -eq 2) 'FixtureIdentityInvalid'
        $p=Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
        if($null -ne $p){$null=$p.Handle;Require ($p.HasExited -or $p.StartTime.ToUniversalTime().Ticks.ToString() -cne $parts[1]) 'OwnedBuildDescendantSurvived'}
    }
}
$sentinel=$null;$owner=$null;$creationOwner=$null;$creationHandle=[IntPtr]::Zero;$creationValidated=$false
$oldProbe=[Environment]::GetEnvironmentVariable('GIT_NATIVE_OWNED_PROBE','Process')
try {
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$fixture;$start.UseShellExecute=$false;$start.Arguments='sentinel "'+$root+'" healthy'
    $sentinel=[Diagnostics.Process]::Start($start)
    [Environment]::SetEnvironmentVariable('GIT_NATIVE_OWNED_PROBE','synthetic','Process')
    foreach($mode in @('healthy','timeout','exception','rootexit')){
        $directory=Join-Path $root $mode;[void][IO.Directory]::CreateDirectory($directory)
        $arguments='root "'+$directory+'" '+$mode
        $timer=[Diagnostics.Stopwatch]::StartNew()
        if($mode -ceq 'exception'){$outcome=[NativeUiDriver.OwnedBuild]::RunFaultProbe($fixture,$arguments,$directory,(Join-Path $directory 'build.log'),10000,6000)}
        elseif($mode -ceq 'timeout'){
            $status=Join-Path $directory 'result.json';$refused=$false
            try{$null=Invoke-OwnedBuild $fixture $arguments $directory (Join-Path $directory 'build.log') $status 6000}catch{if($_.Exception.Message -cne 'Deadline'){throw};$refused=$true}
            Require $refused 'BuildWrapperAcceptedTimeout';$outcome=Read-BoundedJson $status
            Assert-Keys $outcome @('schema','category','reason','remaining','exitCode')
        }else{$outcome=[NativeUiDriver.OwnedBuild]::Run($fixture,$arguments,$directory,(Join-Path $directory 'build.log'),6000)}
        Require ($timer.Elapsed.TotalSeconds -lt 22) 'NativeTreeCleanupUnbounded'
        Require ($outcome.Remaining -eq 0) 'NativeBuildResidualUnverified'
        if($mode -ceq 'healthy'){Require ($outcome.Category -ceq 'None' -and $outcome.ExitCode -eq 0) 'HealthyJobFailed'}
        elseif($mode -ceq 'exception'){Require ($outcome.Category -ceq 'Harness' -and $outcome.Reason -ceq 'OperationFailed') 'ExceptionNotClassified'}
        else{Require ($outcome.Category -ceq 'Timeout' -and $outcome.Reason -ceq 'Deadline') 'TimeoutNotClassified'}
        Assert-TreeGone $directory
        Require (-not $sentinel.HasExited) 'UnrelatedSyntheticSentinelKilled'
        'PASS actual Windows suspended-launch Job Object: '+$mode
    }
    $directory=Join-Path $root 'suspended-creation-fault';[void][IO.Directory]::CreateDirectory($directory)
    $outcome=[NativeUiDriver.OwnedBuild]::RunFaultProbe($fixture,('root "'+$directory+'" timeout'),$directory,(Join-Path $directory 'build.log'),6000,-2)
    Require ($outcome.Category -ceq 'Harness' -and $outcome.Remaining -eq 0) 'SuspendedCreatedChildNotCleaned'
    Require (-not [IO.File]::Exists((Join-Path $directory 'root.identity'))) 'SuspendedChildRanBeforeResume'
    Require (-not $sentinel.HasExited) 'UnrelatedSyntheticSentinelKilled'
    'PASS actual Windows creation-time assigned suspended child exception cleanup'
    # Kill the owner while the exact newly created child is suspended, before membership verification/resume.
    $directory=Join-Path $root 'creation-owner-exit';[void][IO.Directory]::CreateDirectory($directory)
    $creationReady=Join-Path $directory 'child.ready'
    $creationScript=Join-Path $root 'creation-owner.ps1'
    $scriptLines=@(
        '$ErrorActionPreference="Stop"',
        ('Add-Type -Path '+(Quote-Argument (Join-Path $PSScriptRoot 'OwnedBuild.cs'))),
        ('Add-Type -Path '+(Quote-Argument $creationSource)),
        ('[BuildCreationFixture]::Observer='+$PID),
        ('[BuildCreationFixture]::Ready='+(Quote-Argument $creationReady)),
        '$callback=[Action[IntPtr,uint32]]{param($child,$childId);[BuildCreationFixture]::Hold($child,$childId)}',
        ('$null=[NativeUiDriver.OwnedBuild]::RunCreationProbe('+(Quote-Argument $fixture)+",'root "+(Quote-Argument $directory)+" timeout',"+(Quote-Argument $directory)+','+(Quote-Argument (Join-Path $directory 'build.log'))+',60000,$callback)'),
        'throw "CreationProbeReturned"'
    )
    [IO.File]::WriteAllLines($creationScript,$scriptLines)
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;$start.UseShellExecute=$false
    $start.Arguments='-NoLogo -NoProfile -File '+(Quote-Argument $creationScript)
    $creationOwner=[Diagnostics.Process]::Start($start)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists($creationReady) -and -not $creationOwner.HasExited -and $timer.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 25}
    Require ([IO.File]::Exists($creationReady) -and -not $creationOwner.HasExited) 'SuspendedCreationFixtureNotReady'
    $receipt=[IO.File]::ReadAllLines($creationReady);Require ($receipt.Length -eq 3) 'CreationFixtureReceiptRejected'
    $creationHandle=[IntPtr]::new([long]$receipt[0])
    [BuildCreationFixture]::Validate($creationHandle,[uint32]$receipt[1],[long]$receipt[2],$fixture)
    $creationValidated=$true
    Require (-not [BuildCreationFixture]::Gone($creationHandle,0)) 'SuspendedCreationChildAlreadyExited'
    Require (-not [IO.File]::Exists((Join-Path $directory 'root.identity'))) 'CreationChildRanBeforeOwnerDeath'
    $creationOwner.Kill();Require ($creationOwner.WaitForExit(5000)) 'CreationOwnerDidNotExit'
    Require ([BuildCreationFixture]::Gone($creationHandle,10000)) 'CreationTimeKillOnCloseChildSurvived'
    Require (-not [IO.File]::Exists((Join-Path $directory 'root.identity'))) 'CreationChildResumedBeforeOwnerDeath'
    Require (-not $sentinel.HasExited) 'UnrelatedSyntheticSentinelKilled'
    'PASS actual Windows abrupt owner death immediately after suspended creation, before resume'
    # Abrupt owner exit tests KILL_ON_JOB_CLOSE independently of managed finally cleanup.
    $directory=Join-Path $root 'owner-exit';[void][IO.Directory]::CreateDirectory($directory)
    $ownerScript=Join-Path $root 'owner.ps1'
    [IO.File]::WriteAllText($ownerScript,('Add-Type -Path '+(Quote-Argument (Join-Path $PSScriptRoot 'OwnedBuild.cs'))+"`n"+'$null=[NativeUiDriver.OwnedBuild]::Run('+(Quote-Argument $fixture)+",'root "+(Quote-Argument $directory)+" timeout',"+(Quote-Argument $directory)+','+(Quote-Argument (Join-Path $directory 'build.log'))+',60000)'))
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;$start.UseShellExecute=$false
    $start.Arguments='-NoLogo -NoProfile -File '+(Quote-Argument $ownerScript)
    $owner=[Diagnostics.Process]::Start($start)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists((Join-Path $directory 'grandchild.identity')) -and $timer.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 50}
    Require ([IO.File]::Exists((Join-Path $directory 'grandchild.identity'))) 'OwnerExitFixtureNotReady'
    $owner.Kill();Require ($owner.WaitForExit(5000)) 'SyntheticOwnerDidNotExit'
    # Job termination is asynchronous; allow only the fixed cleanup observation budget.
    $timer.Restart();$gone=$false
    do{try{Assert-TreeGone $directory;$gone=$true}catch{Start-Sleep -Milliseconds 50}}while(-not $gone -and $timer.Elapsed.TotalSeconds -lt 10)
    Require $gone 'KillOnCloseDescendantSurvived';Require (-not $sentinel.HasExited) 'UnrelatedSyntheticSentinelKilled'
    'PASS actual Windows Job Object kill-on-close after abrupt synthetic owner exit'
}finally{
    [Environment]::SetEnvironmentVariable('GIT_NATIVE_OWNED_PROBE',$oldProbe,'Process')
    $fixtureClean=$true
    foreach($p in @($creationOwner,$owner,$sentinel)){
        if($null -ne $p){try{if(-not $p.HasExited){$p.Kill();if(-not $p.WaitForExit(5000)){throw 'CleanupIncomplete'}}}catch{$fixtureClean=$false}finally{$p.Dispose()}}
    }
    if($creationHandle -ne [IntPtr]::Zero){
        try{if(-not $creationValidated){throw 'FixtureIdentityInvalid'};[BuildCreationFixture]::Cleanup($creationHandle)}catch{$fixtureClean=$false}finally{[BuildCreationFixture]::Close($creationHandle)}
    }
    # Defensive fixture-only cleanup after a failed mechanism assertion. Full identity
    # and the unique compiled fixture path are required; never target installed apps.
    foreach($identityPath in [IO.Directory]::GetFiles($root,'*.identity',[IO.SearchOption]::AllDirectories)){
        try{
            $parts=[IO.File]::ReadAllLines($identityPath);if($parts.Length -ne 2){throw 'FixtureIdentityInvalid'}
            $p=Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
            if($null -ne $p){
                $null=$p.Handle
                if($p.StartTime.ToUniversalTime().Ticks.ToString() -ceq $parts[1]){
                    if($p.Path -ine $fixture -or $p.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){throw 'FixtureIdentityInvalid'}
                    if(-not $p.HasExited){$p.Kill();if(-not $p.WaitForExit(5000)){throw 'CleanupIncomplete'}}
                }
                $p.Dispose()
            }
        }catch{$fixtureClean=$false}
    }
    if($fixtureClean){[IO.Directory]::Delete($root,$true)}else{throw 'CleanupIncomplete'}
}
