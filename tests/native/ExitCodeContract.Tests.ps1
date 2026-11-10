$ErrorActionPreference = 'Stop'

$smokePath = Join-Path $PSScriptRoot 'InstallerSmoke.Tests.ps1'
$source = Get-Content -LiteralPath $smokePath -Raw
$final = ($source.TrimEnd() -split '\r?\n')[-1].Trim()
if ($final -cne 'exit 0') {
    throw 'Native installer smoke must explicitly exit 0 after its final assertion.'
}
if ($source.IndexOf("Write-Host 'PASS native Windows: silent per-user uninstall removes program and preserves synthetic user data.'") -lt 0) {
    throw 'Native installer smoke lost its data-preservation success assertion.'
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('launchflow-exit-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $fixture = Join-Path $root 'Fixture.ps1'
    $caller = Join-Path $root 'Caller.ps1'
    @'
$ErrorActionPreference = 'Stop'
if ($env:LF_SIMULATED_FAILURE -eq 'process') {
    $proc = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-Command', 'exit 7') -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw 'Owned process returned a nonzero exit code.' }
}
Write-Host 'PASS synthetic process check'
Write-Host 'PASS synthetic retention check'
if ($env:LF_SIMULATED_FAILURE -eq 'assertion') {
    throw 'A final synthetic retention assertion failed.'
}
'@ | Set-Content -LiteralPath $fixture
    Add-Content -LiteralPath $fixture -Value $final

    @'
$ErrorActionPreference = 'Stop'
if ($env:LF_CALLER_STATE -eq 'stale') {
    $global:LASTEXITCODE = 23
} else {
    Remove-Variable -Scope Global -Name LASTEXITCODE -ErrorAction SilentlyContinue
}
& $args[0]
if ($LASTEXITCODE -ne 0) { throw 'Native smoke wrapper rejected its child result.' }
Write-Host 'WRAPPER_ACCEPTED'
'@ | Set-Content -LiteralPath $caller

    $pwsh = (Get-Process -Id $PID).Path
    foreach ($case in @(
        @{ Name = 'unset exit status'; Initial = 'unset'; Failure = ''; Accept = $true },
        @{ Name = 'stale nonzero exit status'; Initial = 'stale'; Failure = ''; Accept = $true },
        @{ Name = 'owned process failure'; Initial = 'unset'; Failure = 'process'; Accept = $false },
        @{ Name = 'assertion failure after PASS messages'; Initial = 'stale'; Failure = 'assertion'; Accept = $false }
    )) {
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $pwsh
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-NonInteractive')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($caller)
        $start.ArgumentList.Add($fixture)
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.UseShellExecute = $false
        $start.Environment['LF_CALLER_STATE'] = $case.Initial
        $start.Environment['LF_SIMULATED_FAILURE'] = $case.Failure
        $proc = [System.Diagnostics.Process]::Start($start)
        if ($null -eq $proc) { throw 'Unable to start PowerShell control-flow fixture.' }
        $out = $proc.StandardOutput.ReadToEnd()
        $err = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $accepted = $proc.ExitCode -eq 0 -and $out.Contains('WRAPPER_ACCEPTED')
        if ($accepted -ne $case.Accept) {
            throw "Exit contract $($case.Name) produced exit $($proc.ExitCode), accepted=$accepted, output=$out, error=$err"
        }
        if (-not $case.Accept -and $proc.ExitCode -eq 0) {
            throw "Exit contract $($case.Name) concealed a genuine failure."
        }
        Write-Host "PASS native exit-code control: $($case.Name)"
    }
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
exit 0
