param([string]$SourceRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'Contracts.ps1')
. (Join-Path $SourceRoot 'Ownership.ps1')
function Require([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
$root=Join-Path ([IO.Path]::GetTempPath()) ('native-registration-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$script:Exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$script:Installer='synthetic-unused';$script:LedgerPath=Join-Path $root 'processes.json';$script:Ledger=@();$script:Launched=$null
$original=${function:Get-Identity}
function Get-Identity([Diagnostics.Process]$Process,[string]$Path){$script:Launched=$Process;throw 'InjectedIdentityInspectionFailure'}
try {
    $caught=$false
    try {$null=Start-Owned $script:Exe '-NoLogo -NoProfile -Command "Start-Sleep -Seconds 20"'}catch{$caught=$_.Exception.Message -ceq 'InjectedIdentityInspectionFailure'}
    Require $caught 'PostStartFaultNotReached'
    Require ($null -ne $script:Launched -and -not $script:Launched.HasExited) 'RealChildNotLive'
    Require ((Count-Owned) -eq -1) 'UnregisteredLiveChildReportedAsZero'
    Require ([IO.File]::Exists($script:LedgerPath+'.spawn-intent')) 'PreLaunchIntentMissing'
    Require (@($script:StartedHandles|Where-Object {$_.Id -eq $script:Launched.Id}).Count -eq 1) 'DirectHandleNotRetained'
    # A fresh watchdog process has no direct handle or in-memory ledger.
    $watch=Join-Path $root 'watch.ps1'
    [IO.File]::WriteAllText($watch,('. '+(Quote-Argument (Join-Path $SourceRoot 'Contracts.ps1'))+"`n. "+(Quote-Argument (Join-Path $SourceRoot 'Ownership.ps1'))+"`n"+'$script:LedgerPath='+(Quote-Argument $script:LedgerPath)+"`nif((Count-Owned) -ne -1){exit 7}"))
    & $script:Exe -NoLogo -NoProfile -File $watch
    Require ($LASTEXITCODE -eq 0) 'WatchdogLostPendingRegistration'
    Require ((Cleanup-Owned) -eq -1) 'CleanupInventedEmptyInventory'
    'PASS real short child post-start identity fault, unknown residual and independent watchdog awareness (portable only)'
}finally{
    # Only this synthetic fixture handle; never a product/process-name kill.
    if($null -ne $script:Launched){if(-not $script:Launched.HasExited){$script:Launched.Kill();Require ($script:Launched.WaitForExit(5000)) 'FixtureCleanupFailed'};$script:Launched.Dispose()}
    # WinPS 5.1 does not reliably restore this dot-sourced function through
    # Set-Item + a captured scriptblock: the next identity became Object[]
    # instead of the expected four-field record. Reload the original definitions.
    . (Join-Path $SourceRoot 'Ownership.ps1')
    [IO.Directory]::Delete($root,$true)
}
# Healthy registration and intent retirement must still produce a verified ledger.
[void][IO.Directory]::CreateDirectory($root);$script:Ledger=@()
try {
    $child=Start-Owned $script:Exe '-NoLogo -NoProfile -Command "Start-Sleep -Seconds 2"'
    # Assert the exact JSON schema through a new process and a live process;
    # no profile, password, command line or process data is sent to CI output.
    Require ((Count-Owned) -eq 1) 'HealthyRegistrationMissing'
    Require (@(Read-Ledger).Count -eq 1) 'HealthyLedgerMissing'
    Require ($child.WaitForExit(10000)) 'HealthyChildExitFailed'
    Require ((Count-Owned) -eq 0) 'HealthyExitNotObserved'
    'PASS healthy verified ledger publication and retired spawn intent'
}finally{if($null -ne $child){if(-not $child.HasExited){$child.Kill();$null=$child.WaitForExit(5000)};$child.Dispose()};[IO.Directory]::Delete($root,$true)}
# A ledger-publication failure cannot retire intent even after identity verification.
[void][IO.Directory]::CreateDirectory($root);$script:Ledger=@();$script:StartedHandles=@()
$originalWrite=${function:Write-Json}
function Write-Json([string]$Path,$Value){if($Path -ceq $script:LedgerPath){throw 'InjectedLedgerPublicationFailure'};& $originalWrite $Path $Value}
try {
    $caught=$false
    try{$null=Start-Owned $script:Exe '-NoLogo -NoProfile -Command "Start-Sleep -Seconds 20"'}catch{$caught=$_.Exception.Message -ceq 'InjectedLedgerPublicationFailure'}
    Require $caught 'LedgerFaultNotReached';Require ($script:StartedHandles.Count -eq 1) 'LedgerFaultHandleLost'
    Require (-not $script:StartedHandles[0].HasExited) 'LedgerFaultChildNotLive'
    Require ((Count-Owned) -eq -1) 'LedgerFaultReportedAsZero'
    Require ([IO.File]::Exists($script:LedgerPath+'.spawn-intent')) 'LedgerFaultClearedIntent'
    'PASS real short child ledger-publication fault retains handle and unknown residual'
}finally{
    foreach($p in $script:StartedHandles){if(-not $p.HasExited){$p.Kill();Require ($p.WaitForExit(5000)) 'FixtureCleanupFailed'};$p.Dispose()}
    Set-Item Function:Write-Json $originalWrite
    [IO.Directory]::Delete($root,$true)
}
# Interrupted atomic intent publication remains unknown even before a ledger exists.
[void][IO.Directory]::CreateDirectory($root)
try{[IO.File]::WriteAllText(($script:LedgerPath+'.spawn-intent.pending'),'synthetic interrupted intent');Require ((Count-Owned) -eq -1) 'InterruptedIntentReportedAsZero';'PASS interrupted atomic intent cannot become empty inventory'}finally{[IO.Directory]::Delete($root,$true)}
