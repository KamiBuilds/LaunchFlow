function Import-BuildOwnership {
    if($null -eq ('NativeUiDriver.OwnedBuild' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'OwnedBuild.cs') | Out-Null}
}
function Invoke-OwnedBuild([string]$Executable,[string]$Arguments,[string]$Directory,[string]$Log,[string]$Status,[int]$Timeout=2400000) {
    Import-BuildOwnership
    # A stopped/failed owner must not leave a stale successful residual claim.
    Write-Json $Status ([ordered]@{schema=1;category='Harness';reason='OperationFailed';remaining=-1;exitCode=-1})
    $outcome=[NativeUiDriver.OwnedBuild]::Run($Executable,$Arguments,$Directory,$Log,$Timeout)
    if($outcome.Category -cnotin @('None','Harness','Timeout') -or $outcome.Reason -cnotin @('None','OperationFailed','Deadline') -or $outcome.Remaining -lt -1){throw 'ManifestRejected'}
    Write-Json $Status ([ordered]@{schema=1;category=[string]$outcome.Category;reason=[string]$outcome.Reason;remaining=[int]$outcome.Remaining;exitCode=[int]$outcome.ExitCode})
    if($outcome.Remaining -ne 0){throw 'CleanupIncomplete'}
    if($outcome.Category -cne 'None' -or $outcome.ExitCode -ne 0){throw $outcome.Reason}
    return $outcome.ExitCode
}
