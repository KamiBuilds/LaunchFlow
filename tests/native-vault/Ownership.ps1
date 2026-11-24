# Shared by the owned worker and its watchdog. Private ledger is never an artifact.
function Assert-WindowsHost {
    if($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or $PSVersionTable.PSEdition -cne 'Desktop' -or -not [Environment]::Is64BitProcess -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'HostRejected'}
    foreach($name in [Environment]::GetEnvironmentVariables('Process').Keys){if([string]$name -like 'LAUNCHFLOW_TEST_*'){throw 'HostRejected'}}
}
function Quote-Argument([string]$Value) {
    if($Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n") -or $Value.EndsWith('\')){throw 'PathRejected'}
    return '"'+$Value+'"'
}
function Get-Identity([Diagnostics.Process]$Process,[string]$Path) {
    $null=$Process.Handle
    return [ordered]@{pid=$Process.Id;startTicks=$Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture);session=$Process.SessionId;path=$Path}
}
function Get-Owned($Identity) {
    Assert-Keys $Identity @('pid','startTicks','session','path')
    if(-not (Test-Integer $Identity.pid) -or $Identity.pid -lt 1 -or -not (Test-Integer $Identity.session) -or $Identity.startTicks -isnot [string] -or $Identity.startTicks -cnotmatch '^[0-9]{15,20}$' -or $Identity.path -isnot [string]){throw 'ManifestRejected'}
    if($Identity.session -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or $Identity.path -cnotin @($script:Exe,$script:Installer)){throw 'PathRejected'}
    $found=Get-Process -Id $Identity.pid -ErrorAction SilentlyContinue
    if($null -eq $found){return $null}
    $null=$found.Handle
    if(-not $found.Path){throw 'WindowIdentityLost'}
    # A live reused PID is not ours. A failed inspection is an error, not proof of absence.
    if($found.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $Identity.startTicks -or $found.SessionId -ne $Identity.session -or -not [string]::Equals($found.Path,$Identity.path,[StringComparison]::OrdinalIgnoreCase)){return $null}
    return $found
}
function Start-Owned([string]$Path,[string]$Arguments='') {
    if($Path -cnotin @($script:Exe,$script:Installer)){throw 'PathRejected'}
    Assert-NoReparse $Path
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$Path;$start.WorkingDirectory=[IO.Path]::GetDirectoryName($Path)
    $start.UseShellExecute=$false;$start.Arguments=$Arguments
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $intent=$script:LedgerPath+'.spawn-intent'
    if(Test-PendingSpawn){throw 'CleanupIncomplete'}
    # Durable conservative ownership precedes launch, including atomic-write interruption.
    Write-Json $intent ([ordered]@{schema=1;state='PendingSpawn'})
    $process=$null
    try {
        $process=[Diagnostics.Process]::Start($start)
        # Retain the direct handle before any stream/identity/publication operation.
        if($null -eq (Get-Variable StartedHandles -Scope Script -ErrorAction SilentlyContinue)){$script:StartedHandles=@()}
        $script:StartedHandles+=@($process)
        $null=$process.Handle
        # Snapshot the exact identity in a PSCustomObject; WinPS 5.1 can
        # otherwise enumerate an OrderedDictionary during positional binding,
        # leaving a malformed one-entry ledger that fails Assert-Keys.
        $identity=[pscustomobject](Get-Identity $process $Path)
        if($null -eq (Get-Owned $identity)){throw 'WindowIdentityLost'}
        $script:Ledger+=@($identity)
        Write-Json $script:LedgerPath ([object[]]$script:Ledger)
        [IO.File]::Delete($intent)
        $null=$process.StandardOutput.ReadToEndAsync();$null=$process.StandardError.ReadToEndAsync()
        return $process
    } catch {
        # Only graceful cleanup via the directly owned handle. Never force-kill product.
        if($null -ne $process){try{if(-not $process.HasExited){$null=$process.CloseMainWindow();$null=$process.WaitForExit(1000)}}catch{}}
        # A pre-publication failure deliberately retains intent; a watchdog reports unknown.
        throw
    }
}
function Read-Ledger {
    if(-not [IO.File]::Exists($script:LedgerPath)){return @()}
    # The ledger must be a top-level JSON array, never an object or a
    # polymorphic wrapper. WinPS 5.1 ConvertFrom-Json can emit the array as a
    # *single* Object[] pipeline value; @(<command>) wraps it one level too deep.
    $raw=[IO.File]::ReadAllText($script:LedgerPath)
    if(-not $raw.TrimStart().StartsWith('[',[StringComparison]::Ordinal)){throw 'ManifestRejected'}
    $decoded=Read-BoundedJson $script:LedgerPath
    $entries=if($null -eq $decoded){[object[]]@()}else{[object[]]$decoded}
    if($entries.Count -gt 16){throw 'ManifestRejected'}
    foreach($e in $entries){$null=Get-Owned $e}
    return $entries
}
function Test-PendingSpawn {
    # Do not deserialize or trust absence of the ledger while registration may be interrupted.
    return (Test-Path -LiteralPath ($script:LedgerPath+'.spawn-intent')) -or (Test-Path -LiteralPath ($script:LedgerPath+'.spawn-intent.pending'))
}
function Count-Owned {
    if(Test-PendingSpawn){return -1}
    $count=0;foreach($e in (Read-Ledger)){if($null -ne (Get-Owned $e)){$count++}};return $count
}
function Wait-OwnedExit([Diagnostics.Process]$Process,[int]$Seconds=240) {
    if(-not $Process.WaitForExit($Seconds*1000)){throw 'ExitFailed'}
    if($Process.ExitCode -ne 0){throw 'ExitFailed'}
}
function Cleanup-Owned {
    $script:Ledger=@(Read-Ledger)
    $live=@(foreach($e in $script:Ledger){$p=Get-Owned $e;if($null -ne $p){$p}})
    if(Test-PendingSpawn){
        # An interrupted spawn cannot authorize another spawn. Known identities can
        # still receive a graceful close, but the unknown transition remains unknown.
        foreach($p in $live){try{$null=$p.CloseMainWindow();$null=$p.WaitForExit(1000)}catch{}}
        return -1
    }
    if($live.Count -gt 0 -and @($live|Where-Object {$_.Path -ieq $script:Exe}).Count -gt 0) {
        $helper=Start-Owned $script:Exe '--exit-for-install'
        foreach($p in $live){$null=$p.WaitForExit(15000)};$null=$helper.WaitForExit(15000)
    }
    return (Count-Owned)
}
function Safe-Reason($ErrorRecord) {
    $e=$ErrorRecord.Exception
    for($i=0;$i -lt 12 -and $null -ne $e;$i++) {if($e.Message -cin $Reasons){return [string]$e.Message};$e=$e.InnerException}
    return 'OperationFailed'
}
function Assert-FreshUser {
    $actual=[Environment]::GetFolderPath('LocalApplicationData')
    if(-not $actual -or -not [string]::Equals([IO.Path]::GetFullPath($actual),[IO.Path]::GetFullPath($env:LOCALAPPDATA),[StringComparison]::OrdinalIgnoreCase)){throw 'PathRejected'}
    Assert-NoReparse $script:DataRoot
    if((Test-Path -LiteralPath $script:DataRoot) -and (-not [IO.Directory]::Exists($script:DataRoot) -or @(Get-ChildItem -LiteralPath $script:DataRoot -Force).Count -ne 0)){throw 'ExistingUserData'}
    if(@(Get-Process -Name LaunchFlow -ErrorAction SilentlyContinue).Count -ne 0 -or (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($script:Exe)))){throw 'ExistingUserData'}
}
