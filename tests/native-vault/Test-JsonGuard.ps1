$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Contracts.ps1')
function Require([bool]$v) { if(-not $v){throw 'JsonGuardAssertionFailed'} }
$root=Join-Path ([IO.Path]::GetTempPath()) ('json-guard-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $path=Join-Path $root 'value.json'
    foreach($text in @('{"a":1,"a":2}','{"a":1,"\u0061":2}','{"nested":{"A":1,"a":2}}','{"a":1} garbage')) {
        [IO.File]::WriteAllText($path,$text)
        $refused=$false;try {$null=Read-BoundedJson $path} catch {$refused=$true}; Require $refused
    }
    [IO.File]::WriteAllText($path,'{"a":[{"a":1},{"a":2}],"b":"a\\b\"c"}')
    Require ((Read-BoundedJson $path).a.Count -eq 2)
    'PASS strict JSON duplicate/escaped/case-insensitive key rejection and valid nested control'
} finally {[IO.Directory]::Delete($root,$true)}
