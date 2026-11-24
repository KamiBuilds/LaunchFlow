$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
. (Join-Path $PSScriptRoot 'Ownership.ps1')
function Require([bool]$v){if(-not $v){throw 'OwnershipAssertionFailed'}}
function Reject([scriptblock]$a){$refused=$false;try{& $a}catch{$refused=$true};Require $refused}
$script:Exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;$script:Installer='synthetic-not-installed'
$start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$script:Exe;$start.UseShellExecute=$false;$start.Arguments='-NoProfile -Command "Start-Sleep -Seconds 60"'
$child=[Diagnostics.Process]::Start($start)
try {
    $identity=Get-Identity $child $script:Exe
    Require ((Get-Owned $identity).Id -eq $child.Id)
    $identity.startTicks='111111111111111111';Require ($null -eq (Get-Owned $identity))
    $identity=Get-Identity $child $script:Exe;$identity.path='unowned';Reject {Get-Owned $identity}
    $identity=Get-Identity $child $script:Exe;$identity.pid=$true;Reject {Get-Owned $identity}
    $identity=Get-Identity $child $script:Exe;$identity.session=-1;Reject {Get-Owned $identity}
    $identity=Get-Identity $child $script:Exe
    $child.Kill();Require ($child.WaitForExit(5000));Require ($null -eq (Get-Owned $identity))
    'PASS actual process PID/start/path/session identity controls and observed exit (portable, not Windows UI)'
}finally{if(-not $child.HasExited){$child.Kill();$null=$child.WaitForExit(5000)};$child.Dispose()}
