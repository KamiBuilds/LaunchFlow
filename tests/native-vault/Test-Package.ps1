$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
function Require([bool]$v){if(-not $v){throw 'PackageAssertionFailed'}}
function Reject([scriptblock]$a){$refused=$false;try{$null=& $a}catch{$refused=$true};Require $refused}
$root=Join-Path ([IO.Path]::GetTempPath()) ('package-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    # Synthetic byte fixtures test integrity contracts, not PE/installer execution.
    $source=@([pscustomobject]@{path='source.cs';sha256=('a'*64);bytes=1})
    Write-Json (Join-Path $root 'source-before.json') $source;Write-Json (Join-Path $root 'source-after.json') $source
    [IO.File]::WriteAllText((Join-Path $root 'source.zip'),'synthetic-archive-contract-fixture')
    [IO.File]::WriteAllText((Join-Path $root 'Installer.exe'),'synthetic-installer-contract-fixture')
    $runtime=@('LaunchFlow.exe','LaunchFlow.dll','coreclr.dll','hostpolicy.dll','Microsoft.Playwright.dll','.playwright/package/cli.js'|ForEach-Object {[pscustomobject]@{path=$_;sha256=('a'*64);bytes=1}})
    Write-Json (Join-Path $root 'runtime.json') $runtime
    $p=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=(Get-FileDigest (Join-Path $root 'source.zip'));sourceBeforeSha256=(Get-FileDigest (Join-Path $root 'source-before.json'));sourceAfterSha256=(Get-FileDigest (Join-Path $root 'source-after.json'));runtimeManifestSha256=(Get-FileDigest (Join-Path $root 'runtime.json'));installerSha256=(Get-FileDigest (Join-Path $root 'Installer.exe'));version='1.2.3';sourceFiles=1;runtimeFiles=$runtime.Count;builderExit=0}
    Write-Json (Join-Path $root 'provenance.json') $p
    Require ((Read-Package $root ('a'*40)).runtime.Count -eq $runtime.Count)
    Reject {Read-Package $root ('b'*40)}
    [IO.File]::WriteAllText((Join-Path $root 'unexpected'),'synthetic');Reject {Read-Package $root ('a'*40)};[IO.File]::Delete((Join-Path $root 'unexpected'))
    $saved=$p.runtimeFiles;$p.runtimeFiles=$true;Reject {Project-Provenance $p};$p.runtimeFiles=$saved
    $p.extra='synthetic';Reject {Project-Provenance $p};$p.Remove('extra')
    [IO.File]::AppendAllText((Join-Path $root 'Installer.exe'),'changed');Reject {Read-Package $root ('a'*40)}
    $payload=Join-Path $root 'payload';[void][IO.Directory]::CreateDirectory($payload)
    [IO.File]::WriteAllText((Join-Path $payload 'x.dll'),'synthetic')
    $m=@(New-Manifest $payload);Assert-Files $payload $m -Installed
    [IO.File]::WriteAllText((Join-Path $payload 'unins000.dat'),'metadata');Assert-Files $payload $m -Installed
    [IO.File]::WriteAllText((Join-Path $payload 'unins000.dll'),'not metadata');Reject {Assert-Files $payload $m -Installed}
    [IO.File]::Delete((Join-Path $payload 'unins000.dll'));[IO.File]::AppendAllText((Join-Path $payload 'x.dll'),'changed');Reject {Assert-Files $payload $m -Installed}
    'PASS package hash binding, exact extras, integer schema, whole payload and narrowly scoped uninstaller exclusion (synthetic fixtures)'
}finally{[IO.Directory]::Delete($root,$true)}
