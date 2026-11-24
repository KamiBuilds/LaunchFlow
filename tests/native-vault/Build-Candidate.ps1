#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceSha,[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
if($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5 -or $SourceSha -cnotmatch '^[0-9a-f]{40}$'){throw 'HostRejected'}
$head=(& git rev-parse HEAD).Trim();if($LASTEXITCODE -ne 0 -or $head -cne $SourceSha){throw 'BindingMismatch'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot);Assert-NoReparse $OutputRoot
if(Test-Path -LiteralPath $OutputRoot){throw 'PathRejected'}
$p=$OutputRoot
while($p){if(Test-Path -LiteralPath (Join-Path $p '.git')){throw 'PathRejected'};$p=[IO.Path]::GetDirectoryName($p)}
[void][IO.Directory]::CreateDirectory($OutputRoot)
$bundle=Join-Path $OutputRoot 'package';$source=Join-Path $OutputRoot 'source'
[void][IO.Directory]::CreateDirectory($bundle);[void][IO.Directory]::CreateDirectory($source)
$archive=Join-Path $bundle 'source.zip'
& git archive --format=zip "--output=$archive" $SourceSha
if($LASTEXITCODE -ne 0){throw 'BindingMismatch'}
# Validate every archive file name, duplicate, mode and tree member before extraction.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
    $members=@($zip.Entries|Where-Object {-not $_.FullName.EndsWith('/')}|ForEach-Object {
        [pscustomobject]@{path=$_.FullName;sha256=('0'*64);bytes=$_.Length}
    });Assert-Manifest $members
    foreach($e in $zip.Entries){if($e.FullName.EndsWith('/')){Assert-Manifest @([pscustomobject]@{path=$e.FullName.TrimEnd('/');sha256=('0'*64);bytes=0})}}
    $tree=@(& git -c core.quotepath=false ls-tree -r $SourceSha)
    if($LASTEXITCODE -ne 0){throw 'BindingMismatch'}
    $paths=@($tree|ForEach-Object {if($_ -cnotmatch '^100(644|755) blob [0-9a-f]{40}\t(.+)$'){throw 'ManifestRejected'};$Matches[2]})
    if(@(Compare-Object $paths @($members.path) -CaseSensitive).Count -ne 0){throw 'BindingMismatch'}
}finally{$zip.Dispose()}
[IO.Compression.ZipFile]::ExtractToDirectory($archive,$source)
$before=@(New-Manifest $source)
Write-Json (Join-Path $bundle 'source-before.json') $before
# Confirm the extracted original builder is the one selected by the source SHA.
Assert-Files $source $before -Exact
$originalCmd=Join-Path $source 'BUILD-INSTALLER.cmd'
if(-not [IO.File]::Exists($originalCmd)){throw 'BindingMismatch'}
. (Join-Path $PSScriptRoot 'BuildOwnership.ps1')
# Same original CMD and EOF input; only the narrowly owned launch/wait changes.
$arguments='/d /s /c ""'+$originalCmd+'" -NoAutoInstall <NUL"'
$code=Invoke-OwnedBuild $env:ComSpec $arguments $source (Join-Path $OutputRoot 'builder.log') (Join-Path $OutputRoot 'build-result.json') 2400000
Assert-Files $source $before
$after=@(foreach($entry in $before){$file=Join-Path $source $entry.path;[pscustomobject]@{path=$entry.path;sha256=(Get-FileDigest $file);bytes=([IO.FileInfo]::new($file)).Length}})
Write-Json (Join-Path $bundle 'source-after.json') $after
$publish=Join-Path $source 'publish'
. (Join-Path $source 'BUILD-PACKAGING-HELPERS.ps1')
Assert-LaunchFlowPayload -PublishDirectory $publish
Assert-LaunchFlowNoUserData -PublishDirectory $publish
Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $publish
$runtime=@(New-Manifest $publish)
Write-Json (Join-Path $bundle 'runtime.json') $runtime
[xml]$project=[IO.File]::ReadAllText((Join-Path $source 'LaunchFlow/LaunchFlow.csproj'))
$versions=@($project.Project.PropertyGroup.Version|Where-Object {$_})
if($versions.Count -ne 1 -or $versions[0] -cnotmatch '^\d+\.\d+\.\d+$'){throw 'ManifestRejected'}
$version=[string]$versions[0]
$installer=Join-Path $source ('dist/LaunchFlow-Setup-v'+$version+'-x64.exe')
if(-not [IO.File]::Exists($installer) -or ([IO.FileInfo]::new($installer)).Length -eq 0){throw 'BindingMismatch'}
[IO.File]::Copy($installer,(Join-Path $bundle 'Installer.exe'),$false)
$p=[ordered]@{schema=1;sourceSha=$SourceSha;archiveSha256=(Get-FileDigest $archive);sourceBeforeSha256=(Get-FileDigest (Join-Path $bundle 'source-before.json'));sourceAfterSha256=(Get-FileDigest (Join-Path $bundle 'source-after.json'));runtimeManifestSha256=(Get-FileDigest (Join-Path $bundle 'runtime.json'));installerSha256=(Get-FileDigest (Join-Path $bundle 'Installer.exe'));version=$version;sourceFiles=$before.Count;runtimeFiles=$runtime.Count;builderExit=$code}
Write-Json (Join-Path $bundle 'provenance.json') (Project-Provenance $p)
$null=Read-Package $bundle $SourceSha
Write-Host 'PASS exact Git archive, unchanged original source, original CMD builder, installer and whole-runtime provenance'
