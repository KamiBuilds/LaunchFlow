# Cross-platform tests of real report/manifest helpers; not native acceptance.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
function Require([bool]$Value) { if (-not $Value) { throw 'MechanismAssertionFailed' } }
function Reject([scriptblock]$Action) { $refused=$false; try { & $Action } catch { $refused=$true }; Require $refused }
$r=New-Report 'valid_keyboard'
$r.binding=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
$r.phase='Complete'; $r.category='None'; $r.reason='None'; $r.completed=$true
foreach($name in (Required-Milestones $r.case)) { $r.milestones[$name]=$true }
$r.remaining=0
$s=Project-Report $r
Require ($s.accepted -eq $true)
$r.milestones.ColdMain=$false
Require (-not (Project-Report $r).accepted)
$r.milestones.ColdMain=$true; $r.remaining=1
Require (-not (Project-Report $r).accepted)
$r.remaining=0; $r.reason='unsafe synthetic sentinel'
Reject { Project-Report $r }
$r.reason='None'; $r.extra='unsafe synthetic sentinel'
Reject { Project-Report $r }
$r=New-Report 'short_correction'
Require ((Required-Milestones $r.case) -contains 'NoDatabaseAfterShort')
Require ((Required-Milestones $r.case) -contains 'ShortWarningAcknowledged')
$r=New-Report 'recovery_cancel'
Require ((Required-Milestones $r.case) -contains 'RecoveryPreserved')
Reject { New-Report 'weak_short' }
foreach($path in @('../x','a/../x','a\\x','/x','a//x','C:x','a/./x','a/')) { Reject { Assert-Manifest @([pscustomobject]@{path=$path;sha256=('a'*64);bytes=1}) } }
Reject { Assert-Manifest @([pscustomobject]@{path='A';sha256=('a'*64);bytes=1},[pscustomobject]@{path='a';sha256=('a'*64);bytes=1}) }
Assert-Manifest @([pscustomobject]@{path='a/b.dll';sha256=('a'*64);bytes=1})
Require ((Get-FileDigest $PSCommandPath) -cmatch '^[0-9a-f]{64}$')
# Reject coercible/noninteger scalar types and extra manifest properties.
foreach($invalid in @($true,1.25,'1')) {
    $bad=New-Report 'valid_keyboard'; $bad.remaining=$invalid
    Reject { Project-Report $bad }
    $bad=New-Report 'valid_keyboard'; $bad.schema=$invalid
    Reject { Project-Report $bad }
    Reject { Assert-Manifest @([pscustomobject]@{path='x';sha256=('a'*64);bytes=$invalid}) }
}
Reject { Assert-Manifest @([pscustomobject]@{path='x';sha256=('a'*64);bytes=1;extra='rejected'}) }
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('native-json-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    $target=Join-Path $scratch 'rewrite.json'
    Write-Json $target ([ordered]@{revision=1})
    Require ((Read-BoundedJson $target).revision -eq 1)
    Write-Json $target ([ordered]@{revision=2})
    Require ((Read-BoundedJson $target).revision -eq 2)
    Require (-not [IO.File]::Exists($target+'.pending'))
} finally { [IO.Directory]::Delete($scratch,$true) }
# Every declared case/milestone is required independently, not merely a passing aggregate.
$binding=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
foreach($caseName in $Cases) {
    $record=New-Report $caseName;$record.binding=$binding
    $record.phase='Complete';$record.category='None';$record.reason='None';$record.completed=$true;$record.remaining=0
    foreach($name in (Required-Milestones $caseName)){$record.milestones.$name=$true}
    Require ((Project-Report $record).accepted)
    foreach($name in (Required-Milestones $caseName)){
        $record.milestones.$name=$false;Require (-not (Project-Report $record).accepted);$record.milestones.$name=$true
    }
    $record.remaining=-1;Require (-not (Project-Report $record).accepted);$record.remaining=0
    $record.binding=$null
    if($caseName -cne 'preflight'){Require (-not (Project-Report $record).accepted)}
    Reject {Project-Report (Project-Report $record)}
}
'PASS report milestone completeness, fail-closed projection, manifest validation, BCL streaming hash, actual Write-Json twice'
