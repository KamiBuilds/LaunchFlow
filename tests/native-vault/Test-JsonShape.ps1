# Root-array compatibility controls; modeled conversion is not Windows execution.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
. (Join-Path $PSScriptRoot 'Ownership.ps1')
function Require-Shape([bool]$Value,[string]$Name) { if(-not $Value){throw ('JsonShapeAssertionFailed:'+ $Name)} }
function Reject-Shape([scriptblock]$Action) {
    $reason='None'
    try { $null=& $Action } catch { $reason=Safe-Reason $_ }
    Require-Shape ($reason -ceq 'ManifestRejected') 'strict-rejection-category'
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('json-shape-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$failures=0;$checks=0
try {
    $path=Join-Path $root 'value.json';$script:LedgerPath=$path
    $script:Exe=Join-Path $root 'synthetic-unused';$script:Installer=Join-Path $root 'synthetic-installer'
    $ledger=ConvertTo-Json -InputObject ([ordered]@{pid=2147483647;startTicks='111111111111111111';session=[Diagnostics.Process]::GetCurrentProcess().SessionId;path=$script:Exe}) -Compress
    $manifest='{"path":"synthetic.dll","sha256":"'+('a'*64)+'","bytes":1}'
    $provenance=[ordered]@{schema=1;sourceSha=('a'*40);archiveSha256=('a'*64);sourceBeforeSha256=('a'*64);sourceAfterSha256=('a'*64);runtimeManifestSha256=('a'*64);installerSha256=('a'*64);version='1.2.3';sourceFiles=1;runtimeFiles=1;builderExit=0}
    foreach($mode in @('actual-host','modeled-nonenumerating-boundary')) {
        if($mode -ceq 'modeled-nonenumerating-boundary') {
            # Deliberately omit NoEnumerate from the modeled command's capabilities.
            function ConvertFrom-Json {
                param([Parameter(ValueFromPipeline=$true)][string]$InputObject)
                process {
                    $value=Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $InputObject -NoEnumerate
                    return ,$value
                }
            }
            if(-not (Get-Command Microsoft.PowerShell.Utility\ConvertFrom-Json).Parameters.ContainsKey('NoEnumerate')) {
                Remove-Item Function:\ConvertFrom-Json
                'SKIP modeled-nonenumerating-boundary: host lacks NoEnumerate; actual-host controls still required'
                continue
            }
        }
        $cases=@(
            @{name='empty-root-array';text='[]';action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 0) 'empty-count';Require-Shape (@(Read-Ledger).Count -eq 0) 'empty-ledger';Reject-Shape {Assert-Manifest @(Read-BoundedJson $path)} }},
            @{name='single-ledger-record';text=('['+$ledger+']');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 1) 'single-count';Assert-Keys $entries[0] @('pid','startTicks','session','path');Require-Shape (@(Read-Ledger).Count -eq 1) 'single-ledger' }},
            @{name='multiple-ledger-records';text=('['+$ledger+','+$ledger+']');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 2) 'multiple-count';foreach($e in $entries){Assert-Keys $e @('pid','startTicks','session','path')};Require-Shape (@(Read-Ledger).Count -eq 2) 'multiple-ledger' }},
            @{name='scalar-provenance';text=(ConvertTo-Json -InputObject $provenance -Compress);action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 1) 'scalar-count';Assert-Keys $entries[0] @($provenance.Keys);$safe=Project-Provenance $entries[0];Require-Shape ($safe.sourceSha -ceq $provenance.sourceSha) 'source-binding';Reject-Shape {Read-Ledger} }},
            @{name='single-manifest-record';text=('['+$manifest+']');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 1) 'manifest-count';Assert-Manifest $entries }},
            @{name='multiple-manifest-records';text=('['+$manifest+','+$manifest.Replace('synthetic.dll','second.dll')+']');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 2) 'manifest-multiple-count';Assert-Manifest $entries }},
            @{name='nested-ledger-not-flattened';text=('[['+$ledger+']]');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 1 -and $entries[0] -is [array] -and $entries[0].Count -eq 1) 'nested-ledger-shape';Reject-Shape {Read-Ledger} }},
            @{name='nested-manifest-not-flattened';text=('[['+$manifest+']]');action={ $entries=@(Read-BoundedJson $path);Require-Shape ($entries.Count -eq 1 -and $entries[0] -is [array] -and $entries[0].Count -eq 1) 'nested-manifest-shape';Reject-Shape {Assert-Manifest @(Read-BoundedJson $path)} }},
            @{name='root-null-not-empty-ledger';text='null';action={ Reject-Shape {Read-Ledger};Reject-Shape {Read-BoundedJson $path} }},
            @{name='null-ledger-entry';text='[null]';action={ Reject-Shape {Read-Ledger} }},
            @{name='null-manifest-entry';text='[null]';action={ Reject-Shape {Assert-Manifest @(Read-BoundedJson $path)} }},
            @{name='empty-nested-ledger';text='[[]]';action={ Reject-Shape {Read-Ledger} }},
            @{name='empty-nested-manifest';text='[[]]';action={ Reject-Shape {Assert-Manifest @(Read-BoundedJson $path)} }}
        )
        $malformedLedgerIndex=0
        foreach($bad in @(
            $ledger.Replace('"pid":2147483647','"pid":true'),
            $ledger.Replace('"pid":2147483647','"pid":"2147483647"'),
            $ledger.Replace('"pid":2147483647','"pid":1.25'),
            $ledger.Replace('"pid":2147483647,',''),
            $ledger.Replace('"pid":2147483647','"extra":1,"pid":2147483647')
        )) { $cases+=@{name=('malformed-ledger-'+$malformedLedgerIndex);text=('['+$bad+']');action={Reject-Shape {Read-Ledger}}};$malformedLedgerIndex++ }
        $malformedManifestIndex=0
        foreach($bad in @(
            $manifest.Replace('"bytes":1','"bytes":true'),$manifest.Replace('"bytes":1','"bytes":"1"'),
            $manifest.Replace('"bytes":1','"bytes":1.25'),$manifest.Replace(',"bytes":1',''),
            $manifest.Replace('"bytes":1','"bytes":1,"extra":1')
        )) { $cases+=@{name=('malformed-manifest-'+$malformedManifestIndex);text=('['+$bad+']');action={Reject-Shape {Assert-Manifest @(Read-BoundedJson $path)}}};$malformedManifestIndex++ }
        $cases+=@{name='ledger-record-limit';text=('['+((@($ledger)*17) -join ',')+']');action={Reject-Shape {Read-Ledger}}}
        $cases+=@{name='byte-limit';text='{}';action={Reject-Shape {Read-BoundedJson $path 1}}}
        foreach($case in $cases) {
            [IO.File]::WriteAllText($path,$case.text)
            $checks++
            try { & $case.action; 'PASS '+$mode+' '+$case.name }
            catch { $failures++;'FAIL '+$mode+' '+$case.name+' '+$_.Exception.Message }
        }
        if($mode -ceq 'modeled-nonenumerating-boundary'){Remove-Item Function:\ConvertFrom-Json}
    }
    'JSON shape checks='+$checks+' failures='+$failures+' host='+$PSVersionTable.PSVersion.ToString()
    Require-Shape ($failures -eq 0) 'all-controls'
} finally { [IO.Directory]::Delete($root,$true) }
